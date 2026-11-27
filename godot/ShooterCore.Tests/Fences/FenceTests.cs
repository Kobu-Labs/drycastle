using System.Numerics;
using Drycastle.ShooterCore.Fences;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Fences;

/// <summary>Shared fixtures: a straight 4 m picket along +x from the origin (16 pieces of 0.25 m) and a wall.</summary>
static class Fx
{
    public static readonly FenceDestructionConfig Cfg = FenceDestructionConfig.Default;

    public static FenceSectionInfo Thin(int index = 0, FenceKind kind = FenceKind.Picket, Vector2? a = null, Vector2? b = null, int pieces = 16, float pieceM = 0.25f) => new()
    {
        Index = (ushort)index, Kind = kind, A = a ?? new Vector2(0, 0), B = b ?? new Vector2(4, 0), ZA = 0, ZB = 0, HeightM = 1.2f, ThicknessM = 0.06f,
        Pieces = pieces, PieceM = pieceM,
    };

    public static FenceSectionInfo Wall(int index = 0) => new() { Index = (ushort)index, Kind = FenceKind.Wall, A = new Vector2(0, 0), B = new Vector2(4, 0), HeightM = 2, ThicknessM = 0.3f };

    public static (IReadOnlyList<FenceSectionInfo> S, FenceIndex I, FenceState St) Make(params FenceSectionInfo[] s) => (s, new FenceIndex(s), new FenceState(s, Cfg));
}

public class FenceFileTests
{
    [Fact]
    public void Parses_axes_kind_and_pieces()
    {
        var s = FenceFile.Parse("""
        {"version":1,"fences":[
          {"a":[1,2],"b":[5,2],"za":3,"zb":4,"height_m":1.5,"type":"picket","kind":"picket","thickness_m":0.1,"colour":"#112233","pieces":32,"piece_m":0.125,"piece0_m":-0.05,"uv":[0,2,-0.1,1.5],"collider":"props_collision@0,0","group":"G-1"},
          {"a":[0,0],"b":[2,0],"za":0,"type":"brick_red","kind":"wall","grid":"fence_00001"}]}
        """);
        Assert.Equal(2, s.Count);
        var t = s[0];
        Assert.Equal(FenceKind.Picket, t.Kind);
        Assert.Equal(new Vector2(1, -2), t.A);
        Assert.Equal(new Vector2(5, -2), t.B);
        Assert.Equal((3f, 4f, 1.5f, 0.1f), (t.ZA, t.ZB, t.HeightM, t.ThicknessM));
        Assert.Equal(0x112233u, t.Tint);
        Assert.Equal((32, 0.125f, -0.05f), (t.Pieces, t.PieceM, t.Piece0M));
        Assert.Equal(new Vector4(0, 2, -0.1f, 1.5f), t.Uv);
        Assert.Equal("props_collision@0,0", t.Collider);
        Assert.True(t.Thin);
        Assert.Equal(FenceKind.Wall, s[1].Kind);
        Assert.Equal("fence_00001", s[1].Grid);
        Assert.Equal(0, s[1].Pieces);
        Assert.False(s[1].Thin);
    }

    [Theory]
    [InlineData("picket", FenceKind.Picket)]
    [InlineData("wire", FenceKind.Wire)]
    [InlineData(null, FenceKind.Plain)]
    [InlineData("brick_red", FenceKind.Wall)]
    [InlineData("brick_white", FenceKind.Wall)]
    [InlineData("brick_wall", FenceKind.Wall)]
    [InlineData("stone_wall", FenceKind.Wall)]
    [InlineData("concrete", FenceKind.Wall)]
    public void Older_map_without_kind_uses_type(string? type, FenceKind kind)
    {
        var ty = type == null ? "" : $",\"type\":\"{type}\"";
        var s = FenceFile.Parse($$"""{"version":1,"fences":[{"a":[0,0],"b":[1,0],"za":0,"zb":0,"height_m":1{{ty}}}]}""")[0];
        Assert.Equal(kind, s.Kind);
        Assert.Equal(0, s.Pieces);
        Assert.Equal(0.06f, s.ThicknessM);
        Assert.Equal(0x9a8064u, s.Tint);
    }

    [Fact]
    public void Broken_entry_keeps_its_index()
    {
        var s = FenceFile.Parse("""{"version":1,"fences":[{"a":[0,0]},7,{"a":[0,0],"b":[1,0],"za":0,"height_m":1,"pieces":4,"piece_m":0.25}]}""");
        Assert.Equal(3, s.Count);
        Assert.Equal((ushort)1, s[1].Index);
        Assert.Equal(FenceKind.Plain, s[0].Kind);
        Assert.Equal(0, s[0].Pieces);
        Assert.Equal(0, s[1].Pieces);
        Assert.Equal(4, s[2].Pieces);
    }

    [Fact]
    public void Unknown_version_or_no_list_is_empty_and_bad_json_throws()
    {
        Assert.Empty(FenceFile.Parse("""{"version":2,"fences":[{"a":[0,0],"b":[1,0]}]}"""));
        Assert.Empty(FenceFile.Parse("""{"version":1}"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => FenceFile.Parse("{"));
    }

    [Fact]
    public void Posts_are_read_with_the_piece_they_break_with()
    {
        var s = FenceFile.Parse("""{"version":1,"fences":[{"a":[0,0],"b":[2,0],"za":0,"height_m":1,"kind":"picket","pieces":16,"piece_m":0.125,"piece0_m":-0.05,"colour":"#102030","posts":[[0,0.1,-0.1,1.04,"#aabbcc"],[2,0.1,-0.1,1.04,"bad"],[1.0,0.1,-0.1,1.04],[0.3]]}]}""")[0];
        Assert.Equal(2, s.Posts.Count);
        Assert.Equal(new FencePost(0, 0.1f, -0.1f, 1.04f, 0xaabbcc, 0), s.Posts[0]);
        Assert.Equal(15, s.Posts[1].Piece);
        Assert.Equal(0x102030u, s.Posts[1].Colour);
    }
}

public class FenceIndexTests
{
    [Fact]
    public void Near_lists_each_section_once_and_only_near_ones()
    {
        var s = new[] { Fx.Thin(0), Fx.Thin(1, a: new Vector2(100, 100), b: new Vector2(104, 100)), Fx.Thin(2, kind: FenceKind.Wire, a: new Vector2(0, 3), b: new Vector2(30, 3)), Fx.Wall(3) };
        var idx = new FenceIndex(s);
        var l = new List<int>();
        idx.Near(new Vector2(2, 1), 2.5f, l);
        Assert.Equal([0, 2], l);
        l.Clear();
        idx.Near(new Vector2(102, 99), 1.5f, l);
        Assert.Equal([1], l);
        l.Clear();
        idx.Near(new Vector2(50, 50), 5, l);
        Assert.Empty(l);
    }
}

public class FenceStateTests
{
    [Fact]
    public void Damage_breaks_a_piece_after_its_hit_points()
    {
        var (_, _, st) = Fx.Make(Fx.Thin());
        Assert.Equal(100, st.Hp(0, 3));
        Assert.False(st.Damage(0, 3, 50));
        Assert.Equal(50, st.Hp(0, 3));
        Assert.Equal(100, st.Hp(0, 4));
        Assert.False(st.Damaged);
        Assert.True(st.Damage(0, 3, 50));
        Assert.True(st.Damaged);
        Assert.Equal(1UL << 3, st.Broken(0));
        Assert.Equal(0, st.Hp(0, 3));
        Assert.False(st.Damage(0, 3, 50));
        Assert.Equal([0], st.BrokenSections());
    }

    [Fact]
    public void Walls_pieceless_and_out_of_range_never_break()
    {
        var (_, _, st) = Fx.Make(Fx.Wall(0), Fx.Thin(1, pieces: 0), Fx.Thin(2));
        Assert.False(st.Damage(0, 0, 1000));
        Assert.False(st.Damage(1, 0, 1000));
        Assert.False(st.Damage(2, 16, 1000));
        Assert.False(st.Damage(2, -1, 1000));
        Assert.False(st.Damage(9, 0, 1000));
        Assert.False(st.Damaged);
    }

    [Fact]
    public void SetBroken_is_exact_and_Restore_makes_whole()
    {
        var (_, _, st) = Fx.Make(Fx.Thin(0), Fx.Thin(1));
        st.SetBroken(1, 0b101);
        Assert.Equal(0b101UL, st.Broken(1));
        Assert.Equal([1], st.BrokenSections());
        st.SetBroken(1, 0b100);
        Assert.Equal(0b100UL, st.Broken(1));
        Assert.Equal(100, st.Hp(1, 0));
        st.SetBroken(0, ulong.MaxValue); // 16 pieces only
        Assert.Equal(0xFFFFUL, st.Broken(0));
        st.Damage(1, 7, 100);
        st.Restore();
        Assert.False(st.Damaged);
        Assert.Empty(st.BrokenSections());
        Assert.Equal(100, st.Hp(1, 7));
    }

    [Fact]
    public void Wire_has_its_own_hit_points()
    {
        var cfg = FenceDestructionConfig.Default;
        Assert.Equal(20, cfg.Of(FenceKind.Wire).BulletDamage);
        var s = new[] { Fx.Thin(kind: FenceKind.Wire) };
        var st = new FenceState(s, FenceDestructionConfig.Parse("""{"version":1,"kinds":{"plain":{"piece_hp":10},"picket":{"piece_hp":10},"wire":{"piece_hp":30}}}"""));
        Assert.Equal(30, st.Hp(0, 0));
    }
}

public class FenceRulesTests
{
    static List<(ushort, byte)> Path(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState st, Vector3 from, Vector3 to)
    {
        var into = new List<(ushort, byte)>();
        FenceRules.PathPieces(s, idx, st, from, Vector3.Normalize(to - from), Vector3.Distance(from, to), into);
        return into;
    }

    [Fact]
    public void PathPieces_takes_every_piece_a_round_flies_through_nearest_first()
    {
        var (s, idx, st) = Fx.Make(Fx.Thin(0), Fx.Thin(1, a: new Vector2(0, 2), b: new Vector2(4, 2)));
        Assert.Equal([((ushort)0, (byte)4), ((ushort)1, (byte)4)], Path(s, idx, st, new Vector3(1.1f, 0.5f, -5), new Vector3(1.1f, 0.5f, 5)));
        Assert.Equal([((ushort)1, (byte)4), ((ushort)0, (byte)4)], Path(s, idx, st, new Vector3(1.1f, 0.5f, 5), new Vector3(1.1f, 0.5f, -5)));
    }

    [Fact]
    public void PathPieces_misses_short_high_parallel_and_broken()
    {
        var (s, idx, st) = Fx.Make(Fx.Thin());
        Assert.Empty(Path(s, idx, st, new Vector3(1.1f, 0.5f, -5), new Vector3(1.1f, 0.5f, -1)));   // stops before the fence
        Assert.Empty(Path(s, idx, st, new Vector3(1.1f, 1.5f, -5), new Vector3(1.1f, 1.5f, 5)));    // over its top (1.2)
        Assert.Empty(Path(s, idx, st, new Vector3(5, 0.5f, -5), new Vector3(5, 0.5f, 5)));          // past its end
        Assert.Empty(Path(s, idx, st, new Vector3(-1, 0.5f, 0), new Vector3(5, 0.5f, 0)));          // along it
        st.SetBroken(0, 1UL << 4);
        Assert.Empty(Path(s, idx, st, new Vector3(1.1f, 0.5f, -5), new Vector3(1.1f, 0.5f, 5)));    // that piece is down already
    }

    [Fact]
    public void PathPieces_ignores_walls_and_pieceless_and_walks_long_diagonal_paths()
    {
        var (s, idx, st) = Fx.Make(Fx.Wall(0), Fx.Thin(1, pieces: 0), Fx.Thin(2, a: new Vector2(400, 0), b: new Vector2(404, 0)));
        Assert.Empty(Path(s, idx, st, new Vector3(1.1f, 0.5f, -5), new Vector3(1.1f, 0.5f, 5)));
        Assert.Equal([((ushort)2, (byte)8)], Path(s, idx, st, new Vector3(402.1f, 0.5f, -300), new Vector3(402.1f, 0.5f, 300)));
        Assert.Equal([((ushort)2, (byte)8)], Path(s, idx, st, new Vector3(353.2f, 0.5f, -48.9f), new Vector3(453.2f, 0.5f, 51.1f))); // crosses z = 0 at x = 402.1, through many cells
    }

    [Fact]
    public void Blast_hits_standing_pieces_with_falloff()
    {
        var (s, idx, st) = Fx.Make(Fx.Thin());
        st.SetBroken(0, 1UL << 8);
        var hits = new List<PieceHit>();
        var mid = s[0].PieceCentre(7);
        FenceRules.Blast(s, idx, st, mid, 1f, 400, hits);
        Assert.DoesNotContain(hits, h => h.Piece == 8);
        Assert.Equal(400, hits.Single(h => h.Piece == 7).Damage);
        var p6 = hits.Single(h => h.Piece == 6).Damage;
        Assert.Equal((int)MathF.Round(400 * (1 - 0.25f / 1f)), p6);
        Assert.DoesNotContain(hits, h => h.Piece == 2);
        Assert.All(hits, h => Assert.True(h.Damage >= 1));
    }

    [Fact]
    public void Blast_far_away_or_zero_radius_does_nothing()
    {
        var (s, idx, st) = Fx.Make(Fx.Thin(), Fx.Wall(1));
        var hits = new List<PieceHit>();
        FenceRules.Blast(s, idx, st, new Vector3(2, 0.6f, 10), 5, 400, hits);
        FenceRules.Blast(s, idx, st, new Vector3(2, 0.6f, 0), 0, 400, hits);
        Assert.Empty(hits);
    }

    // a car 1.8 x 4 m facing -z (yaw 0), its front at z = frontZ, x centred at cx
    static FenceRamBox Car(float cx, float frontZ, float yaw = 0) => new(new Vector3(cx, 0, frontZ + 2), yaw, 0.9f, 2, 1.5f);

    [Fact]
    public void Ram_touches_the_pieces_in_front_of_the_box()
    {
        var (s, idx, st) = Fx.Make(Fx.Thin(a: new Vector2(0, -3), b: new Vector2(4, -3)));
        var got = new List<(ushort, byte)>();
        // front at z = -2.8, driving -z (north): the probe 0.5 m reaches the fence at z = -3
        FenceRules.RamPieces(s, idx, st, Car(2, -2.8f), -Vector3.UnitZ, 0.5f, got);
        Assert.Equal(Enumerable.Range(4, 8).Select(j => ((ushort)0, (byte)j)), got); // x in [1.1, 2.9]
        got.Clear();
        FenceRules.RamPieces(s, idx, st, Car(2, -2.0f), -Vector3.UnitZ, 0.5f, got); // 1 m short
        Assert.Empty(got);
        got.Clear();
        FenceRules.RamPieces(s, idx, st, Car(2, -2.8f), Vector3.UnitZ, 0.5f, got); // driving away
        Assert.Empty(got);
    }

    [Fact]
    public void Ram_skips_broken_pieces_and_respects_height()
    {
        var (s, idx, st) = Fx.Make(Fx.Thin(a: new Vector2(0, -3), b: new Vector2(4, -3)));
        st.SetBroken(0, 1UL << 8);
        var got = new List<(ushort, byte)>();
        FenceRules.RamPieces(s, idx, st, Car(2, -2.8f), -Vector3.UnitZ, 0.5f, got);
        Assert.DoesNotContain(got, g => g.Item2 == 8);
        Assert.Equal(7, got.Count);
        got.Clear();
        var above = new FenceRamBox(new Vector3(2, 1.3f, -0.8f), 0, 0.9f, 2, 1.5f); // feet above the top (1.2 + 0.0)
        FenceRules.RamPieces(s, idx, st, above, -Vector3.UnitZ, 0.5f, got);
        Assert.Empty(got);
    }

    [Fact]
    public void Ram_follows_the_yaw()
    {
        // yaw +90 deg turns left: faces -x. A fence along z at x = -3 is hit, one at x = +3 is not.
        var (s, idx, st) = Fx.Make(Fx.Thin(0, a: new Vector2(-3, -2), b: new Vector2(-3, 2)), Fx.Thin(1, a: new Vector2(3, -2), b: new Vector2(3, 2)));
        var got = new List<(ushort, byte)>();
        var car = new FenceRamBox(new Vector3(-0.8f, 0, 0), MathF.PI / 2, 1, 2, 1.5f); // front at x = -2.8
        FenceRules.RamPieces(s, idx, st, car, -Vector3.UnitX, 0.5f, got);
        Assert.NotEmpty(got);
        Assert.All(got, g => Assert.Equal(0, g.Item1));
    }
}

public class FenceGeometryTests
{
    [Fact]
    public void Runs_split_at_broken_pieces()
    {
        var s = Fx.Thin();
        var runs = new List<(float From, float To)>();
        FenceGeometry.StandingRuns(s, 0, runs);
        Assert.Equal([(0f, 4f)], runs);
        runs.Clear();
        FenceGeometry.StandingRuns(s, (1UL << 2) | (1UL << 3) | (1UL << 15), runs);
        Assert.Equal([(0f, 0.5f), (1f, 3.75f)], runs);
        runs.Clear();
        FenceGeometry.StandingRuns(s, 0xFFFF, runs);
        Assert.Empty(runs);
    }

    [Fact]
    public void Runs_of_a_pieceless_section_are_all_of_it_and_of_a_wall_none()
    {
        var runs = new List<(float From, float To)>();
        FenceGeometry.StandingRuns(Fx.Thin(pieces: 0), ulong.MaxValue, runs);
        Assert.Equal([(0f, 4f)], runs);
        runs.Clear();
        FenceGeometry.StandingRuns(Fx.Wall(), 0, runs);
        Assert.Empty(runs);
    }

    [Fact]
    public void Clipped_end_pieces_stay_inside_the_section()
    {
        var s = Fx.Thin(pieces: 9, pieceM: 0.5f) with { Piece0M = -0.25f }; // 4 m: pieces [-0.25, .25] ... [3.75, 4.25]
        var runs = new List<(float From, float To)>();
        FenceGeometry.StandingRuns(s, 1UL | 1UL << 8, runs);
        Assert.Equal([(0.25f, 3.75f)], runs);
    }

    [Fact]
    public void Collider_boxes_are_closed_outward_and_sized()
    {
        var s = Fx.Thin();
        var tris = new List<Vector3>();
        FenceGeometry.ColliderTriangles(s, 1UL << 7 | 1UL << 8, tris); // two runs
        Assert.Equal(2 * 36, tris.Count);
        for (var run = 0; run < 2; run++)
        {
            var box = tris.Skip(run * 36).Take(36).ToList();
            var c = box.Aggregate(Vector3.Zero, (a, v) => a + v) / box.Count;
            for (var t = 0; t < 36; t += 3)
            {
                var n = Vector3.Cross(box[t + 1] - box[t], box[t + 2] - box[t]);
                Assert.True(n.Length() > 1e-6f);
                Assert.True(Vector3.Dot(n, box[t] - c) > 0);
            }
            Assert.Equal(-0.1f, box.Min(v => v.Y), 4);
            Assert.Equal(1.2f, box.Max(v => v.Y), 4);
            Assert.Equal(0.03f, box.Max(v => v.Z), 4);
            Assert.Equal(-0.03f, box.Min(v => v.Z), 4);
        }
        Assert.Equal(0f, tris.Take(36).Min(v => v.X), 4);
        Assert.Equal(1.75f, tris.Take(36).Max(v => v.X), 4);
        Assert.Equal(2.25f, tris.Skip(36).Min(v => v.X), 4);
        Assert.Equal(4f, tris.Skip(36).Max(v => v.X), 4);
    }

    [Fact]
    public void Nothing_standing_gives_no_triangles()
    {
        var tris = new List<Vector3>();
        FenceGeometry.ColliderTriangles(Fx.Thin(), 0xFFFF, tris);
        Assert.Empty(tris);
    }
}

public class FenceCodecTests
{
    [Fact]
    public void Break_round_trips_in_13_bytes()
    {
        var m = new FenceBreakMsg(1234, 0xF00000000000FFFFUL, -127, 64, 200);
        var b = FenceCodec.EncodeBreak(m);
        Assert.Equal(FenceCodec.BreakBytes, b.Length);
        Assert.Equal(m, FenceCodec.DecodeBreak(b));
        Assert.Equal([0xD2, 0x04], b[..2]);
    }

    [Fact]
    public void Bad_break_is_null()
    {
        Assert.Null(FenceCodec.DecodeBreak(new byte[12]));
        Assert.Null(FenceCodec.DecodeBreak(new byte[14]));
        Assert.Null(FenceCodec.DecodeBreak([]));
    }

    [Fact]
    public void Sync_round_trips_and_splits_at_200_bytes()
    {
        var list = Enumerable.Range(0, 45).Select(i => ((ushort)(i * 3), 1UL << (i % 64))).ToList();
        var msgs = FenceCodec.EncodeSync(new FenceSyncMsg(true, list));
        Assert.Equal(3, msgs.Count);
        Assert.All(msgs, m => Assert.True(m.Length <= FenceCodec.MaxSyncBytes));
        var back = msgs.Select(m => FenceCodec.DecodeSync(m)!).ToList();
        Assert.True(back[0].Reset);
        Assert.False(back[1].Reset);
        Assert.False(back[2].Reset);
        Assert.Equal(list, back.SelectMany(m => m.Sections));
    }

    [Fact]
    public void Sync_edges()
    {
        var reset = FenceCodec.EncodeSync(new FenceSyncMsg(true, []));
        Assert.Single(reset);
        Assert.Equal([1, 0, 0], reset[0]);
        Assert.Empty(FenceCodec.EncodeSync(new FenceSyncMsg(false, [])));
        var one = FenceCodec.EncodeSync(new FenceSyncMsg(false, [(7, 5UL)]));
        Assert.Equal(13, one.Single().Length);
        Assert.False(FenceCodec.DecodeSync(one[0])!.Reset);
    }

    [Fact]
    public void Bad_sync_is_null()
    {
        Assert.Null(FenceCodec.DecodeSync([]));
        Assert.Null(FenceCodec.DecodeSync(new byte[] { 0, 1, 0, 1, 2 }));
        Assert.Null(FenceCodec.DecodeSync(new byte[] { 0, 0, 0, 9 }));
    }
}
