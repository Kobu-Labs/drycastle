using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Drycastle.ShooterCore;

/// <summary>
/// Always-on scoped timers of the server tick (the "server: prof" log lines): how long the named parts of a tick took and how often they ran,
/// summed over the report period, plus the same for the slowest tick of the period. A scope is a timestamp at each end and two array adds: no
/// allocation, about 40 ns. Scopes nest (the time of an inner scope is also inside its outer one), so the columns do not sum to the tick.
/// The scopes belong to the game thread; the navigation worker has its own counter (<see cref="NavPath"/>).
/// </summary>
public static class Prof
{
    public enum S
    {
        // the parts of the match phase
        MatchUpdate, Prepare, Entities, Citizens, Pets, Wildlife, Events,
        // inside the entities: what the animals ask the world
        AnimalStep, AnimalBodyAhead, AnimalCrowd, AnimalPlayersNear, AnimalThreat, AnimalWatched, AnimalGround, AnimalSolid, AnimalFits,
        // players and bots
        PlayerMove, Fire, ShotTargets, LiveTargets, BotBrain, BotSight,
        // world queries, whoever asks
        WorldShot, NearestHull, PhysicsRay,
        // vehicles
        VehicleStep, VehicleContacts,
        // the parts of a player's move
        MoveHead, MoveLogic, MoveStepUp, MoveSlide, MoveBodies, MovePost,
        // the parts of a shot
        FireBulletHit, FireRaycast, FireMissCheck, FirePellets, FireDeal,
        // sending an event to every client, and the players line of the log
        BroadcastEvent, LogPlayers,
        // the parts of a target list: the players, the animals (wild and pets), and taking the armoured riders out
        TargetsPlayers, TargetsAnimals, TargetsArmoured,
        Count,
    }

    static readonly string[] Names =
    {
        "match_update", "prepare", "entities", "citizens", "pets", "wildlife", "events",
        "animal_step", "animal_body_ahead", "animal_crowd", "animal_players_near", "animal_threat", "animal_watched", "animal_ground", "animal_solid", "animal_fits",
        "player_move", "fire", "shot_targets", "live_targets", "bot_brain", "bot_sight",
        "world_shot", "nearest_hull", "physics_ray",
        "vehicle_step", "vehicle_contacts",
        "move_head", "move_logic", "move_step_up", "move_slide", "move_bodies", "move_post",
        "fire_bullet_hit", "fire_raycast", "fire_miss_check", "fire_pellets", "fire_deal",
        "broadcast_event", "log_players",
        "targets_players", "targets_animals", "targets_armoured",
    };

    const int N = (int)S.Count;
    static readonly long[] WinTicks = new long[N], WinCalls = new long[N], TickTicks = new long[N], WorstTicks = new long[N];
    /// <summary>Bytes the game thread allocated inside each scope in the period, and (per scope) the thread's count when the scope began (-1: not asked).</summary>
    static readonly long[] WinAlloc = new long[N], AllocAt = NewAllocAt();
    static long _lapAlloc;
    static long[] NewAllocAt() { var a = new long[N]; Array.Fill(a, -1L); return a; }
    static double _worstMs = -1;
    static long _navPathTicks, _navPathCalls;

    /// <summary>The name of a scope in the log.</summary>
    public static string Name(S s) => Names[(int)s];

    /// <summary>Start of a scope: the timestamp to hand to <see cref="End"/> or <see cref="Lap"/>. For a chain of laps (the bytes allocated are counted from here).</summary>
    public static long Begin()
    {
        _lapAlloc = GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Start of the scope <paramref name="s"/>, which also counts the bytes the thread allocates until its <see cref="End"/>. A scope does not nest in itself.</summary>
    public static long Begin(S s)
    {
        AllocAt[(int)s] = GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }

    /// <summary>End of a scope that began at <paramref name="t0"/>.</summary>
    public static void End(S s, long t0)
    {
        var d = Stopwatch.GetTimestamp() - t0;
        WinTicks[(int)s] += d; TickTicks[(int)s] += d; WinCalls[(int)s]++;
        var at = AllocAt[(int)s];
        if (at < 0) return;
        WinAlloc[(int)s] += GC.GetAllocatedBytesForCurrentThread() - at;
        AllocAt[(int)s] = -1;
    }

    /// <summary>Ends a scope and starts the next one: returns the timestamp the next <see cref="Lap"/> or <see cref="End"/> takes.</summary>
    public static long Lap(S s, long t0)
    {
        var now = Stopwatch.GetTimestamp();
        var d = now - t0;
        WinTicks[(int)s] += d; TickTicks[(int)s] += d; WinCalls[(int)s]++;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        WinAlloc[(int)s] += alloc - _lapAlloc;
        _lapAlloc = alloc;
        return now;
    }

    /// <summary>Start of a tick.</summary>
    public static void TickBegin() => Array.Clear(TickTicks);

    /// <summary>End of a tick that took <paramref name="tickMs"/>: true when it is the slowest of the period so far (its scopes are then kept).</summary>
    public static bool TickEnd(double tickMs)
    {
        if (tickMs <= _worstMs) return false;
        _worstMs = tickMs;
        Array.Copy(TickTicks, WorstTicks, N);
        return true;
    }

    /// <summary>The navigation worker: one path search took the time since <paramref name="t0"/>. Any thread.</summary>
    public static void NavPath(long t0) { Interlocked.Add(ref _navPathTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _navPathCalls); }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The scopes of the period as <c>name=ms_per_second|calls_per_second|kilobytes_allocated_per_second</c> (10 ms/s is 1 % of a core), scopes that never ran left out.
    /// Does not reset: <see cref="Reset"/> does, after the other lines are read.
    /// </summary>
    public static string Scopes(double seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        seconds = Math.Max(seconds, 1e-3);
        for (var i = 0; i < N; i++)
        {
            if (WinCalls[i] == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Names[i]).Append('=').Append((Ms(WinTicks[i]) / seconds).ToString("F2", inv)).Append('|').Append((WinCalls[i] / seconds).ToString("F0", inv))
              .Append('|').Append((WinAlloc[i] / 1000.0 / seconds).ToString("F0", inv));
        }
        return sb.ToString();
    }

    /// <summary>The scopes of the slowest tick of the period, in microseconds (<c>name=us</c>), those under 50 us left out.</summary>
    public static string Worst()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < N; i++)
        {
            var us = Ms(WorstTicks[i]) * 1000;
            if (us < 50) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Names[i]).Append('=').Append(us.ToString("F0", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The navigation worker's period: <c>path=ms_per_second|calls_per_second</c>. Reads and clears its counters.</summary>
    public static string Nav(double seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        seconds = Math.Max(seconds, 1e-3);
        long pt = Interlocked.Exchange(ref _navPathTicks, 0), pc = Interlocked.Exchange(ref _navPathCalls, 0);
        return $"path={(Ms(pt) / seconds).ToString("F2", inv)}|{(pc / seconds).ToString("F1", inv)}";
    }

    /// <summary>Start of the next period.</summary>
    public static void Reset()
    {
        Array.Clear(WinTicks); Array.Clear(WinCalls); Array.Clear(WorstTicks); Array.Clear(WinAlloc);
        _worstMs = -1;
    }
}
