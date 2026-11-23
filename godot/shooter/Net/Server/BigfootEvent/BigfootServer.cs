using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.BigfootEvent;

/// <summary>
/// The Bigfoot event on the server (issue #264, GLOSSARY "Event"). An admin starts it with <c>/event bigfoot</c>; the <see cref="Bigfoot"/> brain
/// (<c>ShooterCore/Events</c>) fights from then on, as a world entity of its own (not a bot) that the shots reach as a scaled <see cref="HitTarget"/>
/// (<see cref="Hit"/> counts the hits). It ends as a win (it dies: an "event won" line, XP, then its body is a corpse that carries the head)
/// or despawned (nobody came near for minutes, the admin cancelled it, the round ended): no reward. This is the lifecycle, the hit pipeline and the reward; the world the brain
/// stands in is <see cref="BigfootEnv"/> and <see cref="BigfootTerrain"/>, the reward body is <see cref="BigfootLoot"/>.
/// </summary>
internal sealed class BigfootServer
{
    readonly ServerContext _ctx;
    readonly BigfootFile _data;
    readonly EventBook _events;
    readonly BigfootTerrain _terrain;
    readonly Fleet _fleet;
    readonly DealDamage _deal;
    readonly DeathPush _deathPush;
    readonly Random _rng;
    readonly WeaponsFile _weapons;
    readonly BigfootLoot _loot;
    readonly MapAreas _areas;
    readonly HuntLogServer _hunts;
    readonly Func<GunshipServer> _gunships;
    readonly SmokeStats _smoke;
    readonly Func<ServerCar, int, Vector3> _seatPos;
    readonly Func<ushort, bool> _scores;
    readonly Action<IEnumerable<XpGrant>> _awardXp;
    readonly int _winXp;

    readonly BigfootLifecycle _life = new();
    Bigfoot? _bigfoot;
    ushort _id;
    BigfootEnv? _env;
    /// <summary>The Bigfoot's hit volume at the end of recent ticks, for the rewind of a shot (ADR 0013), and the tick it appeared.</summary>
    readonly PoseRing _poses = new();
    uint _lifeStart;
    /// <summary>Where it fell and how it lay: when its death clip is over (<see cref="BigfootStage.Dying"/> ends) the body is a corpse to loot there.</summary>
    NVec3 _fell;
    float _fellYaw;
    /// <summary>The living players and the throwable vehicles of this tick, refilled once a tick before the entities tick (<see cref="Prepare"/>).</summary>
    readonly List<BigfootPlayer> _players = new();
    readonly List<BigfootVehicle> _vehicles = new();

    /// <summary>
    /// Registers the Bigfoot's world entity types and follows the entities' end: made after the corpse book, which follows the same despawns.
    /// <paramref name="seatPos"/> is a seat of a vehicle in the world; <paramref name="scores"/> and <paramref name="awardXp"/> are the progression's.
    /// </summary>
    public BigfootServer(ServerContext ctx, BigfootFile data, EventBook events, BigfootTerrain terrain, Fleet fleet, DealDamage deal, DeathPush deathPush, Random rng, WeaponsFile weapons, BigfootLoot loot, MapAreas areas,
        HuntLogServer hunts, Func<GunshipServer> gunships, SmokeStats smoke, Func<ServerCar, int, Vector3> seatPos, Func<ushort, bool> scores, Action<IEnumerable<XpGrant>> awardXp, int winXp)
    {
        _ctx = ctx; _data = data; _events = events; _terrain = terrain; _fleet = fleet; _deal = deal; _deathPush = deathPush; _rng = rng; _weapons = weapons; _loot = loot; _areas = areas;
        _hunts = hunts; _gunships = gunships; _smoke = smoke; _seatPos = seatPos; _scores = scores; _awardXp = awardXp; _winXp = winXp;
        BigfootRules.Register(WorldEntityRegistry.Shared);
        _ctx.Match.Entities.Despawned += OnWorldDespawn;
    }

    // ---- the hit pipeline ----

    /// <summary>The Bigfoot as a hit target now (null when there is none or it is dead): the player's capsule at twice the size, its head guarded.</summary>
    public HitTarget? Target() => _bigfoot is { Alive: true } bf ? bf.HitVolume : null;

    /// <summary>The target where the shooter's screen showed it (<paramref name="view"/> is the rewind tick), else where it stands now.</summary>
    public HitTarget Rewound(HitTarget now, uint view) =>
        LagComp.TryTargetAt(_poses, view, _lifeStart, out var past) ? past : now;

    /// <summary>End of tick: the hit volume it ends the tick with, for the rewind.</summary>
    public void CapturePose() => _poses.Record(_ctx.Tick, _bigfoot is { Alive: true }, _bigfoot?.HitVolume ?? default);

    /// <summary>
    /// A shot, a pellet fan, a blast or a gun round named the Bigfoot. It has no hit points: the hit counts what its source weighs
    /// (<see cref="BigfootRules.Weight"/>: a bullet 1, an explosion or a vehicle weapon more, ramming nothing). The shooter gets the Hit event, so the marker
    /// shows; there is never a headshot. A hit with no weight is absorbed (<see cref="DamageResult.Ignored"/>).
    /// </summary>
    public DamageResult Hit(ushort attackerId, byte weaponIdx)
    {
        _deathPush.Forget(BigfootRules.TargetId);
        if (_bigfoot is not { Alive: true } bf || _ctx.Match.Phase != MatchPhase.Running) return DamageResult.Ignored;
        var source = BigfootRules.SourceOf(weaponIdx, _weapons.Weapons.Count, i => _weapons.Weapons[i].Kind == WeaponKind.Projectile);
        var weight = BigfootRules.Weight(_data.Hits, source);
        if (!(weight > 0)) return DamageResult.Ignored;
        var killed = bf.Hit(attackerId, weight);
        _smoke.HitsLanded++;
        var msg = new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(BigfootRules.TargetId, attackerId, weaponIdx, (ushort)Math.Clamp(weight, 1f, 255f), false, false, killed)));
        _ctx.Players.TryGetValue(attackerId, out var attacker);
        if (!_gunships().NoteHit(attackerId, false, killed) && attacker != null) _ctx.Send(attacker, msg);
        return killed ? DamageResult.Killed : DamageResult.Damaged;
    }

    // ---- the tick ----

    /// <summary>Once a tick before the entities tick: the living players and the vehicles it may throw, for the brain.</summary>
    public void Prepare()
    {
        _players.Clear(); _vehicles.Clear();
        if (_bigfoot == null) return;
        foreach (var c in _ctx.Players.Values)
            if (_ctx.IsAlive(c.Id)) _players.Add(new BigfootPlayer(c.Id, ToN(c.Body.GlobalPosition), c.Body.State.OnFloor, c.Car != null));
        foreach (var car in _fleet.Cars)
            if (!car.Destroyed && car.Thrown == null) _vehicles.Add(new BigfootVehicle(car.Id, ToN(car.GlobalPosition), car.Def.HalfLength));
    }

    /// <summary>A vehicle thrown by the Bigfoot flies its arc: the server places it, and its riders with it. It lands exactly where the throw was aimed.</summary>
    public void StepThrownVehicle(ServerCar car, float dt)
    {
        var t = car.Thrown!;
        var p = t.Step(dt);
        car.PlaceThrown(new Vector3(p.X, p.Y, p.Z), t.Yaw, t.VerticalSpeed);
        if (t.Done)
        {
            var landing = t.Landing;
            car.EndThrow(new Vector3(landing.X, landing.Y, landing.Z));
            Fx(BigfootFxKind.VehicleLand, ToN(car.GlobalPosition), car.Id);
        }
        car.PrevPos = car.TickEndPos = car.GlobalPosition;
        for (var s = 0; s < car.Seat.Count; s++) // the riders stay on their seats the whole way
        {
            if (_fleet.OccupantAt(car, s) is not { } o) continue;
            var at = _seatPos(car, o.CarSeat);
            o.Body.Teleport(at);
            _ctx.Match.SetPosition(o.Id, ToN(at));
        }
    }

    void Fx(BigfootFxKind kind, NVec3 at, ushort id = 0, byte extra = 0) =>
        _ctx.Broadcast(EventKind.BigfootFx, BigfootFxCodec.Encode(new BigfootFxMsg(kind, at, id, extra)));

    // ---- the lifecycle ----

    /// <summary>"/event bigfoot": spawns it at a baked point of a <c>bigfoot</c> zone, or, with none on the map, near a random living player (a dev fallback, said to the admin and in the log).</summary>
    public void Start(ServerClient admin)
    {
        if (_life.WhyNotStart(_events) is { } why) { _ctx.ChatTo(admin, ChatLineKind.Error, why); return; } // an event runs, or the last Bigfoot still lies where it fell
        if (_ctx.Match.Phase != MatchPhase.Running) { _ctx.ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        var living = _ctx.Players.Values.Where(o => _ctx.IsAlive(o.Id)).ToList();
        var spawn = _areas.PickEventSpawn(EventBook.Bigfoot);
        var fallback = false;
        string? nearName = null;
        NVec3 nearPos = default;
        if (spawn is not { } point)
        {
            // no bigfoot zone on this map: the dev fallback, a spot near a random living player (humans first)
            var pool = living.Where(o => !o.IsBot).ToList();
            if (pool.Count == 0) pool = living;
            if (pool.Count == 0) { _ctx.ChatTo(admin, ChatLineKind.Error, "The map has no bigfoot zone and no player is alive to spawn it near."); return; }
            var anchor = pool[_rng.Next(pool.Count)];
            var found = FallbackSpot(ToN(anchor.Body.GlobalPosition));
            if (found is not { } spot) { _ctx.ChatTo(admin, ChatLineKind.Error, "The map has no bigfoot zone, and no ground was found near a player for the fallback."); return; }
            spawn = spot; fallback = true; nearName = anchor.Name; nearPos = ToN(anchor.Body.GlobalPosition);
        }
        var at = spawn!.Value;
        _env = new BigfootEnv(_ctx, _data, _terrain, _fleet, _deal, _rng, _players, _vehicles);
        Prepare();
        var yaw = 0f;
        var nearest = living.OrderBy(o => Flat(ToN(o.Body.GlobalPosition), at)).FirstOrDefault();
        if (nearest != null) yaw = TurretMath.YawTo(at, ToN(nearest.Body.GlobalPosition));
        var participants = living.Count(o => Flat(ToN(o.Body.GlobalPosition), at) <= _data.Hits.ParticipantRadiusM);
        var bf = new Bigfoot(_data, _env, at, yaw, _rng.Next(), participants);
        bf.Died += OnDied;
        var id = _ctx.Match.Entities.Spawn(bf);
        if (id is not { } eid) { _ctx.ChatTo(admin, ChatLineKind.Error, "The world is full of entities; the Bigfoot could not be spawned."); return; }
        _bigfoot = bf; _id = eid;
        _life.Appeared();
        _lifeStart = _ctx.Tick;
        _events.Start(EventBook.Bigfoot, _ctx.Now);
        Fx(BigfootFxKind.Appear, bf.Position);
        _ctx.ChatToAll(ChatLineKind.System, "", $"{admin.Name} started the Bigfoot event. Something huge is moving through the village.");
        if (fallback) _ctx.ChatTo(admin, ChatLineKind.Info, $"The map has no bigfoot zone, so this is the dev fallback: it appeared {Flat(bf.Position, nearPos):0} m from {nearName}.");
        GD.Print($"server: Bigfoot started by {admin.Name} #{admin.Id} at ({bf.Position.X:F1},{bf.Position.Y:F1},{bf.Position.Z:F1}), {(fallback ? $"FALLBACK spawn near {nearName} (the map has no bigfoot zone)" : "a baked bigfoot zone point")}, hits needed {bf.HitsNeeded}, {participants} participants");
    }

    /// <summary>A random spot between the fallback distances from <paramref name="anchor"/> with ground under it, or null.</summary>
    NVec3? FallbackSpot(NVec3 anchor)
    {
        var f = _data.FallbackSpawn;
        for (var i = 0; i < 16; i++)
        {
            var a = (float)_rng.NextDouble() * MathF.Tau;
            var d = f.MinDistanceM + (float)_rng.NextDouble() * (f.MaxDistanceM - f.MinDistanceM);
            var x = anchor.X + MathF.Cos(a) * d; var z = anchor.Z + MathF.Sin(a) * d;
            if (_terrain.Ground(x, z, anchor.Y) is { } y) return new NVec3(x, y, z);
        }
        return null;
    }

    /// <summary>"/event current end" while the Bigfoot runs (the handler table): a win, for testing; it dies by the same path as a kill (<see cref="OnDied"/>).</summary>
    public void End(ServerClient c)
    {
        if (_bigfoot is not { } bf) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} ended the Bigfoot event as a win.");
        bf.Kill();
    }

    /// <summary>"/event current cancel" while the Bigfoot runs: gone, no reward.</summary>
    public void Cancel(ServerClient c)
    {
        if (_bigfoot is not { } bf) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} cancelled the Bigfoot event.");
        bf.Remove(WorldDespawnReason.Removed); // the manager despawns it on this tick; OnWorldDespawn ends the run
    }

    /// <summary>It fell: the event is won. The line, the XP for everyone credited, and the effect; the body lies out its death clip and then the tracks begin.</summary>
    void OnDied(Bigfoot bf)
    {
        _events.End(EventBook.Bigfoot, EventPhase.Won, _ctx.Now);
        _life.Fell();
        _fell = bf.Position; _fellYaw = bf.Yaw;
        Fx(BigfootFxKind.Death, bf.Position);
        var credited = bf.Credited.Where(id => _ctx.Players.ContainsKey(id)).ToList();
        var names = credited.Count == 0 ? "" : " " + string.Join(", ", credited.Select(id => _ctx.Players[id].Name).Take(8)) + (credited.Count > 8 ? " and others" : "") + " took part.";
        _ctx.ChatToAll(ChatLineKind.System, "", $"Event won: the Bigfoot is down!{names} Loot its body for what it left behind.");
        _hunts.RecordMystery(credited, Mysteries.Bigfoot, bf.Position); // the log: everyone credited has caught it
        _awardXp(credited.Where(_scores).Select(id => new XpGrant(id, _winXp, XpReason.EventWin)).ToList());
        GD.Print($"server: Bigfoot died (last hit by #{bf.Killer}), credited {string.Join(",", credited)}");
    }

    /// <summary>The Bigfoot entity left the world: after its death it is the reward's time, otherwise the event ended with no reward.</summary>
    void OnWorldDespawn(WorldDespawned w)
    {
        if (w.Id != _id || _bigfoot == null) return;
        var won = _life.Gone();
        _bigfoot = null; _id = 0; _env = null;
        if (won)
        {
            if (w.Reason != WorldDespawnReason.Removed) _loot.SpawnBody(_fell, _fellYaw); // a round end clears the world: no reward then
            return;
        }
        if (_events.End(EventBook.Bigfoot, EventPhase.Despawned, _ctx.Now) != null)
        {
            _ctx.ChatToAll(ChatLineKind.System, "", w.Reason == WorldDespawnReason.Expired ? "The Bigfoot went back into the woods. Nobody came for it." : "The Bigfoot event is over.");
            GD.Print($"server: Bigfoot despawned ({w.Reason}), no reward");
        }
    }

    /// <summary>A new round clears the world, the Bigfoot and its tracks with it (the despawn handler ends the run); this is the net for a run the clear did not reach.</summary>
    public void RoundReset()
    {
        if (_life.Stage == BigfootStage.None) _events.End(EventBook.Bigfoot, EventPhase.Despawned, _ctx.Now);
    }

    static NVec3 ToN(Vector3 v) => new(v.X, v.Y, v.Z);

    static float Flat(NVec3 a, NVec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
