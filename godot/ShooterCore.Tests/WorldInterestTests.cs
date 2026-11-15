using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Distance interest on the world channel (ADR 0024): the encoder's per-client relevance and the replica's dormant entities, end to end.</summary>
public class WorldInterestTests
{
    const byte Animal = 20, Plain = 21;

    sealed class Rig
    {
        public readonly WorldEntityRegistry Types = new();
        public readonly WorldSnapshotEncoder Enc;
        public readonly WorldReplica Rep;
        public readonly List<WorldEntry> World = new();
        public Vector3 Viewer = Vector3.Zero;
        public uint Tick;
        ushort _next;

        public Rig()
        {
            StateMachineDiagnostics.Strict = true;
            Types.Register(new WorldEntityDef(Animal, "test animal", HasHp: true, Interest: true));
            Types.Register(new WorldEntityDef(Plain, "test plain", HasHp: false));
            Enc = new WorldSnapshotEncoder(Types);
            Rep = new WorldReplica(Types);
        }

        public static WorldState At(byte type, float x, float z = 0) => WorldState.Make(new Vector3(x, 1, z), 0.5f, 0, 0, 1, type == Animal ? 80 : 0); // hp is only on the wire for types that have it

        /// <summary>Spawns an entity at <paramref name="x"/> metres from the viewer along x; the spawn event reaches the client at once.</summary>
        public ushort Add(byte type, float x, float z = 0)
        {
            var id = ++_next;
            var s = At(type, x, z);
            World.Add(new WorldEntry(id, type, s));
            Rep.OnEvent(EventKind.WorldSpawn, WorldCodec.EncodeSpawn(id, type, s));
            return id;
        }

        public void Place(ushort id, float x, float z = 0)
        {
            var i = World.FindIndex(w => w.Id == id);
            World[i] = World[i] with { State = At(World[i].Type, x, z) };
        }

        public void Despawn(ushort id)
        {
            World.RemoveAll(w => w.Id == id);
            Rep.OnEvent(EventKind.WorldDespawn, WorldCodec.EncodeDespawn(id, WorldDespawnReason.Removed));
        }

        /// <summary>One server tick for this client. Packets are delivered unless <paramref name="lost"/>.</summary>
        public List<byte[]> Step(bool lost = false)
        {
            var packets = Enc.Encode(++Tick, Rep.AckTick, World, Viewer);
            if (!lost) foreach (var p in packets) Rep.Apply(p);
            return packets;
        }

        public void Settle(int ticks = 3) { for (var i = 0; i < ticks; i++) Step(); }
    }

    /// <summary>(id, flags) of every record of the packets.</summary>
    static List<(ushort Id, byte Flags)> Records(List<byte[]> packets)
    {
        var list = new List<(ushort, byte)>();
        foreach (var p in packets)
        {
            var o = WorldSnapshotEncoder.HeaderBytes;
            for (var i = 0; i < p[11]; i++)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o));
                var f = p[o + 3];
                o += 4;
                if ((f & Wire.FPosAbs) != 0) o += 12;
                if ((f & Wire.FPosDelta) != 0) o += 6;
                if ((f & Wire.FRot) != 0) o += 4;
                if ((f & Wire.FStatus) != 0) o += 3;
                if ((f & Wire.FHp) != 0) o += 1;
                list.Add((id, f));
            }
        }
        return list;
    }

    static byte FlagsOf(List<byte[]> packets, ushort id)
    {
        foreach (var (rid, f) in Records(packets)) if (rid == id) return f;
        return 0xFF; // no record
    }

    const byte NoRecord = 0xFF;

    static void AssertShownAt(Rig r, ushort id)
    {
        var e = r.Rep.Entities[id];
        Assert.False(e.Dormant);
        Assert.Equal(r.World.Find(w => w.Id == id).State, e.State);
    }

    [Fact]
    public void AnimalNeverInRangeIsNeverSentAndTheClientKeepsItHidden()
    {
        var r = new Rig();
        r.Add(Plain, 10); // something to keep the baseline going
        r.Settle();
        var far = r.Add(Animal, 2000);
        Assert.True(r.Rep.Entities[far].Dormant); // hidden from its spawn, before any snapshot
        r.Place(far, 2001);
        for (var i = 0; i < 4; i++)
        {
            var packets = r.Step();
            Assert.Equal(NoRecord, FlagsOf(packets, far));
            Assert.True(r.Rep.Entities[far].Dormant);
            Assert.Equal(0, r.Rep.Entities[far].Buf.Count);
        }
    }

    [Fact]
    public void EnteringKeyframesUntilAckedThenDeltas()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 900);
        r.Settle();
        Assert.True(r.Rep.Entities[a].Dormant);

        r.Place(a, 700);
        Assert.Equal(Wire.FKey | Wire.FHp, FlagsOf(r.Step(lost: true), a)); // lost: the client has not acknowledged it
        r.Place(a, 701);
        Assert.Equal(Wire.FKey | Wire.FHp, FlagsOf(r.Step(), a)); // keyframe again, now delivered
        AssertShownAt(r, a);
        Assert.Equal(1, r.Rep.Entities[a].Buf.Count); // no slide from the old pose

        r.Place(a, 702); // acknowledged: back to deltas
        var flags = FlagsOf(r.Step(), a);
        Assert.Equal(Wire.FPosDelta, flags);
        AssertShownAt(r, a);
    }

    [Fact]
    public void KeyframesContinueWhileTheAckLagsBehindTheFirstKeyframe()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 900);
        r.Settle();
        r.Place(a, 700);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Wire.FKey | Wire.FHp, FlagsOf(r.Step(lost: true), a));
            r.Place(a, 700 + i + 1);
        }
        Assert.Equal(Wire.FKey | Wire.FHp, FlagsOf(r.Step(), a));
        AssertShownAt(r, a);
    }

    [Fact]
    public void LeavingIsDormantUntilAckedThenSilentWithoutRemoval()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 700);
        r.Settle();
        AssertShownAt(r, a);

        r.Place(a, 900);
        Assert.Equal(Wire.FDormant, FlagsOf(r.Step(lost: true), a));
        Assert.False(r.Rep.Entities[a].Dormant); // lost
        Assert.Equal(Wire.FDormant, FlagsOf(r.Step(), a)); // sent again until acknowledged
        Assert.True(r.Rep.Entities[a].Dormant);

        for (var i = 0; i < 4; i++)
        {
            r.Place(a, 900 + i);
            var packets = r.Step();
            Assert.Equal(NoRecord, FlagsOf(packets, a)); // silent: no dormant, no removal
        }
        Assert.True(r.Rep.Entities.ContainsKey(a)); // still alive
        Assert.True(r.Rep.Entities[a].Dormant);
    }

    [Fact]
    public void HysteresisKeepsAnEntityAtTheEdgeFromFlapping()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 790);
        r.Settle(); // in
        foreach (var x in new[] { 820f, 845f, 805f, 849f })
        {
            r.Place(a, x);
            Assert.NotEqual(Wire.FDormant, FlagsOf(r.Step(), a) & Wire.FDormant); // still in: delta (never a dormant record)
            AssertShownAt(r, a);
        }
        r.Place(a, 852);
        Assert.Equal(Wire.FDormant, FlagsOf(r.Step(), a)); // out past 850
        Assert.True(r.Rep.Entities[a].Dormant);
        r.Settle();
        foreach (var x in new[] { 840f, 820f, 801f })
        {
            r.Place(a, x);
            Assert.Equal(NoRecord, FlagsOf(r.Step(), a)); // out until it is inside 800
            Assert.True(r.Rep.Entities[a].Dormant);
        }
        r.Place(a, 799);
        Assert.Equal(Wire.FKey | Wire.FHp, FlagsOf(r.Step(), a));
        AssertShownAt(r, a);
    }

    [Fact]
    public void ReEnteringAfterMovingFarWhileDormantShowsTheNewPoseNotAStaleDelta()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 700);
        r.Settle();
        r.Place(a, 1500);
        r.Settle(4); // dormant, then silent
        Assert.True(r.Rep.Entities[a].Dormant);

        r.Place(a, 2500, 300); // keeps wandering while hidden (a jump beyond a delta)
        r.Settle(2);
        r.Place(a, 600, -40);
        var flags = FlagsOf(r.Step(), a);
        Assert.Equal(Wire.FKey | Wire.FHp, flags);
        AssertShownAt(r, a);
        Assert.Equal(1, r.Rep.Entities[a].Buf.Count);
        r.Place(a, 601, -40);
        r.Settle(2);
        AssertShownAt(r, a);
    }

    [Fact]
    public void DespawnWhileDormantIsCleanedUp()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 700);
        var b = r.Add(Animal, 5000);
        r.Settle();
        r.Place(a, 1200);
        r.Settle(4); // a is silent, b never was in range
        Assert.Equal(2, r.Enc.FilteredTracked); // a: silent; b: told it is hidden at the join, now silent

        r.Despawn(a);
        r.Despawn(b);
        r.Settle(2);
        Assert.Equal(0, r.Enc.FilteredTracked);
        Assert.False(r.Rep.Entities.ContainsKey(a));
        Assert.False(r.Rep.Entities.ContainsKey(b));
        Assert.False(r.Rep.States.ContainsKey(a));
    }

    [Fact]
    public void UnfilteredTypesAreSentAtAnyRange()
    {
        var r = new Rig();
        var p = r.Add(Plain, 5000);
        Assert.False(r.Rep.Entities[p].Dormant);
        Assert.Equal(Wire.FKey, FlagsOf(r.Step(), p));
        AssertShownAt(r, p);
        r.Place(p, 5001);
        Assert.Equal(Wire.FPosDelta, FlagsOf(r.Step(), p));
        r.Place(p, -9000);
        r.Step();
        AssertShownAt(r, p);
        Assert.Equal(0, r.Enc.FilteredTracked);
    }

    [Fact]
    public void WithoutAViewerNothingIsFiltered()
    {
        var types = new WorldEntityRegistry();
        types.Register(new WorldEntityDef(Animal, "test animal", HasHp: true, Interest: true));
        var enc = new WorldSnapshotEncoder(types);
        var packets = enc.Encode(1, Wire.NoTick, new[] { new WorldEntry(1, Animal, Rig.At(Animal, 9000)) });
        Assert.Equal(Wire.FKey | Wire.FHp, Records(packets)[0].Flags);
    }

    [Fact]
    public void JoinFullSetKeyframesNearEntitiesAndHidesTheFarOnes()
    {
        var r = new Rig();
        var near = r.Add(Animal, 300);
        var far = r.Add(Animal, 3000);
        var first = r.Step();
        Assert.Equal(Wire.NoTick, BinaryPrimitives.ReadUInt32LittleEndian(first[0].AsSpan(5)));
        AssertShownAt(r, near);
        Assert.True(r.Rep.Entities[far].Dormant);
        r.Settle();
        Assert.True(r.Rep.Entities[far].Dormant);
        AssertShownAt(r, near);
    }

    [Fact]
    public void ABaselineThatFellOutOfTheHistoryHidesWhatLeftRangeMeanwhile()
    {
        var r = new Rig();
        r.Add(Plain, 0);
        var a = r.Add(Animal, 700);
        r.Step(); // the join's full set, the only ack the client will ever send
        AssertShownAt(r, a);
        r.Place(a, 2000);
        // the client's acknowledgements stop while the server runs past the history depth
        for (var i = 0; i < WorldHistory.Depth + 4; i++) r.Enc.Encode(++r.Tick, Wire.NoTick, r.World, r.Viewer);
        var packets = r.Step(); // the ack is gone from the history: a full set
        Assert.Equal(Wire.NoTick, BinaryPrimitives.ReadUInt32LittleEndian(packets[0].AsSpan(5)));
        Assert.True(r.Rep.Entities[a].Dormant);
        r.Settle();
        Assert.True(r.Rep.Entities[a].Dormant);
    }

    [Fact]
    public void LossyDeliveryOfAMovingCrowdAlwaysConvergesToTheServerView()
    {
        var r = new Rig();
        var rng = new Random(7);
        var ids = new List<ushort>();
        var pos = new Dictionary<ushort, Vector2>();
        for (var i = 0; i < 90; i++)
        {
            var id = r.Add(i % 9 == 0 ? Plain : Animal, 0);
            ids.Add(id);
            pos[id] = new Vector2(rng.Next(-1200, 1200), rng.Next(-1200, 1200));
        }
        for (var t = 0; t < 400; t++)
        {
            foreach (var id in ids)
            {
                var v = pos[id] + new Vector2(rng.Next(-14, 15), rng.Next(-14, 15)); // up to 14 m a tick: crosses the band
                pos[id] = v;
                r.Place(id, v.X, v.Y);
            }
            r.Viewer = new Vector3(MathF.Sin(t / 40f) * 300, 0, 0);
            var packets = r.Enc.Encode(++r.Tick, r.Rep.AckTick, r.World, r.Viewer);
            foreach (var p in packets) if (rng.NextDouble() > 0.3) r.Rep.Apply(p); // each packet is lost on its own
        }
        r.Settle(6); // the link recovers
        foreach (var w in r.World)
        {
            var e = r.Rep.Entities[w.Id];
            var d = MathF.Sqrt((w.State.Position.X - r.Viewer.X) * (w.State.Position.X - r.Viewer.X) + w.State.Position.Z * w.State.Position.Z);
            if (w.Type == Plain || d <= WorldInterest.EnterM) { Assert.False(e.Dormant); Assert.Equal(w.State, e.State); }
            else if (d > WorldInterest.LeaveM) Assert.True(e.Dormant);
        }
    }

    [Fact]
    public void TheInterestBandIsTheAgreedNumbers()
    {
        Assert.Equal(800f, WorldInterest.EnterM);
        Assert.Equal(850f, WorldInterest.LeaveM);
        Assert.True(Wire.ProtocolVersion >= 55);
    }
}
