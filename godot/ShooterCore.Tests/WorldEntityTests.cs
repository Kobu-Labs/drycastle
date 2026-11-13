using System.Buffers.Binary;
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

    /// <summary>One server tick for one client, against the client's acknowledged world tick. The packets are delivered unless <paramref name="lost"/>.</summary>
    static List<byte[]> Send(WorldSnapshotEncoder enc, WorldReplica rep, uint tick, WorldEntityManager m, bool lost = false)
    {
        var packets = enc.Encode(tick, rep.AckTick, Entries(m));
        if (!lost) foreach (var p in packets) rep.Apply(p);
        return packets;
    }

    static uint BaseOf(byte[] p) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(5));

    /// <summary>Flags of the first record of a packet.</summary>
    static byte FirstFlags(byte[] p) => p[WorldSnapshotEncoder.HeaderBytes + 3];

    static WorldState StateOf(WorldEntityManager m, ushort id) => m.StateOf(id)!.Value;

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
    public void StateRoundTripsThroughKeyframeAndDeltaAgainstTheAckedBaseline()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        var e = new Dummy { Pos = new Vector3(1, 2, 3), Yaw = 0.5f, Pitch = 0.1f, Health = 100 };
        var m = new WorldEntityManager(types);
        Relay(m, rep);
        var id = m.Spawn(e)!.Value;

        // keyframe (new entity, no baseline yet)
        var first = Send(enc, rep, 10, m);
        Assert.Single(first);
        Assert.Equal(Wire.NoTick, BaseOf(first[0]));
        Assert.Equal(Wire.FKey | Wire.FHp, FirstFlags(first[0]));
        AssertMatches(rep.Entities[id].State, StateOf(m, id));
        Assert.Equal(Hp, rep.Entities[id].Type);

        // small move, rotation and hp change: a delta against tick 10, the client's acknowledged tick
        e.Pos = new Vector3(1.5f, 2, 3.25f); e.Yaw = 0.7f; e.Health = 61;
        var packets = Send(enc, rep, 11, m);
        Assert.Single(packets);
        Assert.Equal(10u, BaseOf(packets[0]));
        Assert.Equal(Wire.FPosDelta | Wire.FRot | Wire.FHp, FirstFlags(packets[0]));
        AssertMatches(rep.Entities[id].State, StateOf(m, id));
        Assert.Equal(61, rep.Entities[id].State.Hp);
        Assert.Equal(11u, rep.AckTick);
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
        Send(enc, rep, 0, m);

        e.Pos = new Vector3(900, 0, 0); // 115200 units: too big for the i16 delta, so the record goes absolute
        var packets = Send(enc, rep, 1, m);
        Assert.Single(packets);
        Assert.Equal(Wire.FPosAbs, FirstFlags(packets[0]));
        AssertMatches(rep.Entities[id].State, StateOf(m, id));
    }

    [Fact]
    public void UnchangedEntitiesAreOmittedWithNoPeriodicKeyframes()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        var m = new WorldEntityManager(types);
        m.Spawn(new Dummy { Pos = new Vector3(5, 0, 5) });
        var packetTicks = new List<uint>();
        for (uint tick = 0; tick < 200; tick++)
            if (Send(enc, rep, tick, m).Count > 0) packetTicks.Add(tick);

        // the entity never moves: one keyframe for the first sight, then no packets at all, also past the history depth (no 30-tick keyframe)
        Assert.Equal(new uint[] { 0 }, packetTicks);
        Assert.Equal(0u, rep.AckTick); // nothing was committed after the keyframe, so the ack stays at tick 0
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
        var packets = Send(enc, rep, 0, m);
        Assert.Equal(0, FirstFlags(packets[0]) & Wire.FHp);
        Assert.Equal(0, rep.Entities[id].State.Hp);
        Assert.Equal((ushort)4, rep.Entities[id].State.Owner);
    }

    [Fact]
    public void LostPacketHealsOnTheNextTickAgainstTheSameBaseline()
    {
        var types = Types();
        var m = new WorldEntityManager(types);
        var rep = new WorldReplica();
        Relay(m, rep);
        var enc = new WorldSnapshotEncoder(types);
        var da = new Dummy { Pos = new Vector3(0, 0, 0) };
        var db = new Dummy { Pos = new Vector3(0, 0, 0) };
        var ida = m.Spawn(da)!.Value;
        var idb = m.Spawn(db)!.Value;
        Send(enc, rep, 0, m);
        Assert.Equal(0u, rep.AckTick);

        // tick 1: both move, and the packets are lost on the way
        da.Pos = new Vector3(1, 0, 0); db.Pos = new Vector3(0, 2, 0);
        Send(enc, rep, 1, m, lost: true);
        Assert.Equal(0u, rep.AckTick); // nothing committed: the client still acknowledges tick 0
        Assert.NotEqual(StateOf(m, idb), rep.Entities[idb].State);

        // tick 2: the client acknowledged 0, so the delta carries tick 1's change too: no keyframe is needed
        da.Pos = new Vector3(2, 0, 0);
        var packets = Send(enc, rep, 2, m);
        Assert.Single(packets);
        Assert.Equal(0u, BaseOf(packets[0]));
        Assert.Equal(Wire.FPosDelta, FirstFlags(packets[0]));
        Assert.Equal(2u, rep.AckTick);
        AssertMatches(rep.Entities[ida].State, StateOf(m, ida));
        AssertMatches(rep.Entities[idb].State, StateOf(m, idb));
    }

    [Fact]
    public void AWorldTickCommitsOnlyWhenAllItsPacketsArrive()
    {
        var types = Types();
        var m = new WorldEntityManager(types);
        var rep = new WorldReplica();
        Relay(m, rep);
        var enc = new WorldSnapshotEncoder(types);
        var dummies = new List<Dummy>();
        for (var i = 0; i < 45; i++) { var d = new Dummy { Pos = new Vector3(i, 0, 0) }; dummies.Add(d); m.Spawn(d); }
        Assert.Equal(2, Send(enc, rep, 0, m).Count); // 40 + 5: two packets for one tick
        Assert.Equal(0u, rep.AckTick);

        foreach (var d in dummies) d.Pos += new Vector3(1, 0, 0);
        var packets = enc.Encode(1, rep.AckTick, Entries(m));
        Assert.Equal(2, packets.Count);
        Assert.False(rep.Apply(packets[1])); // the second half arrives alone: not a tick yet
        Assert.Equal(0u, rep.AckTick);
        Assert.True(rep.Apply(packets[0])); // the tick completes
        Assert.Equal(1u, rep.AckTick);
        foreach (var (id, _) in rep.Entities) AssertMatches(rep.Entities[id].State, StateOf(m, id));
    }

    [Fact]
    public void BaselineThatFellOutOfTheHistoryFallsBackToKeyframes()
    {
        var types = Types();
        var m = new WorldEntityManager(types);
        var rep = new WorldReplica();
        Relay(m, rep);
        var enc = new WorldSnapshotEncoder(types);
        var dummies = new List<Dummy>();
        for (var i = 0; i < 3; i++) { var d = new Dummy { Pos = new Vector3(i, 1, 0) }; dummies.Add(d); m.Spawn(d); }
        Send(enc, rep, 0, m);
        Assert.Equal(0u, rep.AckTick);

        // the client's acknowledgements stop while the server runs past the history depth (its last ack is now too old to keep)
        for (uint tick = 1; tick <= WorldHistory.Depth + 4; tick++)
        {
            foreach (var d in dummies) d.Pos += new Vector3(1, 0, 0);
            enc.Encode(tick, Wire.NoTick, Entries(m)); // not delivered
        }

        foreach (var d in dummies) d.Pos += new Vector3(1, 0, 0);
        var packets = enc.Encode(WorldHistory.Depth + 5, rep.AckTick, Entries(m));
        Assert.Single(packets);
        Assert.Equal(Wire.NoTick, BaseOf(packets[0])); // full set: no baseline any more
        Assert.Equal(Wire.FKey, FirstFlags(packets[0]) & Wire.FKey);
        Assert.True(rep.Apply(packets[0]));
        Assert.Equal(WorldHistory.Depth + 5u, rep.AckTick);
        foreach (var (id, _) in rep.Entities) AssertMatches(rep.Entities[id].State, StateOf(m, id));
    }

    [Fact]
    public void DeltaAgainstABaselineTheClientDoesNotHoldIsRejected()
    {
        var types = Types();
        var enc = new WorldSnapshotEncoder(types);
        var m = new WorldEntityManager(types);
        var e = new Dummy();
        var id = m.Spawn(e)!.Value;
        enc.Encode(0, Wire.NoTick, Entries(m)); // the keyframe goes to a client that is not listening
        e.Pos = new Vector3(0.5f, 0, 0);
        var rep = new WorldReplica();
        var delta = enc.Encode(1, 0u, Entries(m)); // a delta against tick 0, which this client never committed
        Assert.Single(delta);
        Assert.False(rep.Apply(delta[0]));
        Assert.False(rep.HasState);
        Assert.False(rep.Entities.ContainsKey(id));
    }

    [Fact]
    public void RemovedEntityLeavesTheBaselineAndTheClientState()
    {
        var types = Types();
        var (m, _) = Manager(types);
        var enc = new WorldSnapshotEncoder(types);
        var rep = new WorldReplica();
        Relay(m, rep);
        var a = m.Spawn(new Dummy())!.Value;
        var b = m.Spawn(new Dummy())!.Value;
        Send(enc, rep, 0, m);
        Assert.True(rep.States.ContainsKey(a));

        m.Despawn(a, WorldDespawnReason.Removed); // the reliable event removes the entity at once
        var packets = Send(enc, rep, 1, m);
        Assert.Single(packets); // b is unchanged and omitted; a is a removal record
        Assert.Equal(Wire.FRemove, FirstFlags(packets[0]));
        Assert.False(rep.States.ContainsKey(a));
        Assert.True(rep.States.ContainsKey(b));
        Assert.False(rep.Entities.ContainsKey(a));
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
        var keyframe = enc.Encode(5, Wire.NoTick, Entries(m));

        var rep = new WorldReplica();
        Assert.True(rep.Apply(keyframe[0])); // unreliable arrives first: committed, but the entity is not spawned yet
        Assert.False(rep.Entities.ContainsKey(id));
        Assert.True(rep.States.ContainsKey(id));
        rep.OnEvent(EventKind.WorldSpawn, spawnPayload);
        Assert.True(rep.Entities.ContainsKey(id));
        AssertMatches(rep.Entities[id].State, StateOf(m, id));
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
        var inFlight = enc.Encode(3, Wire.NoTick, Entries(m)); // sent before the despawn, delivered after it
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
