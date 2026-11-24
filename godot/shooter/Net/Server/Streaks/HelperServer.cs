using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Sentries;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// The helicopter helper on the server (killstreak "helicopter_helper"): the bridge between the pure rules in ShooterCore
/// (<see cref="HelicopterHelperStreak"/>) and the match. It spawns the helicopter over the map centre (the average of the spawn
/// points), tells a targeted player only (the red-dot warning), and sends each round as a tracer (the shared Shot event) with
/// its damage through the normal damage path, so the kill goes to the owner.
/// </summary>
internal sealed class HelperServer : IHelperWorld
{
    readonly ServerContext _ctx;
    readonly KillstreakFile _killstreaks;
    readonly WeaponsFile _weapons;
    readonly Func<string, ushort?> _spawnOverMap;
    readonly TargetBook _targets;
    readonly SentryWorld _sentries;
    readonly DealDamage _deal;
    readonly ImpactFx _impacts;
    readonly Random _rng;
    readonly List<HelperCandidate> _enemies = new();
    int _weaponIndex;

    public HelperServer(ServerContext ctx, KillstreakFile killstreaks, WeaponsFile weapons, Func<string, ushort?> spawnOverMap, TargetBook targets, SentryWorld sentries, DealDamage deal, ImpactFx impacts, Random rng)
    {
        _ctx = ctx; _killstreaks = killstreaks; _weapons = weapons; _spawnOverMap = spawnOverMap; _targets = targets; _sentries = sentries; _deal = deal; _impacts = impacts; _rng = rng;
    }

    /// <summary>The streak, built against this server; the streak's random source is the helper's own.</summary>
    public HelicopterHelperStreak Streak()
    {
        var tuning = _killstreaks.Find(HelicopterHelperStreak.StreakId)?.Helper
            ?? throw new InvalidOperationException("killstreaks.json: helicopter_helper has no helper tuning");
        _weaponIndex = _weapons.Weapons.FindIndex(w => w.Id == tuning.WeaponId);
        if (_weaponIndex < 0) throw new InvalidOperationException($"killstreaks.json: helicopter helper names unknown weapon \"{tuning.WeaponId}\"");
        return new HelicopterHelperStreak(tuning, this, _rng.NextDouble);
    }

    // ---- IHelperWorld ----

    /// <summary>Spawns the helicopter circling the middle of the spawn points, at a random phase and direction. Owned by nobody (no control).</summary>
    public ushort? SpawnHelicopter() => _spawnOverMap("helicopter");

    public void Despawn(ushort entityId) => _ctx.Match.Entities.Despawn(entityId, WorldDespawnReason.Removed);

    public NVec3? Helicopter(ushort entityId) => _ctx.Match.Entities.StateOf(entityId)?.Position;

    /// <summary>Every live player except the owner and the owner's teammates, aimed at chest height, with the stance that sets their exposure.</summary>
    public IReadOnlyList<HelperCandidate> Enemies(ushort owner)
    {
        // one buffer for every helper: its gunner reads the enemies inside its own tick and keeps none (HelicopterGunner)
        _enemies.Clear();
        var targets = _targets.Live();
        foreach (var t in targets)
            if (BotEnemies.IsEnemy(_ctx.Match, owner, t.Id) && !NonEnemyTargets.IsNonEnemy(t.Id)) _enemies.Add(new HelperCandidate(t.Id, t.Feet + NVec3.UnitY * SentryWorld.AimHeightM, t.Stance));
        _targets.Return(targets);
        return _enemies;
    }

    /// <summary>True when no world surface lies between the two points (the same test as a turret's sight line).</summary>
    public bool Clear(NVec3 from, NVec3 to)
    {
        var d = NVec3.Distance(from, to);
        if (d <= 0) return true;
        var hit = _sentries.WorldRay(from, (to - from) / d, d);
        return hit is not { } h || NVec3.Distance(from, h.Point) >= d - 0.05f;
    }

    /// <summary>The red-dot warning, to the targeted player only.</summary>
    public void Warn(ushort helicopter, ushort target, float seconds)
    {
        if (!_ctx.Players.TryGetValue(target, out var c)) return;
        _ctx.SendEvent(c, EventKind.HelperWarning, HelperWarningCodec.Encode(new HelperWarning(target, helicopter, seconds)));
    }

    /// <summary>One round: a tracer for everyone (the shared Shot event, from no player), and the damage on a hit.</summary>
    public void Round(ushort owner, ushort target, NVec3 from, NVec3 end, bool hit, float damage)
    {
        _ctx.Broadcast(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(0, (byte)_weaponIndex, from, end)));
        if (hit)
        {
            _impacts.Broadcast(end, ImpactSize.Puff, body: true); // a round that lands: a small burst at the body (ticket #154)
            _deal(owner, SpecialWeapons.Helper, target, damage, false, false);
        }
    }
}
