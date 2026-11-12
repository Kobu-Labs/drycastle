using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class WorldEntityTests
{
    const byte Hp = 7, Plain = 8;

    static WorldEntityRegistry Types()
    {
        var r = new WorldEntityRegistry();
        r.Register(new WorldEntityDef(Hp, "dummy turret", HasHp: true));
        r.Register(new WorldEntityDef(Plain, "dummy corpse", HasHp: false));
        return r;
    }

    /// <summary>Test-only world entity. Dies when its hp runs out or its lifetime ends.</summary>
    sealed class Dummy : IWorldEntity
    {
        public byte TypeId { get; init; } = Hp;
        public ushort Owner { get; init; } = 3;
        public Vector3 Pos;
        public float Yaw, Pitch, Health = 100, Life = float.MaxValue;
        public WorldDespawnReason? Ended { get; private set; }
        public int Ticks;

        public void Tick(float dt)
        {
            Ticks++;
            Life -= dt;
            if (Life <= 0) Ended = WorldDespawnReason.Expired;
        }

        public void OnDamage(float amount, ushort attacker)
        {
            Health -= amount;
            if (Health <= 0) Ended = WorldDespawnReason.Destroyed;
        }

        public WorldState Serialize() => WorldState.Make(Pos, Yaw, Pitch, Owner, 1, TypeId == Hp ? Health : 0);
    }

    /// <summary>Feeds the manager's events into a list in order, so tests can check the lifecycle sequence.</summary>
    static (WorldEntityManager m, List<string> log) Manager(WorldEntityRegistry? types = null)
    {
        var m = new WorldEntityManager(types ?? Types());
        var log = new List<string>();
        m.Spawned += s => log.Add($"spawn {s.Id}");
        m.Despawned += d => log.Add($"despawn {d.Id} {d.Reason}");
        return (m, log);
    }

    /// <summary>Replays the reliable lifecycle events the server would send, in order, into a replica.</summary>
    static void Relay(WorldEntityManager m, WorldReplica r)
    {
        m.Spawned += s => r.OnEvent(EventKind.WorldSpawn, WorldCodec.EncodeSpawn(s.Id, s.Type, s.State));
        m.Despawned += d => r.OnEvent(EventKind.WorldDespawn, WorldCodec.EncodeDespawn(d.Id, d.Reason));
    }

    static List<WorldEntry> Entries(WorldEntityManager m) => m.Snapshot();

    [Fact]
    public void SpawnAndDespawnPayloadsRoundTrip()
    {
        var s = WorldState.Make(new Vector3(12.5f, -3.25f, 400f), 1.2f, -0.3f, 9, 2, 88f);
        var spawn = WorldCodec.EncodeSpawn(300, Hp, s);
        Assert.True(WorldCodec.TryDecodeSpawn(spawn, out var id, out var type, out var back));
        Assert.Equal((ushort)300, id);
        Assert.Equal(Hp, type);
        Assert.Equal(s, back);

        var gone = WorldCodec.EncodeDespawn(300, WorldDespawnReason.Expired);
        Assert.True(WorldCodec.TryDecodeDespawn(gone, out var gid, out var reason));
        Assert.Equal((ushort)300, gid);
        Assert.Equal(WorldDespawnReason.Expired, reason);
    }

    [Fact]
    public void TruncatedOrUnknownLifecyclePayloadsAreRejected()
    {
        var spawn = WorldCodec.EncodeSpawn(1, Hp, default);
        for (var cut = 0; cut < spawn.Length; cut++)
            Assert.False(WorldCodec.TryDecodeSpawn(spawn.AsSpan(0, cut), out _, out _, out _));
        Assert.False(WorldCodec.TryDecodeDespawn(new byte[] { 1, 0, 9 }, out _, out _));
    }

    [Fact]
    public void StateRoundTripsThroughKeyframeAndDelta()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        var e = new Dummy { Pos = new Vector3(1, 2, 3), Yaw = 0.5f, Pitch = 0.1f, Health = 100 };
        var m = new WorldEntityManager(types);
        Relay(m, rep);
        var id = m.Spawn(e)!.Value;

        // keyframe (new entity)
        foreach (var p in enc.Encode(10, Entries(m))) Assert.True(rep.Apply(p));
        AssertMatches(rep.Entities[id].State, Entries(m)[0].State);
        Assert.Equal(Hp, rep.Entities[id].Type);

        // small move, rotation and hp change: delta
        e.Pos = new Vector3(1.5f, 2, 3.25f); e.Yaw = 0.7f; e.Health = 61;
        var packets = enc.Encode(11, Entries(m));
        Assert.Single(packets);
        Assert.Equal(Wire.FPosDelta | Wire.FRot | Wire.FHp, packets[0][6 + 3]); // flags of the only record: no keyframe
        foreach (var p in packets) Assert.True(rep.Apply(p));
        AssertMatches(rep.Entities[id].State, Entries(m)[0].State);
        Assert.Equal(61, rep.Entities[id].State.Hp);
    }

    [Fact]
    public void LargeJumpSendsAbsolutePositionWithoutAKeyframe()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        var m = new WorldEntityManager(types);
        Relay(m, rep);
        var e = new Dummy { Pos = Vector3.Zero };
        var id = m.Spawn(e)!.Value;
        foreach (var p in enc.Encode(0, Entries(m))) rep.Apply(p);

        e.Pos = new Vector3(900, 0, 0); // 115200 units: too big for the i16 delta, so the record goes absolute
        foreach (var p in enc.Encode(1, Entries(m))) Assert.True(rep.Apply(p));
        AssertMatches(rep.Entities[id].State, Entries(m)[0].State);
    }

    [Fact]
    public void UnchangedEntitiesAreOmittedAndKeyframesComeEvery30Ticks()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var m = new WorldEntityManager(types);
        m.Spawn(new Dummy { Pos = new Vector3(5, 0, 5) });
        var keyframes = new List<uint>();
        var packetTicks = new List<uint>();
        for (uint tick = 0; tick < 90; tick++)
        {
            var packets = enc.Encode(tick, Entries(m));
            if (packets.Count > 0) packetTicks.Add(tick);
            foreach (var p in packets)
                if ((p[6 + 3] & Wire.FKey) == Wire.FKey) keyframes.Add(tick);
        }
        // the entity never moves: packets only on keyframe ticks, and keyframes 30 ticks apart after the first
        Assert.Equal(keyframes, packetTicks);
        Assert.True(keyframes.Count >= 3);
        for (var i = 2; i < keyframes.Count; i++) Assert.Equal(30u, keyframes[i] - keyframes[i - 1]);
        Assert.Empty(enc.Encode(100, Array.Empty<WorldEntry>()));
    }

    [Fact]
    public void PlainTypesCarryNoHpByte()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        var m = new WorldEntityManager(types);
        Relay(m, rep);
        var id = m.Spawn(new Dummy { TypeId = Plain, Owner = 4, Health = 50 })!.Value;
        foreach (var p in enc.Encode(0, Entries(m))) Assert.True(rep.Apply(p));
        Assert.Equal(0, rep.Entities[id].State.Hp);
        Assert.Equal((ushort)4, rep.Entities[id].State.Owner);
    }

    [Fact]
    public void SpawnBeforeTheFirstKeyframeReachesTheClientInOrder()
    {
        var types = Types();
        var m = new WorldEntityManager(types);
        var rep = new WorldReplica();
        Relay(m, rep);
        var enc = new WorldSnapshotEncoder(types);
        var id = m.Spawn(new Dummy { Pos = new Vector3(2, 0, 2) })!.Value;
        // the spawn event alone already creates the entity with its state
        Assert.True(rep.Entities.ContainsKey(id));
        foreach (var p in enc.Encode(7, Entries(m))) Assert.True(rep.Apply(p));
        AssertMatches(rep.Entities[id].State, Entries(m)[0].State);
    }

    [Fact]
    public void KeyframeThatBeatsItsSpawnIsHeldAndAppliedOnSpawn()
    {
        var types = Types();
        var m = new WorldEntityManager(types);
        var enc = new WorldSnapshotEncoder(types);
        var e = new Dummy { Pos = new Vector3(3, 1, 3), Health = 40 };
        var id = m.Spawn(e)!.Value;
        var spawnPayload = WorldCodec.EncodeSpawn(id, Hp, Entries(m)[0].State);
        var keyframe = enc.Encode(5, Entries(m));

        var rep = new WorldReplica();
        foreach (var p in keyframe) Assert.True(rep.Apply(p)); // unreliable arrives first: held, not applied
        Assert.False(rep.Entities.ContainsKey(id));
        rep.OnEvent(EventKind.WorldSpawn, spawnPayload);
        Assert.True(rep.Entities.ContainsKey(id));
        AssertMatches(rep.Entities[id].State, Entries(m)[0].State);
    }

    [Fact]
    public void DeltaForAnUnknownEntityIsDropped()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var m = new WorldEntityManager(types);
        var e = new Dummy();
        var id = m.Spawn(e)!.Value;
        enc.Encode(0, Entries(m)); // the keyframe goes to a client that is not listening
        e.Pos = new Vector3(0.5f, 0, 0);
        var rep = new WorldReplica();
        var delta = enc.Encode(1, Entries(m)); // a delta, for an id this client never got a spawn or keyframe for
        Assert.Single(delta);
        Assert.True(rep.Apply(delta[0]));
        Assert.False(rep.Entities.ContainsKey(id));
    }

    [Fact]
    public void DespawnEventsAreRaisedInCallOrderAndTheClientDropsTheEntity()
    {
        var types = Types();
        var (m, log) = Manager(types);
        var rep = new WorldReplica();
        Relay(m, rep);

        var a = m.Spawn(new Dummy())!.Value;
        var b = m.Spawn(new Dummy())!.Value;
        m.Despawn(a, WorldDespawnReason.Removed);

        Assert.Equal(new[] { $"spawn {a}", $"spawn {b}", $"despawn {a} Removed" }, log);
        Assert.False(rep.Entities.ContainsKey(a));
        Assert.True(rep.Entities.ContainsKey(b));
    }

    [Fact]
    public void StaleSnapshotAfterDespawnCannotBringTheEntityBack()
    {
        var types = Types();
        var (m, _) = Manager(types);
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        Relay(m, rep);
        var a = m.Spawn(new Dummy())!.Value;
        var inFlight = enc.Encode(3, Entries(m)); // sent before the despawn, delivered after it
        m.Despawn(a, WorldDespawnReason.Destroyed);
        foreach (var p in inFlight) rep.Apply(p);
        Assert.False(rep.Entities.ContainsKey(a));
    }

    [Fact]
    public void IdsAreNotReusedAfterDespawn()
    {
        var (m, log) = Manager();
        var a = m.Spawn(new Dummy())!.Value;
        m.Despawn(a, WorldDespawnReason.Expired);
        var b = m.Spawn(new Dummy())!.Value;
        Assert.NotEqual(a, b);
        Assert.Equal(new[] { $"spawn {a}", $"despawn {a} Expired", $"spawn {b}" }, log);
    }

    [Fact]
    public void UnregisteredTypeIsRefusedWithAnError()
    {
        var logs = new List<string>();
        var m = new WorldEntityManager(Types()) { Log = logs.Add };
        Assert.Null(m.Spawn(new Dummy { TypeId = 99 }));
        Assert.Equal(0, m.Count);
        Assert.Single(logs);
    }

    [Fact]
    public void CapOf256IsEnforcedWithAnErrorLog()
    {
        var logs = new List<string>();
        var m = new WorldEntityManager(Types()) { Log = logs.Add };
        for (var i = 0; i < WorldEntityManager.MaxEntities; i++) Assert.NotNull(m.Spawn(new Dummy()));
        Assert.Null(m.Spawn(new Dummy()));
        Assert.Equal(WorldEntityManager.MaxEntities, m.Count);
        Assert.Single(logs);
        Assert.Contains("cap", logs[0]);
    }

    [Fact]
    public void DamageDestroysAndTickExpiresWithTheRightReason()
    {
        var (m, log) = Manager();
        var turret = m.Spawn(new Dummy { Health = 30 })!.Value;
        var missile = m.Spawn(new Dummy { Life = 0.5f })!.Value;

        m.Damage(turret, 10, 1);
        Assert.Equal(2, m.Count); // still alive after 10 of 30 hp
        m.Damage(turret, 25, 1);
        m.Tick(0.6f);

        Assert.Equal(new[] { $"spawn {turret}", $"spawn {missile}", $"despawn {turret} Destroyed", $"despawn {missile} Expired" }, log);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void ClearDespawnsEverythingAsRemoved()
    {
        var (m, log) = Manager();
        var a = m.Spawn(new Dummy())!.Value;
        var b = m.Spawn(new Dummy())!.Value;
        m.Clear();
        Assert.Equal(new[] { $"spawn {a}", $"spawn {b}", $"despawn {a} Removed", $"despawn {b} Removed" }, log);
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void TheMatchOwnsAnEntityManagerThatClearsAtRoundStart()
    {
        var cfg = new MatchConfig();
        var match = new MatchSim(cfg, () => 0, new List<Vector3> { Vector3.Zero }, new Random(1), Types());
        match.Entities.Spawn(new Dummy());
        Assert.Equal(1, match.Entities.Count);
        match.AddPlayer(1, "p");   // starts the round when the player count is enough
        Assert.Equal(0, match.Entities.Count);
    }

    static void AssertMatches(WorldState got, WorldState want)
    {
        Assert.Equal(want.X, got.X); Assert.Equal(want.Y, got.Y); Assert.Equal(want.Z, got.Z);
        Assert.Equal(want.Yaw, got.Yaw); Assert.Equal(want.Pitch, got.Pitch);
        Assert.Equal(want.Owner, got.Owner); Assert.Equal(want.State, got.State);
    }
}
