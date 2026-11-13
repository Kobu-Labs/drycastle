using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The helicopter helper on the server (killstreak "helicopter_helper"): the bridge between the pure rules in ShooterCore
/// (<see cref="HelicopterHelperStreak"/>) and the match. It spawns the helicopter over the map centre (the average of the spawn
/// points), tells a targeted player only (the red-dot warning), and sends each round as a tracer (the shared Shot event) with
/// its damage through the normal damage path, so the kill goes to the owner.
/// </summary>
public partial class NetServer
{
    /// <summary>The helicopter helper's streak, built once by <see cref="BuildStreaks"/>; null when the table has none.</summary>
    HelicopterHelperStreak? _helper;

    /// <summary>Weapons.json index of the helper's rounds (its tracer and sound), resolved when the streak is built.</summary>
    int _helperWeaponIndex;

    readonly Random _helperRng = new();

    /// <summary>Builds the streak against this server. Called from <see cref="BuildStreaks"/>.</summary>
    IKillstreak MakeHelicopterHelper()
    {
        var tuning = Killstreaks.Find(HelicopterHelperStreak.StreakId)?.Helper
            ?? throw new InvalidOperationException("killstreaks.json: helicopter_helper has no helper tuning");
        _helperWeaponIndex = Weapons.Weapons.FindIndex(w => w.Id == tuning.WeaponId);
        if (_helperWeaponIndex < 0) throw new InvalidOperationException($"killstreaks.json: helicopter helper names unknown weapon \"{tuning.WeaponId}\"");
        _helper = new HelicopterHelperStreak(tuning, new HelperWorld(this), _helperRng.NextDouble);
        return _helper;
    }

    /// <summary>The helper's view of the server.</summary>
    sealed class HelperWorld : IHelperWorld
    {
        readonly NetServer _s;
        public HelperWorld(NetServer server) => _s = server;

        public ushort? SpawnHelicopter() => _s.SpawnHelicopterOverMap();
        public void Despawn(ushort entityId) => _s._match.Entities.Despawn(entityId, WorldDespawnReason.Removed);
        public NVec3? Helicopter(ushort entityId) => _s.HelicopterPosition(entityId);
        public IReadOnlyList<HelperCandidate> Enemies(ushort owner) => _s.HelperEnemies(owner);
        public bool Clear(NVec3 from, NVec3 to) => _s.HelperClear(from, to);
        public void Warn(ushort helicopter, ushort target, float seconds) => _s.SendHelperWarning(helicopter, target, seconds);
        public void Round(ushort owner, ushort target, NVec3 from, NVec3 end, bool hit, float damage) => _s.HelperRound(owner, target, from, end, hit, damage);
    }

    /// <summary>Spawns the helicopter circling the middle of the spawn points, at a random phase and direction. Owned by nobody (no control).</summary>
    ushort? SpawnHelicopterOverMap()
    {
        var centre = NVec3.Zero;
        if (Spawns.Count > 0) centre = Spawns.Aggregate(NVec3.Zero, (a, p) => a + p) / (float)Spawns.Count;
        var ground = GroundBelow(centre) ?? centre.Y;
        var at = new NVec3(centre.X, ground, centre.Z);
        var phase = (float)(_helperRng.NextDouble() * Math.Tau);
        return SpawnAircraft("helicopter", at, phase, _helperRng.Next(2) == 0, 0);
    }

    NVec3? HelicopterPosition(ushort entityId) => _match.Entities.StateOf(entityId)?.Position;

    /// <summary>Every live player except the owner, aimed at chest height, with the stance that sets their exposure.</summary>
    IReadOnlyList<HelperCandidate> HelperEnemies(ushort owner)
    {
        // one buffer for every helper: its gunner reads the enemies inside its own tick and keeps none (HelicopterGunner)
        _helperEnemies.Clear();
        var targets = LiveTargets();
        foreach (var t in targets)
            if (t.Id != owner) _helperEnemies.Add(new HelperCandidate(t.Id, t.Feet + NVec3.UnitY * TurretAimHeightM, t.Stance));
        ReturnTargets(targets);
        return _helperEnemies;
    }

    readonly List<HelperCandidate> _helperEnemies = new();

    /// <summary>True when no world surface lies between the two points (the same test as a turret's sight line).</summary>
    bool HelperClear(NVec3 from, NVec3 to)
    {
        var d = NVec3.Distance(from, to);
        if (d <= 0) return true;
        var hit = TurretRay(from, (to - from) / d, d);
        return hit is not { } h || NVec3.Distance(from, h.Point) >= d - 0.05f;
    }

    /// <summary>The red-dot warning, to the targeted player only.</summary>
    void SendHelperWarning(ushort helicopter, ushort target, float seconds)
    {
        if (!_byId.TryGetValue(target, out var c)) return;
        Send(c, new EventMsg((byte)EventKind.HelperWarning, HelperWarningCodec.Encode(new HelperWarning(target, helicopter, seconds))));
    }

    /// <summary>One round: a tracer for everyone (the shared Shot event, from no player), and the damage on a hit.</summary>
    void HelperRound(ushort owner, ushort target, NVec3 from, NVec3 end, bool hit, float damage)
    {
        BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(0, (byte)_helperWeaponIndex, from, end)));
        if (hit)
        {
            BroadcastImpact(end, ImpactSize.Puff, body: true); // a round that lands: a small burst at the body (ticket #154)
            Deal(owner, SpecialWeapons.Helper, target, damage, false, false);
        }
    }
}
