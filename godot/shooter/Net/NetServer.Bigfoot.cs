using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Bigfoot event on the server (issue #264, GLOSSARY "Event"). An admin starts it with <c>/event bigfoot</c>; the <see cref="Bigfoot"/> brain
/// (<c>ShooterCore/Events</c>) fights from then on, as a world entity of its own (not a bot) that the shots reach as a scaled <see cref="HitTarget"/>
/// (<see cref="DealBigfoot"/> counts the hits). It ends as a win (it dies: an "event won" line, XP, then its body is a corpse that carries the head)
/// or despawned (nobody came near for minutes, the admin cancelled it, the round ended): no reward. This file is the world the brain stands in
/// (<see cref="BigfootEnv"/>: the terrain, the solid world, the players, the vehicles it throws), the lifecycle, the reward and the chat commands.
/// </summary>
public partial class NetServer
{
    /// <summary>bigfoot.json: every number of the fight.</summary>
    public BigfootFile BigfootData = new();

    readonly EventBook _events = new();
    Bigfoot? _bigfoot;
    ushort _bigfootId;
    BigfootEnv? _bigfootEnv;
    /// <summary>The Bigfoot's hit volume at the end of recent ticks, for the rewind of a shot (ADR 0013), and the tick it appeared.</summary>
    readonly PoseRing _bigfootPoses = new();
    uint _bigfootLifeStart;
    /// <summary>It died: its body lies out the death clip, and when the clip is over it is a corpse to loot where it fell.</summary>
    bool _bigfootWon;
    NVec3 _bigfootFell;
    float _bigfootFellYaw;
    /// <summary>The living players and the throwable vehicles of this tick, refilled once a tick before the entities tick (<see cref="BigfootPrepare"/>).</summary>
    readonly List<BigfootPlayer> _bfPlayers = new();
    readonly List<BigfootVehicle> _bfVehicles = new();

    /// <summary>Registers the Bigfoot's world entity types and follows the entities' end. Called once from <c>_Ready</c>.</summary>
    void InitBigfoot()
    {
        BigfootRules.Register(WorldEntityRegistry.Shared);
        _match.Entities.Despawned += OnBigfootWorldDespawn;
    }

    // ---- the hit pipeline ----

    /// <summary>The Bigfoot as a hit target now (null when there is none or it is dead): the player's capsule at twice the size, its head guarded.</summary>
    HitTarget? BigfootTarget() => _bigfoot is { Alive: true } bf ? bf.HitVolume : null;

    /// <summary>The target where the shooter's screen showed it (<paramref name="view"/> is the rewind tick), else where it stands now.</summary>
    HitTarget BigfootRewound(HitTarget now, uint view) =>
        LagComp.TryTargetAt(_bigfootPoses, view, _bigfootLifeStart, out var past) ? past : now;

    void CaptureBigfootPose() => _bigfootPoses.Record(_tick, _bigfoot is { Alive: true }, _bigfoot?.HitVolume ?? default);

    /// <summary>
    /// A shot, a pellet fan, a blast or a gun round named the Bigfoot (<see cref="Deal"/>). It has no hit points: the hit counts what its source weighs
    /// (<see cref="BigfootRules.Weight"/>: a bullet 1, an explosion or a vehicle weapon more, ramming nothing). The shooter gets the Hit event, so the marker
    /// shows; there is never a headshot. A hit with no weight is absorbed (<see cref="DamageResult.Ignored"/>).
    /// </summary>
    DamageResult DealBigfoot(ushort attackerId, byte weaponIdx)
    {
        _deathPush.Remove(BigfootRules.TargetId);
        if (_bigfoot is not { Alive: true } bf || _match.Phase != MatchPhase.Running) return DamageResult.Ignored;
        var source = BigfootRules.SourceOf(weaponIdx, Weapons.Weapons.Count, i => Weapons.Weapons[i].Kind == WeaponKind.Projectile);
        var weight = BigfootRules.Weight(BigfootData.Hits, source);
        if (!(weight > 0)) return DamageResult.Ignored;
        var killed = bf.Hit(attackerId, weight);
        _hitsLanded++;
        var msg = new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(BigfootRules.TargetId, attackerId, weaponIdx, (ushort)Math.Clamp(weight, 1f, 255f), false, false, killed)));
        _byId.TryGetValue(attackerId, out var attacker);
        if (attackerId == _gunFxOwner) _gunFx.Hit(attackerId, false, killed);
        else if (attacker != null) Send(attacker, msg);
        return killed ? DamageResult.Killed : DamageResult.Damaged;
    }

    // ---- the tick ----

    /// <summary>Once a tick before the entities tick: the living players and the vehicles it may throw, for the brain.</summary>
    void BigfootPrepare()
    {
        _bfPlayers.Clear(); _bfVehicles.Clear();
        if (_bigfoot == null) return;
        foreach (var c in _byId.Values)
            if (_match.Get(c.Id)?.Life == PlayerLife.Alive) _bfPlayers.Add(new BigfootPlayer(c.Id, N(c.Body.GlobalPosition), c.Body.State.OnFloor, c.Car != null));
        foreach (var car in _cars)
            if (!car.Destroyed && car.Thrown == null) _bfVehicles.Add(new BigfootVehicle(car.Id, N(car.GlobalPosition), car.Def.HalfLength));
    }

    /// <summary>A vehicle thrown by the Bigfoot flies its arc: the server places it, and its riders with it. It lands exactly where the throw was aimed.</summary>
    void StepThrownVehicle(ServerCar car, float dt)
    {
        var t = car.Thrown!;
        var p = t.Step(dt);
        car.PlaceThrown(new Vector3(p.X, p.Y, p.Z), t.Yaw, t.VerticalSpeed);
        if (t.Done)
        {
            var landing = t.Landing;
            car.EndThrow(new Vector3(landing.X, landing.Y, landing.Z));
            BigfootFxEvent(BigfootFxKind.VehicleLand, N(car.GlobalPosition), car.Id);
        }
        car.PrevPos = car.TickEndPos = car.GlobalPosition;
        for (var s = 0; s < car.Seat.Count; s++) // the riders stay on their seats the whole way
        {
            if (OccupantAt(car, s) is not { } o) continue;
            var at = SeatPos(car, o.CarSeat);
            o.Body.Teleport(at);
            _match.SetPosition(o.Id, N(at));
        }
    }

    void BigfootFxEvent(BigfootFxKind kind, NVec3 at, ushort id = 0, byte extra = 0) =>
        BroadcastEvent(EventKind.BigfootFx, BigfootFxCodec.Encode(new BigfootFxMsg(kind, at, id, extra)));

    // ---- the lifecycle ----

    /// <summary>"/event bigfoot": spawns it at a baked point of an area that carries <c>event spawn: bigfoot</c>, or, with none on the map, near a random living player (a dev fallback, said to the admin and in the log).</summary>
    void StartBigfoot(Client admin)
    {
        if (_events.Current is { } running)
        {
            ChatTo(admin, ChatLineKind.Error, $"The {running.Kind} event is already running. /event current end ends it as a win, /event current cancel removes it.");
            return;
        }
        if (_bigfoot != null) { ChatTo(admin, ChatLineKind.Error, "The last Bigfoot is still lying where it fell. Try again in a few seconds."); return; }
        if (_match.Phase != MatchPhase.Running) { ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        var living = _byId.Values.Where(o => _match.Get(o.Id)?.Life == PlayerLife.Alive).ToList();
        var spawn = PickEventSpawn(EventBook.Bigfoot);
        var fallback = false;
        string? nearName = null;
        NVec3 nearPos = default;
        if (spawn is not { } point)
        {
            // no area on this map carries the attribute: the dev fallback, a spot near a random living player (humans first)
            var pool = living.Where(o => !o.IsBot).ToList();
            if (pool.Count == 0) pool = living;
            if (pool.Count == 0) { ChatTo(admin, ChatLineKind.Error, "The map has no 'event spawn: bigfoot' area and no player is alive to spawn it near."); return; }
            var anchor = pool[_rng.Next(pool.Count)];
            var found = FallbackBigfootSpot(N(anchor.Body.GlobalPosition));
            if (found is not { } spot) { ChatTo(admin, ChatLineKind.Error, "The map has no 'event spawn: bigfoot' area, and no ground was found near a player for the fallback."); return; }
            spawn = spot; fallback = true; nearName = anchor.Name; nearPos = N(anchor.Body.GlobalPosition);
        }
        var at = spawn!.Value;
        _bigfootEnv = new BigfootEnv(this);
        _bigfootEnv.Refresh();
        var yaw = 0f;
        var nearest = living.OrderBy(o => Dist2D(N(o.Body.GlobalPosition), at)).FirstOrDefault();
        if (nearest != null) yaw = TurretMath.YawTo(at, N(nearest.Body.GlobalPosition));
        var participants = living.Count(o => Dist2D(N(o.Body.GlobalPosition), at) <= BigfootData.Hits.ParticipantRadiusM);
        var bf = new Bigfoot(BigfootData, _bigfootEnv, at, yaw, _rng.Next(), participants);
        bf.Died += OnBigfootDied;
        var id = _match.Entities.Spawn(bf);
        if (id is not { } eid) { ChatTo(admin, ChatLineKind.Error, "The world is full of entities; the Bigfoot could not be spawned."); return; }
        _bigfoot = bf; _bigfootId = eid; _bigfootWon = false;
        _bigfootLifeStart = _tick;
        _events.Start(EventBook.Bigfoot, _elapsed);
        BigfootFxEvent(BigfootFxKind.Appear, bf.Position);
        ChatToAll(ChatLineKind.System, "", $"{admin.Name} started the Bigfoot event. Something huge is moving through the village.");
        if (fallback) ChatTo(admin, ChatLineKind.Info, $"The map has no 'event spawn: bigfoot' area, so this is the dev fallback: it appeared {Dist2D(bf.Position, nearPos):0} m from {nearName}.");
        GD.Print($"server: Bigfoot started by {admin.Name} #{admin.Id} at ({bf.Position.X:F1},{bf.Position.Y:F1},{bf.Position.Z:F1}), {(fallback ? $"FALLBACK spawn near {nearName} (the map has no event_spawn: bigfoot area)" : "a baked event_spawn point")}, hits needed {bf.HitsNeeded}, {participants} participants");
    }

    /// <summary>A random spot between the fallback distances from <paramref name="anchor"/> with ground under it, or null.</summary>
    NVec3? FallbackBigfootSpot(NVec3 anchor)
    {
        var f = BigfootData.FallbackSpawn;
        for (var i = 0; i < 16; i++)
        {
            var a = (float)_rng.NextDouble() * MathF.Tau;
            var d = f.MinDistanceM + (float)_rng.NextDouble() * (f.MaxDistanceM - f.MinDistanceM);
            var x = anchor.X + MathF.Cos(a) * d; var z = anchor.Z + MathF.Sin(a) * d;
            if (BigfootGround(x, z, anchor.Y) is { } y) return new NVec3(x, y, z);
        }
        return null;
    }

    /// <summary>"/event current end" (a win, for testing) or "/event current cancel" (gone, no reward).</summary>
    void ChatCurrentEvent(Client c, string action)
    {
        if (action is not ("end" or "cancel")) { ChatTo(c, ChatLineKind.Error, "Usage: /event current end (conclude it as a win) or /event current cancel (remove it, no reward)."); return; }
        if (_events.Current == null || _bigfoot is not { } bf)
        {
            // no Bigfoot: the Holy Mass keeps its own run (it can only be ended)
            if (action == "end" || _mass is not { Running: true }) ChatCurrentEnd(c);
            else ChatTo(c, ChatLineKind.Error, "The Holy Mass takes /event current end.");
            return;
        }
        if (action == "end")
        {
            ChatToAll(ChatLineKind.System, "", $"{c.Name} ended the Bigfoot event as a win.");
            bf.Kill(); // the same path as a kill: OnBigfootDied
        }
        else
        {
            ChatToAll(ChatLineKind.System, "", $"{c.Name} cancelled the Bigfoot event.");
            bf.Remove(WorldDespawnReason.Removed); // the manager despawns it on this tick; OnBigfootWorldDespawn ends the run
        }
    }

    /// <summary>It fell: the event is won. The line, the XP for everyone credited, and the effect; the body lies out its death clip and then the tracks begin.</summary>
    void OnBigfootDied(Bigfoot bf)
    {
        _events.End(EventPhase.Won, _elapsed);
        _bigfootWon = true;
        _bigfootFell = bf.Position; _bigfootFellYaw = bf.Yaw;
        BigfootFxEvent(BigfootFxKind.Death, bf.Position);
        var credited = bf.Credited.Where(id => _byId.ContainsKey(id)).ToList();
        var names = credited.Count == 0 ? "" : " " + string.Join(", ", credited.Select(id => _byId[id].Name).Take(8)) + (credited.Count > 8 ? " and others" : "") + " took part.";
        ChatToAll(ChatLineKind.System, "", $"Event won: the Bigfoot is down!{names} Loot its body for what it left behind.");
        AwardXp(credited.Where(Scores).Select(id => new XpGrant(id, Levels.Awards.EventWin, XpReason.EventWin)).ToList());
        GD.Print($"server: Bigfoot died (last hit by #{bf.Killer}), credited {string.Join(",", credited)}");
    }

    /// <summary>The Bigfoot entity left the world: after its death it is the reward's time, otherwise the event ended with no reward.</summary>
    void OnBigfootWorldDespawn(WorldDespawned w)
    {
        if (w.Id != _bigfootId || _bigfoot == null) return;
        var won = _bigfootWon;
        _bigfoot = null; _bigfootId = 0; _bigfootEnv = null; _bigfootWon = false;
        if (won)
        {
            if (w.Reason != WorldDespawnReason.Removed) SpawnBigfootReward(_bigfootFell); // a round end clears the world: no reward then
            return;
        }
        if (_events.End(EventPhase.Despawned, _elapsed) != null)
        {
            ChatToAll(ChatLineKind.System, "", w.Reason == WorldDespawnReason.Expired ? "The Bigfoot went back into the woods. Nobody came for it." : "The Bigfoot event is over.");
            GD.Print($"server: Bigfoot despawned ({w.Reason}), no reward");
        }
    }

    /// <summary>The reward: when its death clip is over, its body is a corpse where it fell that carries the head (the corpse item path of ADR 0016 and 0018), lasting <see cref="BigfootReward.CorpseSeconds"/>.</summary>
    void SpawnBigfootReward(NVec3 fell)
    {
        var r = BigfootData.Reward;
        var item = Items.IndexOf(r.HeadItem);
        if (item < 0) { GD.PrintErr($"server: the Bigfoot's reward item '{r.HeadItem}' is not in items.json: no corpse"); return; }
        var knife = Weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee); // a weapon nobody can take into a hotbar position (CorpseRules.CanReplace): the corpse is for its item
        var corpse = new Corpse(BigfootRules.TargetId, fell, _bigfootFellYaw, Math.Max(0, knife), 0, new[] { new ItemStack(item) }, r.CorpseSeconds);
        var id = _corpses.Add(corpse);
        if (id is not { } cid) { GD.PrintErr("server: the world refused the Bigfoot's corpse"); return; }
        BroadcastCorpseItems(cid);
        GD.Print($"server: Bigfoot corpse {cid} at ({fell.X:F1},{fell.Y:F1},{fell.Z:F1}), lasts {r.CorpseSeconds:F0} s");
    }

    /// <summary>A new round clears the world, the Bigfoot and its tracks with it; nothing of the event is left running.</summary>
    void BigfootRoundReset()
    {
        if (_events.Current != null && _bigfoot == null) _events.End(EventPhase.Despawned, _elapsed);
    }

    // ---- the terrain and the solid world ----

    /// <summary>Whether the collider a ray hit is a tree's trunk: the Bigfoot tramples those (placed trees and the baked ones carry the surface "wood").</summary>
    static bool IsTreeHit(Godot.Collections.Dictionary hit) =>
        hit["collider"].AsGodotObject() is Node n && n.HasMeta("surface") && n.GetMeta("surface").AsString() == "wood";

    /// <summary>
    /// The ground height at (x, z) for something standing near <paramref name="nearY"/>: the first surface a ray finds from a little above down to a
    /// dozen metres below, trees skipped. Null where there is none, or the water is deeper than it wades. A roof less than a few metres above counts as ground.
    /// </summary>
    float? BigfootGround(float x, float z, float nearY)
    {
        if (Water.Depth(x, z) > BigfootWadeM) return null;
        var space = World.GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid>();
        for (var i = 0; i < 5; i++)
        {
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(x, nearY + BigfootStepUpM, z), new Vector3(x, nearY - BigfootDropM, z), CollisionLayers.World);
            q.Exclude = exclude;
            var hit = space.PerfRay(q);
            if (hit.Count == 0) return null;
            if (!IsTreeHit(hit)) return hit["position"].AsVector3().Y;
            exclude.Add(hit["rid"].AsRid());
        }
        return null;
    }

    const float BigfootStepUpM = 3.5f, BigfootDropM = 12f, BigfootWadeM = 2.2f;

    /// <summary>True when something solid that is not a tree (a building, a wall, a post) stands between the two points, for a body of <paramref name="radius"/>.</summary>
    bool BigfootBlocked(NVec3 from, NVec3 to, float radius)
    {
        var dir = to - from;
        var len = dir.Length();
        if (len < 1e-4f) return false;
        dir /= len;
        var space = World.GetWorld3D().DirectSpaceState;
        foreach (var dy in new[] { 0f, 1.3f }) // a low and a high ray: a fence post and a wall
        {
            var a = new Vector3(from.X, from.Y + dy, from.Z);
            var b = a + new Vector3(dir.X, dir.Y, dir.Z) * (len + radius);
            var exclude = new Godot.Collections.Array<Rid>();
            for (var i = 0; i < 6; i++)
            {
                var q = PhysicsRayQueryParameters3D.Create(a, b, CollisionLayers.World);
                q.Exclude = exclude;
                var hit = space.PerfRay(q);
                if (hit.Count == 0) break;
                if (!IsTreeHit(hit)) return true;
                exclude.Add(hit["rid"].AsRid());
            }
        }
        return false;
    }

    // ---- the world the brain stands in ----

    /// <summary>The server's side of <see cref="IBigfootWorld"/>: terrain and solids by the physics space, the players and vehicles by the tick's lists, and the hands that hurt, hold, throw and show.</summary>
    sealed class BigfootEnv : IBigfootWorld
    {
        readonly NetServer _s;
        public BigfootEnv(NetServer s) => _s = s;

        /// <summary>Fills the lists once before the first tick (the event began between ticks).</summary>
        public void Refresh() => _s.BigfootPrepare();

        public IReadOnlyList<BigfootPlayer> Players => _s._bfPlayers;
        public IReadOnlyList<BigfootVehicle> Vehicles => _s._bfVehicles;
        public float? GroundY(float x, float z, float nearY) => _s.BigfootGround(x, z, nearY);
        public bool Blocked(NVec3 from, NVec3 to, float radius) => _s.BigfootBlocked(from, to, radius);

        public void SpawnTrunk(NVec3 from, NVec3 velocity) =>
            _s._match.Entities.Spawn(new TrunkEntity(_s.BigfootData, this, from, velocity, _s._rng.Next()));

        public void Hurt(ushort player, float damage, BigfootHurt how)
        {
            if (!_s._byId.ContainsKey(player)) return;
            _s.Deal(BigfootRules.TargetId, SpecialWeapons.Bigfoot, player, damage, false, false);
        }

        public void Knock(ushort player, NVec3 velocity)
        {
            if (_s._byId.TryGetValue(player, out var c) && c.Car == null) c.Body.AddImpulse(new Vector3(velocity.X, velocity.Y, velocity.Z));
        }

        public void Hold(ushort player, NVec3 at)
        {
            if (!_s._byId.TryGetValue(player, out var c) || c.Car != null) return;
            c.HeldUntilTick = _s._tick + 1; // this tick's inputs and the next's are skipped: the Bigfoot puts the body here again every tick
            c.Body.Teleport(new Vector3(at.X, at.Y, at.Z));
            _s._match.SetPosition(player, at);
        }

        public void Launch(ushort player, NVec3 velocity, float graceSeconds)
        {
            if (!_s._byId.TryGetValue(player, out var c)) return;
            c.HeldUntilTick = 0; // let go
            if (velocity == NVec3.Zero) return;
            c.LaunchGraceUntil = _s._elapsed + graceSeconds;
            c.Body.AddImpulse(new Vector3(velocity.X, velocity.Y, velocity.Z));
        }

        public void LiftVehicle(ushort vehicle, NVec3 hold, float seconds)
        {
            var car = _s._cars.FirstOrDefault(v => v.Id == vehicle);
            if (car == null || car.Destroyed || car.Thrown != null) return;
            car.Thrown = new VehicleThrow(N(car.GlobalPosition), car.State.Yaw, hold, seconds);
            car.Wake();
            car.HitCooldown.Clear();
        }

        public void ThrowVehicle(ushort vehicle, NVec3 landing, float seconds, float spinRadPerS)
        {
            var car = _s._cars.FirstOrDefault(v => v.Id == vehicle);
            if (car is { Destroyed: false, Thrown: { Released: false } lifted }) { lifted.Release(landing, seconds, spinRadPerS); return; } // from over its head
            if (car == null || car.Destroyed || car.Thrown != null) return;
            car.Thrown = new VehicleThrow(N(car.GlobalPosition), car.State.Yaw, landing, seconds, spinRadPerS);
            car.Wake();
            car.HitCooldown.Clear();
        }

        public void Fx(BigfootFxKind kind, NVec3 at, ushort id = 0, byte extra = 0) => _s.BigfootFxEvent(kind, at, id, extra);
    }
}
