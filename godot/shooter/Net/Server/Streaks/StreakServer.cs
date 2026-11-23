using Godot;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Uav;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Turrets;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// Killstreaks on the server (ticket #118): the streak counter and loadout streaks per life, activation on the Killstreak key's press edge, and the owner-only streak events. The rules live in
/// <see cref="KillstreakService"/> (<see cref="Rules"/>); this class feeds it the server's events and owns the UAV (issue #121: <see cref="UavStreak"/> decides when pulses go out, this supplies the living
/// players and their Ghost-style visibility and sends each pulse to the owner alone; its aircraft (#303) is an ownerless <see cref="Aircraft"/> over the map, so no client mistakes it for a gunship to steer)
/// and the helicopter helper's clock. Each streak's real behaviour is an adapter of its own in this folder (see <see cref="IKillstreak"/>).
/// </summary>
internal sealed class StreakServer
{
    readonly ServerContext _ctx;
    readonly Random _rng = new();
    readonly GunshipServer _gunships;
    readonly Func<IReadOnlyList<NVec3>> _spawns;
    readonly Func<NVec3, float?> _groundBelow;
    readonly UavStreak _uav;
    HelicopterHelperStreak? _helper;

    /// <param name="make">The streak adapter of a table entry, or null for one that has none yet (it keeps its logging stub). The UAV is the server's own.</param>
    /// <param name="spawns">The map's spawn points: the aircraft over the map circle their middle.</param>
    /// <param name="groundBelow">The height of the ground under a point.</param>
    public StreakServer(ServerContext ctx, KillstreakFile killstreaks, GunshipServer gunships, Func<IReadOnlyList<NVec3>> spawns, Func<NVec3, float?> groundBelow, Func<StreakServer, string, IKillstreak?> make)
    {
        _ctx = ctx; _gunships = gunships; _spawns = spawns; _groundBelow = groundBelow;
        _uav = new UavStreak(UavContacts, (owner, kind, payload) => { if (ctx.Players.TryGetValue(owner, out var c)) ctx.SendEvent(c, kind, payload); },
            () => SpawnOverMap(UavStreak.AircraftKind), id => ctx.Match.Entities.Despawn(id, WorldDespawnReason.Removed));
        Rules = new KillstreakService(
            killstreaks,
            killstreaks.Streaks.Select(s =>
            {
                var streak = s.Id == UavStreak.StreakId ? _uav : make(this, s.Id) ?? new LoggingKillstreak(s.Id);
                if (streak is HelicopterHelperStreak helper) _helper = helper;
                return streak;
            }),
            (owner, kind, payload) => { if (ctx.Players.TryGetValue(owner, out var c)) ctx.SendEvent(c, (EventKind)kind, payload); },
            s => GD.Print($"server: {s}"));
    }

    /// <summary>The streak rules: the counter, the held streaks, the controls (<see cref="KillstreakService.ControlOf"/>), the sandbox's hold.</summary>
    public KillstreakService Rules { get; }

    /// <summary>Living players with their UAV visibility (the perk's <c>uav_visibility</c>; 0 hides them), and the target dummies with the default one.</summary>
    IReadOnlyList<UavContact> UavContacts() =>
        _ctx.Players.Values.Where(o => _ctx.IsAlive(o.Id))
            .Select(o => new UavContact(o.Id, o.Body.GlobalPosition.X, o.Body.GlobalPosition.Z, o.Perks.UavVisibility, _ctx.Match.Get(o.Id)?.Team ?? Teams.None))
            .Concat(_ctx.Match.Targets.All.Where(d => d.Alive).Select(d => new UavContact(d.Id, d.Spot.Feet.X, d.Spot.Feet.Z, PerkModifiers.None.UavVisibility))).ToList();

    /// <summary>Spawns an aircraft of a kind circling the middle of the spawn points, at a random phase and direction. Owned by nobody (no control).</summary>
    public ushort? SpawnOverMap(string kindId)
    {
        var centre = NVec3.Zero;
        var spawns = _spawns();
        if (spawns.Count > 0) centre = spawns.Aggregate(NVec3.Zero, (a, p) => a + p) / (float)spawns.Count;
        var ground = _groundBelow(centre) ?? centre.Y;
        var at = new NVec3(centre.X, ground, centre.Z);
        var phase = (float)(_rng.NextDouble() * Math.Tau);
        return _gunships.Spawn(kindId, at, phase, _rng.Next(2) == 0, 0);
    }

    /// <summary>The random source the helper streak shares with the spawn over the map.</summary>
    public Random Rng => _rng;

    /// <summary>A life starts at spawn (join and every spawn, like the perk): the loadout's streaks and the perk's cost reduction.</summary>
    public void Arm(ServerClient c, Loadout l) => Rules.Arm(c.Id, new[] { l.Streak1, l.Streak2, l.Streak3 }, c.Perks.StreakCostReduction);

    /// <summary>The Killstreak key was pressed (its press edge): use the next held streak while the owner is alive.</summary>
    public void Activate(ServerClient c)
    {
        var p = c.Body.GlobalPosition;
        Rules.Activate(c.Id, _ctx.Now, new NVec3(p.X, p.Y, p.Z));
    }

    /// <summary>
    /// A kill for the Streak counter. Returns whether a streak's own weapon made it (the XP streak award). A streak's own
    /// weapon never advances the counter; the kill-feed id list (ticket #143) also covers the aircraft and the vehicle gun.
    /// </summary>
    public bool Kill(PlayerKilled k, string weaponKey)
    {
        var fromStreak = Rules.IsStreakWeapon(weaponKey);
        Rules.OnKill(k.Killer, k.Victim, fromStreak || StreakWeapons.Is(k.Weapon));
        return fromStreak;
    }

    /// <summary>The player died: the counter starts over, and a turret does not outlive its owner.</summary>
    public void Death(ushort victim)
    {
        Rules.OnDeath(victim);
        _ctx.Match.Entities.DespawnOwned(victim, TurretRules.TypeId, WorldDespawnReason.Removed);
    }

    /// <summary>The player moved to another team: their turrets and killstreak progress are dropped like at a death (a turret would else turn on its owner's new teammates).</summary>
    public void Wipe(ushort id)
    {
        Rules.Wipe(id);
        _ctx.Match.Entities.DespawnOwned(id, TurretRules.TypeId, WorldDespawnReason.Removed);
    }

    public void RoundReset() => Rules.Reset();

    public void Tick(float dt)
    {
        Rules.Tick(_ctx.Now);
        _uav.Tick(_ctx.Now);
        _helper?.Tick(dt);
    }

    public void Leave(ushort id)
    {
        Rules.Forget(id);
        _ctx.Match.Entities.DespawnOwned(id, TurretRules.TypeId, WorldDespawnReason.Removed);
    }
}
