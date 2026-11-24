using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Harvest;

/// <summary>
/// What the machine does to people: the reel is the only part that harms (<see cref="HarvesterConfig.Reel"/>), a player on foot inside its box is drawn in while it moves forward (the body kills nobody: vehicles.json run_over is false). A body the
/// reel killed is not thrown but goes into the machine (<see cref="MachineCorpse"/>, a corpse owned by <see cref="HarvesterRules.BaleOwner"/>): spun round the reel at the front, swallowed with its Items, then thrown out of the unloader inside a bale.
/// </summary>
internal sealed class ReelServer
{
    readonly ServerContext _ctx;
    readonly HarvesterConfig _cfg;
    readonly Fleet _fleet;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly DeathPush _deathPush;
    readonly CorpseBook _corpses;
    readonly ItemsServer _gear;
    readonly WeaponsFile _weapons;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly BaleFire _bales;
    readonly Func<ServerCar, NVec3> _carVelocity;
    readonly float _hitCooldown;

    /// <summary>Players the harvester killed this tick, from the kill event to the corpse: their bodies go into the machine instead of being thrown.</summary>
    readonly Dictionary<ushort, ServerCar> _victims = new();
    readonly List<(MachineCorpse Machine, NVec3 From, ServerCar Car)> _machineCorpses = new();
    /// <summary>The Items of the victims inside the machine, by their corpse id, until the bale leaves the unloader.</summary>
    readonly Dictionary<ushort, List<ItemStack>> _machineItems = new();

    /// <param name="carVelocity">What a vehicle's velocity is for what leaves it (<c>NetServer.CarVelocity</c>).</param>
    /// <param name="hitCooldown">Seconds before the same body is hit by the same vehicle again (the run-over's).</param>
    public ReelServer(ServerContext ctx, HarvesterConfig cfg, Fleet fleet, TargetBook targets, DealDamage deal, DeathPush deathPush, CorpseBook corpses, ItemsServer gear, WeaponsFile weapons,
        Drycastle.MapLoader.MapLoader? map, BaleFire bales, Func<ServerCar, NVec3> carVelocity, float hitCooldown)
    {
        _ctx = ctx; _cfg = cfg; _fleet = fleet; _targets = targets; _deal = deal; _deathPush = deathPush; _corpses = corpses; _gear = gear; _weapons = weapons; _map = map; _bales = bales;
        _carVelocity = carVelocity; _hitCooldown = hitCooldown;
    }

    static NVec3 Pos(ServerCar car) => new(car.GlobalPosition.X, car.GlobalPosition.Y, car.GlobalPosition.Z);

    /// <summary>A new round: nobody is in the machine any more.</summary>
    public void Reset() { _victims.Clear(); _machineCorpses.Clear(); _machineItems.Clear(); }

    /// <summary>The credit of the machine's kills: its driver (the AI's, or a player who took the seat), else nobody.</summary>
    ushort Attacker(ServerCar car) => _fleet.OccupantAt(car, 0) is { } d && _ctx.IsAlive(d.Id) ? d.Id : HarvesterRules.NobodyAttacker;

    /// <summary>The reel is the only part that harms: a player on foot inside its box is drawn in while the machine moves forward.</summary>
    public void Contacts(ServerCar car)
    {
        if (car.State.Speed < _cfg.CutMinSpeedMps) return;
        var attacker = Attacker(car);
        var pos = Pos(car);
        var targets = _targets.Live();
        try
        {
            foreach (var t in targets)
            {
                if (!_ctx.Players.TryGetValue(t.Id, out var o) || o.Car != null || car.Seat.SeatOf(t.Id) >= 0) continue;
                if (!_cfg.Reel.Contains(pos, car.State.Yaw, t.Feet + NVec3.UnitY * 0.1f)) continue;
                if (car.HitCooldown.TryGetValue(t.Id, out var until) && _ctx.Now < until) continue;
                car.HitCooldown[t.Id] = _ctx.Now + _hitCooldown;
                _victims[t.Id] = car;
                var r = _deal(attacker, SpecialWeapons.Harvester, t.Id, _cfg.ReelDamage, false, false);
                if (r != DamageResult.Ignored) GD.Print($"server: the harvester's reel drew in #{t.Id} ({r})");
                else _victims.Remove(t.Id);
            }
        }
        finally { _targets.Return(targets); }
    }

    /// <summary>A kill event (<c>PlayerKilled</c>): when the harvester made it, the body is not thrown, it goes into the machine.</summary>
    public void OnKill(PlayerKilled k)
    {
        if (k.Weapon != SpecialWeapons.Harvester) return; // only the reel
        if (!_victims.TryGetValue(k.Victim, out var car) || car.Destroyed) { _victims.Remove(k.Victim); return; }
        _deathPush.Forget(k.Victim); // no ragdoll throw: the body lies where it fell until the machine draws it in
    }

    /// <summary>A corpse was made: when the harvester killed its player the corpse begins its way into the machine.</summary>
    public void OnCorpse(ushort victim, ushort corpseId, Corpse corpse)
    {
        if (!_victims.Remove(victim, out var car) || car.Destroyed) return;
        corpse.Attach(corpse.Position); // seated: it cannot be looted and is moved by the machine
        _machineCorpses.Add((new MachineCorpse(corpseId, _cfg, victim), corpse.Position, car));
        HarvestFx.Send(_ctx, HarvesterCodec.FxKind.Drawn, corpse.Position, corpseId, victim);
    }

    /// <summary>The corpses on their way into the machine: drawn in, swallowed, thrown out as a bale.</summary>
    public void Step(float dt)
    {
        if (_machineCorpses.Count == 0) return;
        for (var i = _machineCorpses.Count - 1; i >= 0; i--)
        {
            var (m, from, car) = _machineCorpses[i];
            var machineGone = car.Destroyed; // it blew up or was hidden: whatever is in it is lost, a body still outside is left where it is
            if (m.Phase == MachineCorpsePhase.Drawn)
            {
                var body = _corpses.Get(m.Corpse);
                if (body == null || machineGone) // taken by the cap or the round, or no machine any more
                {
                    if (body is { Seated: true }) body.Throw(NVec3.Zero);
                    _machineCorpses.RemoveAt(i);
                    continue;
                }
                var fwd0 = new NVec3(car.Forward.X, 0, car.Forward.Z);
                var intake0 = Pos(car) + fwd0 * _cfg.HeaderForwardM + NVec3.UnitY * _cfg.ReelAxisUpM; // the reel's axis: the clients spin the body round it
                var k = m.DrawFraction;
                body.Follow(NVec3.Lerp(from, intake0, k * k * (3f - 2f * k)));
            }
            else if (machineGone) { _machineCorpses.RemoveAt(i); _machineItems.Remove(m.Corpse); continue; }
            switch (m.Tick(dt))
            {
                case MachineCorpsePhase.Inside: // swallowed: the body is gone from the world, its Items ride inside
                    if (_corpses.Get(m.Corpse) is { } swallowed)
                    {
                        _machineItems[m.Corpse] = swallowed.Items.ToList();
                        HarvestFx.Send(_ctx, HarvesterCodec.FxKind.Swallowed, swallowed.Position, m.Corpse, m.Victim);
                        _ctx.Match.Entities.Despawn(m.Corpse, WorldDespawnReason.Removed);
                    }
                    break;
                case MachineCorpsePhase.Baled:
                    EjectBale(car, _machineItems.Remove(m.Corpse, out var items) ? items : new List<ItemStack>(), m.Victim);
                    _machineCorpses.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>
    /// The bale leaves the unloader (rear, on the left of the machine, <see cref="HarvesterConfig.UnloaderHeightM"/> up) with the victim's Items, thrown backwards and up on top of the machine's velocity
    /// (<see cref="HarvesterCatch.BaleVelocity"/>): it flies, lands and rolls to a stop (<see cref="Corpse.Launch"/> with <c>rolls</c>, flown with the other corpses). One entity, the body inside it
    /// is drawn by the clients (<paramref name="victim"/> rides in the effect). It lies for as long as a corpse does; its <see cref="HayBale"/> follows it (<see cref="BaleFire.FollowMade"/>).
    /// </summary>
    void EjectBale(ServerCar car, List<ItemStack> items, ushort victim)
    {
        var fwd = new NVec3(car.Forward.X, 0, car.Forward.Z);
        if (fwd.LengthSquared() < 1e-6f) fwd = -NVec3.UnitZ; else fwd = NVec3.Normalize(fwd);
        var at = HarvesterCatch.UnloaderAt(Pos(car), fwd, car.HalfLength, _cfg);
        var ground = _map?.GroundHeight(at.X, at.Z) ?? float.NaN;
        if (!float.IsNaN(ground)) at.Y = MathF.Max(at.Y, ground + 0.2f); // never spawned inside the ground
        var knife = Math.Max(0, _weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee)); // a weapon nobody can swap into a hotbar position: the bale is for its Items
        var bale = new Corpse(HarvesterRules.BaleOwner, at, car.State.Yaw, knife, 0, items, _cfg.BaleLifetimeS);
        bale.Launch(HarvesterCatch.BaleVelocity(fwd, _carVelocity(car), _cfg), rolls: true);
        var id = _corpses.Add(bale);
        if (id is not { } cid) return;
        if (items.Count > 0) _gear.BroadcastCorpseItems(cid);
        _bales.AddMade(cid, bale.Position);
        HarvestFx.Send(_ctx, HarvesterCodec.FxKind.BaleOut, at, cid, victim);
        GD.Print($"server: harvester bale {cid} of #{victim} thrown from ({at.X:F1},{at.Y:F1},{at.Z:F1}) with {items.Count} items");
    }
}
