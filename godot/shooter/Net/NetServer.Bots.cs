using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Bots on the server (#214): players without a connection. A bot is a <see cref="Client"/> with no peer and a
/// <see cref="BotBrain"/>; each tick the brain's command is queued as the bot's input, and from there it is a player like
/// any other: the same movement step, weapons, damage, corpses, kill feed and snapshots, and clients need no change to see
/// it. This file is the bridge between the pure brain in ShooterCore and the match: it keeps the roster filled, answers
/// the brain's questions (who is alive, is the line clear) and tells it when it was hit.
/// <para>
/// Rules: bots are in the match only while a human is (an empty server runs no bot rounds). They count as players for
/// <c>min_players</c>, the score and the round end. They earn no XP and have no stats, and killing one grants no XP and
/// writes no kill stat; the streak counter does advance. They do not use vehicles or killstreaks, and they ignore target
/// dummies and the citizens.
/// </para>
/// <para>
/// Moving: each bot heads for the nearest other living player (<see cref="BotGoal"/>) along a route from
/// <see cref="BotNav"/>, the navigation mesh baked from the map colliders at start. Routes cost milliseconds, so one is
/// answered per tick, the bots taking turns. If the mesh cannot be built the bots stand where they spawn.
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>--bots N: how many bots join while at least one human is in the match. 0 = none.</summary>
    public int Bots;

    /// <summary>Most bots a server takes (they need no peer slot, so this is apart from <see cref="MaxPeers"/>).</summary>
    public const int MaxBots = ChatCommands.MaxBots;

    static readonly string[] BotNames =
    {
        "Adam", "Boris", "Cyril", "Dano", "Emil", "Fero", "Gabo", "Hana", "Ivan", "Jano", "Karol", "Lenka",
        "Milan", "Nora", "Oto", "Palo", "Rasto", "Sona", "Tomas", "Ula", "Viera", "Zuza", "Marek", "Petra",
        "Juro", "Eva", "Miro", "Dasa", "Stano", "Iva", "Lubo", "Olga",
    };

    /// <summary>Every living player as the bots see them, rebuilt once a tick and shared by all the brains.</summary>
    readonly List<BotContact> _botContacts = new();
    BotWorld? _botWorld;

    sealed class BotWorld : IBotWorld
    {
        readonly NetServer _s;
        public BotWorld(NetServer server) => _s = server;
        public IReadOnlyList<BotContact> Enemies(ushort self) => _s._botContacts; // the shared list: the brain skips itself
        public bool Clear(NVec3 from, NVec3 to) => _s.BotLineClear(from, to);
        public NVec3? Goal(ushort self) => _s.BotGoal(self);
    }

    bool IsBot(ushort id) => _byId.TryGetValue(id, out var c) && c.IsBot;

    /// <summary>The map folder (for the navigation bake: its manifest lists the chunks) and where baked tiles are kept. Set by the host.</summary>
    public string MapDir = "";
    public string NavCacheDir = "";
    BotNav? _nav;
    int _navTurn, _navRoutes;
    double _navMs, _navMaxMs, _navReportAt;

    bool _navTried;

    /// <summary>
    /// Once the colliders are in (at start, or when an admin first asks for bots): the mesh the bots walk on. Only when bots are
    /// wanted, and not in the smoke arenas (far from the map). Tried once; it blocks for the bake, so a live call warns first.
    /// </summary>
    void BuildBotNav(bool force = false)
    {
        if (_navTried || (Bots <= 0 && !force) || ArenaMode || MapDir == "") return;
        _navTried = true;
        try
        {
            _nav = BotNav.Build(World, MapDir, NavCacheDir, Math.Max(1, System.Environment.ProcessorCount / 2));
            GD.Print($"server: bot navigation from {_nav.Source}: {_nav.Polygons} polygons in {_nav.Tiles} tiles, {_nav.BuildSeconds:F1} s");
        }
        catch (Exception e)
        {
            GD.PrintErr($"server: bot navigation failed, the bots will stand where they spawn: {e.Message}");
            _nav = null;
        }
    }

    /// <summary>Where the action is for a bot: the nearest other living player. Null when it is alone.</summary>
    NVec3? BotGoal(ushort self)
    {
        NVec3? best = null;
        var bestD = float.PositiveInfinity;
        NVec3 me = default;
        foreach (var c in _botContacts) if (c.Id == self) { me = c.Feet; break; }
        foreach (var c in _botContacts)
        {
            if (c.Id == self) continue;
            var d = NVec3.DistanceSquared(c.Feet, me);
            if (d < bestD) { bestD = d; best = c.Feet; }
        }
        return best;
    }

    /// <summary>Answers one waiting route request, the bots in turn, and logs the cost of the routes every ten seconds.</summary>
    void ServeBotRoute(List<Client> bots)
    {
        if (_nav == null) return;
        for (var k = 0; k < bots.Count; k++)
        {
            var b = bots[(_navTurn + k) % bots.Count];
            if (!b.BotWasAlive || b.Brain!.PathRequest is not { } want) continue;
            _navTurn = (_navTurn + k + 1) % bots.Count;
            var t0 = Time.GetTicksUsec();
            var route = _nav.Route(new Vector3(want.From.X, want.From.Y, want.From.Z), new Vector3(want.To.X, want.To.Y, want.To.Z));
            var ms = (Time.GetTicksUsec() - t0) / 1000.0;
            b.Brain.SetPath(route.Select(N).ToList(), want.To, _elapsed);
            _navRoutes++; _navMs += ms; _navMaxMs = Math.Max(_navMaxMs, ms);
            break;
        }
        if (_elapsed < _navReportAt) return;
        if (_navRoutes > 0) GD.Print($"server: bots {bots.Count}, routes {_navRoutes} in 10 s, mean {_navMs / _navRoutes:F1} ms, max {_navMaxMs:F1} ms");
        // what each bot is doing, and how often it has been blocked this life: the first thing to read when bots misbehave
        GD.Print($"server: bots: {string.Join(", ", bots.Select(b => b.BotWasAlive ? $"{b.Name[4..]} {b.Brain!.Status}{(b.Brain.Stuck > 0 ? $" stuck x{b.Brain.Stuck}" : "")}" : $"{b.Name[4..]} dead"))}");
        _navReportAt = _elapsed + 10; _navRoutes = 0; _navMs = _navMaxMs = 0;
    }

    /// <summary>Nothing that stops a bullet between the two points: world, vehicles and shot-only cover, as for a shot. Players do not block.</summary>
    bool BotLineClear(NVec3 from, NVec3 to)
    {
        var d = to - from;
        var len = d.Length();
        if (len < 0.01f) return true;
        var dir = d / len;
        return WorldDistance(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len) >= len - 0.05f;
    }

    /// <summary>Once a tick, before the players are simulated: keeps the roster at the wanted size and queues one input per living bot.</summary>
    void BotsTick(float dt)
    {
        if (Bots <= 0 && _byId.Values.All(c => !c.IsBot)) return; // no bots wanted and none to send away: the usual server
        var bots = _byId.Values.Where(c => c.IsBot).ToList();
        var humans = _byId.Count - bots.Count;
        var want = humans > 0 ? Math.Min(Bots, MaxBots) : 0;
        if (bots.Count < want) AddBot(); // one a tick: a join builds a body
        else if (bots.Count > want)
        {
            var gone = bots[^1];
            bots.RemoveAt(bots.Count - 1);
            GD.Print($"server: bot {gone.Name} #{gone.Id} leaves");
            Leave(gone);
        }
        if (bots.Count == 0) return;

        _botContacts.Clear();
        foreach (var o in _byId.Values)
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive)
                _botContacts.Add(new BotContact(o.Id, N(o.Body.GlobalPosition), o.Body.State.Stance, _match.IsProtected(o.Id)));
        _botWorld ??= new BotWorld(this);

        foreach (var b in bots)
        {
            var alive = _match.Get(b.Id)?.Life == PlayerLife.Alive;
            if (alive && !b.BotWasAlive) b.Brain!.Reset((float)(_rng.NextDouble() * Math.Tau), _elapsed); // a new life: no memory of the last one
            b.BotWasAlive = alive;
            if (!alive) continue; // dead or waiting for the round: no input, as a dead player's is ignored
            var w = b.W;
            var self = new BotSelf(b.Id, N(b.Body.GlobalPosition), b.Body.State.Stance, w.Mag(w.Slot), w.Current.MagazineSize, w.Reserve(w.Slot), w.Reloading, w.Current.FullAuto);
            var cmd = b.Brain!.Tick(_elapsed, dt, self, _botWorld);
            var move = new MoveInput { MoveX = cmd.MoveX, MoveY = cmd.MoveY, Yaw = cmd.Yaw, Buttons = cmd.Buttons, SpeedScale = 1f };
            b.Inputs.Enqueue(NetInput.From(++b.BotSeq, move, cmd.Pitch, 0)); // slot 0: the weapon it spawned with
        }
        ServeBotRoute(bots);
    }

    /// <summary>A new bot: an id, a free name, a guest profile with a random starter loadout, then the same join as a player.</summary>
    void AddBot()
    {
        var id = _ids.Allocate(i => _clients.Values.Any(o => o.Id == i) || _byId.ContainsKey(i));
        if (id == 0) return;
        var taken = _byId.Values.Select(c => c.Name).ToHashSet();
        var name = BotNames.Select(n => "Bot " + n).FirstOrDefault(n => !taken.Contains(n)) ?? $"Bot {id}";
        var c = new Client { Id = id, Name = name, ConnectedAt = _elapsed, Brain = new BotBrain(BotTuning.Default, id) };
        var profile = GuestProfile();
        profile.Active = _rng.Next(Loadout.SlotCount);
        AttachProfile(c, profile);
        Join(c);
    }

    /// <summary>A bot that takes damage from another player looks toward where it came from.</summary>
    void BotHit(ushort victim, ushort attacker)
    {
        if (victim == attacker || !_byId.TryGetValue(victim, out var v) || v.Brain == null || !_byId.TryGetValue(attacker, out var a)) return;
        v.Brain.Alert(N(a.Body.GlobalPosition), _elapsed);
    }

    /// <summary>One log line per kill a bot made or suffered, so a server log shows what the bots do.</summary>
    void BotKillLog(PlayerKilled k)
    {
        if (k.Killer == k.Victim || (!IsBot(k.Killer) && !IsBot(k.Victim))) return;
        string Who(ushort id) => _byId.TryGetValue(id, out var c) ? c.Name : $"#{id}";
        GD.Print($"server: {Who(k.Killer)} killed {Who(k.Victim)} (weapon {WeaponKey(k.Weapon)}, {k.Cause})");
    }
}
