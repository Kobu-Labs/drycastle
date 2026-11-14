using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Holy Mass demon: data, anchor graph, hit count, crawl, fireball, wipe.</summary>
public class DemonTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static DemonFile Full => DemonFile.Load(Path.Combine(DataDir, "holy_mass_demon.json"));

    /// <summary>The shipped numbers with the roar, the leap and the flame off: the crawl and the fireball tests read the plain demon.</summary>
    static DemonFile Def
    {
        get { var d = Full; d.Roar.DurationS = 0; d.Leap.Chance = 0; d.Flame.DurationS = 0; d.Pounce.PinS = 0; return d; }
    }

    /// <summary>A hand-written room, 20 x 30 x 10 m about the origin, in the file's map axes (x east, y north, z up): three anchors on the west wall, two on the ceiling, three on the east wall. In the game's axes (x, z, -y) the ceiling is y = 10.</summary>
    public const string ChurchJson = """
        { "anchors": [
          { "id": 0, "pos": [-10, 10, 3],  "normal": [1, 0, 0],  "links": [1, 3] },
          { "id": 1, "pos": [-10, 0, 3],   "normal": [1, 0, 0],  "links": [2, 3] },
          { "id": 2, "pos": [-10, -10, 3], "normal": [1, 0, 0],  "links": [] },
          { "id": 3, "pos": [-5, 0, 10],   "normal": [0, 0, -1], "links": [4] },
          { "id": 4, "pos": [5, 0, 10],    "normal": [0, 0, -1], "links": [5] },
          { "id": 5, "pos": [10, 0, 3],    "normal": [-1, 0, 0], "links": [6, 7] },
          { "id": 6, "pos": [10, 10, 3],   "normal": [-1, 0, 0], "links": [] },
          { "id": 7, "pos": [10, -10, 3],  "normal": [-1, 0, 0], "links": [] }
        ] }
        """;

    static AnchorGraph Graph => AnchorGraph.FromJson(ChurchJson);
    static DemonPlayer P(ushort id, float x, float z, bool alive = true) => new(id, new Vector3(x, 0, z), alive);

    static DemonBrain Brain(int seed = 1, Func<Vector3, Vector3, bool>? sight = null, int start = 5) => new(Def, Graph, new Random(seed), sight, start);

    /// <summary>Steps at 30 Hz until <paramref name="stop"/> or <paramref name="seconds"/> pass; returns every event.</summary>
    static List<DemonEvent> Run(DemonBrain b, IReadOnlyList<DemonPlayer> players, float seconds, Func<DemonBrain, bool>? stop = null)
    {
        var ev = new List<DemonEvent>();
        for (var i = 0; i < (int)(seconds * 30); i++)
        {
            b.Step(1f / 30, players, ev);
            if (stop != null && stop(b)) break;
        }
        return ev;
    }

    // ---- data ----

    [Fact]
    public void The_shipped_file_loads()
    {
        var d = Def;
        Assert.True(d.Burn.Dps > 0 && d.Fireball.Damage > 0 && d.Crawl.DwellS.Length == 2);
        Assert.True(d.HitWeights.Explosive > d.HitWeights.Bullet);
    }

    [Fact]
    public void Hits_needed_scale_with_players_inside_and_are_clamped()
    {
        var d = Def;
        Assert.Equal(d.Hits.Min <= d.HitsNeeded(1) ? d.HitsNeeded(1) : d.Hits.Min, d.HitsNeeded(0));
        Assert.True(d.HitsNeeded(4) > d.HitsNeeded(2) && d.HitsNeeded(2) > d.HitsNeeded(1));
        Assert.Equal(d.Hits.Max, d.HitsNeeded(10_000));
        Assert.Equal((int)MathF.Round(d.Hits.Base + 2 * d.Hits.PerExtraPlayer), d.HitsNeeded(3));
    }

    [Fact]
    public void A_file_with_a_missing_section_is_refused()
    {
        var json = File.ReadAllText(Path.Combine(DataDir, "holy_mass_demon.json"));
        var e = Assert.Throws<DataFileException>(() => DemonFile.Parse(json.Replace("\"burn\"", "\"burnt\"")));
        Assert.Contains("burn.dps", e.Message);
        Assert.Throws<DataFileException>(() => DemonFile.Parse(json.Replace("[0.8, 2.2]", "[2.2, 0.8]")));
        Assert.Throws<DataFileException>(() => DemonFile.Parse(json.Replace("\"version\": 1", "\"version\": 99")));
    }

    // ---- anchors ----

    [Fact]
    public void Links_count_both_ways_and_normals_are_unit()
    {
        var g = Graph;
        Assert.Equal(8, g.Count);
        Assert.Equal(new[] { 0, 2, 3 }, g.Neighbours(1));
        Assert.Contains(4, g.Neighbours(5));
        Assert.All(g.Anchors, a => Assert.Equal(1f, a.Normal.Length(), 1e-5f));
    }

    [Fact]
    public void The_file_is_in_map_axes_and_comes_out_in_game_axes()
    {
        var g = AnchorGraph.FromJson("""{"anchors":[{"id":0,"pos":[1,2,3],"normal":[0,1,0],"links":[]}]}""");
        Assert.Equal(new Vector3(1, 3, -2), g[0].Pos);
        Assert.Equal(new Vector3(0, 0, -1), g[0].Normal); // north is -z
        Assert.Equal(new Vector3(0, 10, 0), Graph[3].Pos + new Vector3(5, 0, 0)); // the ceiling is up
        Assert.Equal(new Vector3(0, -1, 0), Graph[3].Normal);
    }

    [Fact]
    public void A_broken_graph_is_refused()
    {
        Assert.Throws<DataFileException>(() => AnchorGraph.FromJson("""{"anchors":[{"id":0,"pos":[0,0,0],"normal":[0,1,0],"links":[9]}]}"""));
        Assert.Throws<DataFileException>(() => AnchorGraph.FromJson("""{"anchors":[{"id":0,"pos":[0,0,0],"normal":[0,1,0],"links":[]},{"id":1,"pos":[1,0,0],"normal":[0,1,0],"links":[]}]}""")); // two islands
        Assert.Throws<DataFileException>(() => AnchorGraph.FromJson("""{"anchors":[{"id":0,"pos":[0,0,0],"normal":[0,0,0],"links":[]}]}"""));
        Assert.Throws<DataFileException>(() => AnchorGraph.FromJson("""{"anchors":[{"id":0,"pos":[0,0,0],"normal":[0,1,0]},{"id":0,"pos":[0,0,0],"normal":[0,1,0]}]}"""));
        Assert.Throws<DataFileException>(() => AnchorGraph.FromJson("""{"anchors":[]}"""));
    }

    // ---- hit count ----

    [Fact]
    public void It_dies_at_the_hits_needed_and_credits_everyone_who_hit_it()
    {
        var b = Brain();
        var ev = new List<DemonEvent>();
        var need = b.HitsNeeded;
        b.RegisterHit(1, DemonHitKind.Bullet, ev);
        b.RegisterHit(2, DemonHitKind.Bullet, ev);
        b.RegisterHit(1, DemonHitKind.Bullet, ev);
        Assert.Equal(3, b.HitsTaken);
        var last = DemonHitResult.Hit;
        while (b.Phase != DemonPhase.Dead) last = b.RegisterHit(1, DemonHitKind.Bullet, ev);
        Assert.Equal(DemonHitResult.Killed, last);
        Assert.Equal(need, b.HitsTaken);
        var died = Assert.Single(ev.OfType<DemonDied>());
        Assert.Equal(new ushort[] { 1, 2 }, died.Credited); // player 3 never hit it
        Assert.Equal(DemonHitResult.Ignored, b.RegisterHit(3, DemonHitKind.Bullet, ev));
        Assert.DoesNotContain((ushort)3, b.Credited);
    }

    [Fact]
    public void Explosive_and_vehicle_hits_weigh_more_than_a_bullet()
    {
        var b = Brain();
        var ev = new List<DemonEvent>();
        b.RegisterHit(1, DemonHitKind.Bullet, ev);
        b.RegisterHit(1, DemonHitKind.Explosive, ev);
        b.RegisterHit(1, DemonHitKind.Vehicle, ev);
        var d = Def;
        Assert.Equal(d.HitWeights.Bullet + d.HitWeights.Explosive + d.HitWeights.Vehicle, b.HitsTaken);
        Assert.Equal(d.HitWeights.Explosive, ev.OfType<DemonHurt>().ElementAt(1).Weight);
    }

    [Fact]
    public void More_players_inside_make_it_tougher_and_never_softer()
    {
        var b = Brain();
        var solo = b.HitsNeeded;
        Run(b, new[] { P(1, 0, 0), P(2, 1, 0), P(3, 2, 0) }, 0.1f);
        Assert.Equal(Def.HitsNeeded(3), b.HitsNeeded);
        Assert.True(b.HitsNeeded > solo);
        Run(b, new[] { P(1, 0, 0) }, 0.1f);
        Assert.Equal(Def.HitsNeeded(3), b.HitsNeeded);
    }

    // ---- wipe ----

    [Fact]
    public void A_wipe_heals_it_and_it_waits_then_wakes_for_the_next_player()
    {
        var b = Brain();
        var ev = new List<DemonEvent>();
        Run(b, new[] { P(1, 0, 0) }, 0.5f);
        Assert.NotEqual(DemonPhase.Waiting, b.Phase);
        b.RegisterHit(1, DemonHitKind.Explosive, ev);
        Assert.True(b.HitsTaken > 0);

        ev = Run(b, new[] { P(1, 0, 0, alive: false) }, 0.1f);
        Assert.Single(ev.OfType<DemonWiped>());
        Assert.Equal(0, b.HitsTaken);
        Assert.Empty(b.Credited);

        // someone walks in at once, but it keeps still (bar the end of a crawl) for the rearm delay
        var fire = Run(b, new[] { P(2, 0, 0) }, Def.Crawl.RearmDelayS - 1);
        Assert.Empty(fire.OfType<DemonTelegraph>());
        Assert.True(b.Phase is DemonPhase.Waiting);
        Run(b, new[] { P(2, 0, 0) }, 3);
        Assert.True(b.Phase is DemonPhase.Dwelling or DemonPhase.Crawling or DemonPhase.Telegraphing);
    }

    [Fact]
    public void Nobody_inside_is_not_a_wipe()
    {
        var b = Brain();
        var ev = Run(b, Array.Empty<DemonPlayer>(), 5);
        Assert.Empty(ev);
        Assert.Equal(DemonPhase.Waiting, b.Phase);
    }

    // ---- crawl ----

    [Fact]
    public void It_crawls_along_links_at_the_crawl_speed_and_stays_on_the_surface()
    {
        var g = Graph;
        var b = Brain();
        var players = new[] { P(1, -8, 8) };
        var prev = b.Pose.Position;
        var crawled = false;
        for (var i = 0; i < 30 * 40; i++)
        {
            b.Step(1f / 30, players, new List<DemonEvent>());
            var pose = b.Pose;
            Assert.True(Vector3.Distance(prev, pose.Position) <= Def.Crawl.SpeedMps / 30 + 1e-3f, "faster than the crawl speed");
            Assert.Equal(1f, pose.Normal.Length(), 1e-4f);
            Assert.Equal(1f, pose.Forward.Length(), 1e-4f);
            Assert.True(MathF.Abs(Vector3.Dot(pose.Forward, pose.Normal)) < 1e-3f, "forward lies in the surface");
            if (b.Phase == DemonPhase.Crawling)
            {
                crawled = true;
                Assert.True(b.Path.Count > 0 && g.Contains(b.Path[^1]));
                for (var k = 1; k < b.Path.Count; k++) Assert.Contains(b.Path[k], g.Neighbours(b.Path[k - 1]));
            }
            prev = pose.Position;
        }
        Assert.True(crawled);
    }

    [Fact]
    public void It_turns_to_its_target_at_rest_and_blends_normals_round_a_corner()
    {
        var b = Brain(start: 3); // ceiling
        var players = new[] { P(1, -5, 8) };
        Run(b, players, 3, x => x.Phase == DemonPhase.Dwelling);
        Assert.Equal(DemonPhase.Dwelling, b.Phase);
        Assert.Equal(new Vector3(0, -1, 0), b.Pose.Normal);
        var toTarget = Vector3.Normalize(new Vector3(0, 0, 8)); // straight to +z from (-5, 10, 0), in the ceiling plane
        Assert.True(Vector3.Dot(b.Pose.Forward, toTarget) > 0.99f);

        // crawling off the ceiling onto a wall turns its normal between the two
        var seen = new List<Vector3>();
        for (var i = 0; i < 30 * 30; i++) { b.Step(1f / 30, players, new List<DemonEvent>()); seen.Add(b.Pose.Normal); }
        Assert.Contains(seen, n => n.Y < -0.1f && n.Y > -0.99f && MathF.Abs(n.X) > 0.1f);
    }

    [Fact]
    public void It_keeps_to_goals_that_see_the_target_when_it_can()
    {
        // the target stands on the west side; only the west wall anchors (0 to 2) see it
        bool Sight(Vector3 a, Vector3 b) => a.X < 0 && b.X < 0;
        var b = Brain(sight: Sight, start: 5);
        var players = new[] { P(1, -8, 0) };
        var goals = new HashSet<int>();
        for (var i = 0; i < 30 * 60; i++)
        {
            b.Step(1f / 30, players, new List<DemonEvent>());
            if (b.Phase == DemonPhase.Crawling && b.Path.Count > 0) goals.Add(b.Path[^1]);
        }
        Assert.NotEmpty(goals);
        Assert.All(goals, g => Assert.True(Graph[g].Pos.X < 0 || g == 4, $"goal {g} cannot see the target")); // 4 is the way across
    }

    [Fact]
    public void The_same_seed_gives_the_same_demon()
    {
        List<(Vector3, DemonPhase)> Trace(int seed)
        {
            var b = Brain(seed);
            var players = new[] { P(1, -8, 8), P(2, 6, -6) };
            var t = new List<(Vector3, DemonPhase)>();
            for (var i = 0; i < 30 * 30; i++) { b.Step(1f / 30, players, new List<DemonEvent>()); t.Add((b.Pose.Position, b.Phase)); }
            return t;
        }
        Assert.Equal(Trace(7), Trace(7));
    }

    // ---- fireball ----

    [Fact]
    public void It_telegraphs_then_throws_a_fireball_that_lands_on_the_target()
    {
        var def = Def;
        var b = Brain(start: 3);
        var players = new[] { P(1, -5, 12) }; // standing still, 12 m from the ceiling anchor, in range
        var ev = new List<DemonEvent>();
        var fireAt = -1; var teleAt = -1;
        for (var i = 0; i < 30 * 8 && fireAt < 0; i++)
        {
            b.Step(1f / 30, players, ev);
            if (teleAt < 0 && ev.OfType<DemonTelegraph>().Any()) teleAt = i;
            if (ev.OfType<DemonFireballLaunched>().Any()) fireAt = i;
        }
        Assert.True(teleAt >= 0 && fireAt > teleAt);
        Assert.InRange((fireAt - teleAt) / 30f, def.Fireball.TelegraphS - 0.1f, def.Fireball.TelegraphS + 0.1f);
        Assert.Equal((ushort)1, ev.OfType<DemonTelegraph>().First().Target);

        // fly it on a flat floor and into the target's body
        var ball = ev.OfType<DemonFireballLaunched>().First().Ball;
        var hit = Fly(ball, def.Fireball, players, out var impact);
        Assert.Equal(FireballOutcome.Player, hit.Outcome);
        Assert.Equal((ushort)1, hit.Victim);
        Assert.True(Vector3.Distance(impact, new Vector3(-5, 1, 12)) < 1.5f);
    }

    static FireballStep Fly(Fireball ball, FireballDef def, IReadOnlyList<DemonPlayer> players, out Vector3 impact)
    {
        var targets = players.Where(p => p.Alive).Select(p => new HitTarget(p.Id, p.Feet, Stance.Stand, 0)).ToList();
        FireballStep step = default;
        for (var i = 0; i < 30 * 10; i++)
        {
            step = FireballFlight.Step(ball, def, 1f / 30, (from, dir, len) =>
            {
                if (dir.Y >= -1e-6f) return float.PositiveInfinity; // the floor at y = 0
                var t = -from.Y / dir.Y;
                return t <= len ? t : float.PositiveInfinity;
            }, targets);
            if (step.Outcome != FireballOutcome.Flying) break;
            ball = step.Ball;
        }
        impact = step.Impact;
        return step;
    }

    [Fact]
    public void It_does_not_throw_out_of_range_or_without_a_line_of_sight()
    {
        var far = Run(Brain(start: 3), new[] { P(1, 0, 400) }, 10);
        Assert.Empty(far.OfType<DemonTelegraph>());
        var blind = Run(Brain(sight: (_, _) => false, start: 3), new[] { P(1, -5, 12) }, 10);
        Assert.Empty(blind.OfType<DemonTelegraph>());
    }

    [Fact]
    public void A_throw_leaves_the_cooldown_before_the_next()
    {
        var b = Brain(start: 3);
        var ev = Run(b, new[] { P(1, -5, 12) }, 12);
        var times = new List<int>();
        var ev2 = new List<DemonEvent>();
        var b2 = Brain(start: 3);
        for (var i = 0; i < 30 * 12; i++)
        {
            ev2.Clear(); b2.Step(1f / 30, new[] { P(1, -5, 12) }, ev2);
            if (ev2.OfType<DemonFireballLaunched>().Any()) times.Add(i);
        }
        Assert.True(times.Count >= 2);
        for (var i = 1; i < times.Count; i++) Assert.True((times[i] - times[i - 1]) / 30f >= Def.Fireball.CooldownS - 0.05f);
        Assert.NotEmpty(ev);
    }

    // ---- ballistics and blast ----

    [Theory]
    [InlineData(0, 0, 0, 20, 0, 0, false)]
    [InlineData(0, 10, 0, 30, 2, -5, false)]
    [InlineData(0, 5, 0, 8, 1, 30, true)]
    [InlineData(0, 10, 0, 0, -6, 12, false)]
    public void The_solved_arc_reaches_the_target(float fx, float fy, float fz, float tx, float ty, float tz, bool lob)
    {
        var from = new Vector3(fx, fy, fz); var to = new Vector3(tx, ty, tz);
        Assert.True(Ballistics.Solve(from, to, 24, 9.8f, lob, out var v, out var t));
        Assert.Equal(24f, v.Length(), 1e-3f);
        var at = from + v * t + new Vector3(0, -0.5f * 9.8f * t * t, 0);
        Assert.True(Vector3.Distance(at, to) < 1e-2f, $"landed {at}");
    }

    [Fact]
    public void A_target_beyond_the_reach_has_no_arc()
    {
        Assert.False(Ballistics.Solve(Vector3.Zero, new Vector3(500, 0, 0), 24, 9.8f, false, out _, out _));
    }

    [Fact]
    public void The_blast_follows_the_shelter_rule_and_ignites_the_hurt()
    {
        var def = Def;
        var burns = new BurnSet();
        HitTarget T(ushort id, float x, float z) => new(id, new Vector3(x, 0, z), Stance.Stand, 0);
        var targets = new[] { T(1, 0, 0), T(2, 2, 0), T(3, 30, 0) };

        var open = FireballBlast.Detonate(new Vector3(0, 0.5f, 0), 1, targets, def.Fireball, null, burns, def.Burn);
        Assert.Equal(2, open.Count); // 3 is far outside the splash
        Assert.Equal(def.Fireball.Damage, open.First(v => v.Id == 1).Damage); // the direct hit takes the full damage
        var splash = open.First(v => v.Id == 2).Damage;
        Assert.True(splash > 0 && splash < def.Fireball.Damage);
        Assert.True(burns.IsBurning(1) && burns.IsBurning(2) && !burns.IsBurning(3));
        Assert.All(open, v => Assert.True(v.Ignited));

        // a room round the blast: the player standing beside it, outside the room, is spared; the direct hit does not ask
        var ring = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
        var room = new BunkerRoom { BunkerId = "church", Level = 0, Ring = ring, Y0 = -1, Y1 = 5, MinX = -1, MaxX = 1, MinZ = -1, MaxZ = 1 };
        var sheltered = FireballBlast.Detonate(new Vector3(0, 0.5f, 0), null, targets, def.Fireball, new Shelters(new[] { room }));
        Assert.Equal(new ushort[] { 1 }, sheltered.Select(v => v.Id));
        var direct = FireballBlast.Detonate(new Vector3(0, 0.5f, 0), 2, targets, def.Fireball, new Shelters(new[] { room }));
        Assert.Contains(direct, v => v.Id == 2 && v.Damage == def.Fireball.Damage);
    }

    [Fact]
    public void A_fireball_stops_at_a_wall_and_expires()
    {
        var def = Def.Fireball;
        var wall = FireballFlight.Step(new Fireball(0, Vector3.Zero, new Vector3(24, 0, 0), 0), def, 0.5f, (_, _, len) => len > 5 ? 5f : float.PositiveInfinity, Array.Empty<HitTarget>());
        Assert.Equal(FireballOutcome.World, wall.Outcome);
        var old = FireballFlight.Step(new Fireball(0, new Vector3(0, 50, 0), Vector3.Zero, def.LifeS - 0.01f), def, 0.1f, (_, _, _) => float.PositiveInfinity, Array.Empty<HitTarget>());
        Assert.Equal(FireballOutcome.Expired, old.Outcome);
    }

    // ---- the entity and its wire ----

    [Fact]
    public void The_normal_and_facing_survive_the_wire()
    {
        foreach (var n in new[] { new Vector3(0, -1, 0), new Vector3(1, 0, 0), new Vector3(-0.8355f, 0, 0.5495f), new Vector3(0.6f, -0.6f, 0.5f), Vector3.UnitY })
        {
            var u = DemonRules.UnpackNormal(DemonRules.PackNormal(n));
            Assert.True(Vector3.Dot(Vector3.Normalize(n), u) > 0.998f, $"{n} came back as {u}");
        }
        var e = new DemonEntity { Position = new Vector3(1, 4, -2), Normal = new Vector3(0, -1, 0), Forward = Vector3.Normalize(new Vector3(0.3f, 0, -1)), State = DemonState.Crawling, HitsLeft = 0.5f };
        var w = e.Serialize();
        Assert.Equal(DemonState.Crawling, DemonRules.StateOf(w.State));
        Assert.InRange(w.Hp, 127, 129);
        Assert.True(Vector3.Dot(e.Forward, DemonRules.Facing(w.YawRad, w.PitchRad)) > 0.999f);
        Assert.Equal(0, new DemonEntity { HitsLeft = 0 }.Serialize().Hp);
    }

    [Fact]
    public void Fireball_events_round_trip_and_bad_lengths_are_refused()
    {
        var f = new DemonFireballMsg(7, new Vector3(1, 2, 3), new Vector3(4, -5, 6));
        Assert.Equal(f, DemonCodec.DecodeFireball(DemonCodec.EncodeFireball(f)));
        var i = new DemonImpactMsg(7, new Vector3(1, 2, 3), true, false);
        Assert.Equal(i, DemonCodec.DecodeImpact(DemonCodec.EncodeImpact(i)));
        Assert.Null(DemonCodec.DecodeFireball(new byte[5]));
        Assert.Null(DemonCodec.DecodeImpact(new byte[5]));
    }

    // ---- roar, leap, flame ----

    static DemonBrain With(Action<DemonFile> tweak, int seed = 1, int start = 5)
    {
        var d = Def; d.Roar.DurationS = 0; d.Roar.IntervalS = new[] { 22f, 38f }; d.Roar.HealthShares = new[] { 0.66f, 0.33f };
        d.Leap = Full.Leap; d.Flame = Full.Flame; d.Roar = Full.Roar;
        d.Leap.Chance = 0; d.Flame.DurationS = 0; d.Roar.DurationS = 0;
        tweak(d);
        return new DemonBrain(d, Graph, new Random(seed), null, start);
    }

    [Fact]
    public void It_roars_when_it_wakes_and_when_its_hits_fall_to_a_phase()
    {
        var b = With(d => d.Roar.DurationS = Full.Roar.DurationS);
        var players = new[] { P(1, 0, 0) };
        var ev = Run(b, players, 0.2f);
        Assert.Single(ev.OfType<DemonRoared>()); // the wake
        Assert.Equal(DemonPhase.Roaring, b.Phase);
        Run(b, players, Full.Roar.DurationS + 0.2f);
        Assert.NotEqual(DemonPhase.Roaring, b.Phase);

        var hits = new List<DemonEvent>();
        while (b.HitsNeeded - b.HitsTaken > b.HitsNeeded * 0.6f) b.RegisterHit(1, DemonHitKind.Bullet, hits); // under 66 % left
        var again = Run(b, players, 6f, x => x.Phase == DemonPhase.Roaring);
        Assert.Equal(DemonPhase.Roaring, b.Phase);
        Assert.Single(again.OfType<DemonRoared>());
        Run(b, players, Full.Roar.DurationS + 0.5f);
        Assert.DoesNotContain(Run(b, players, 3f), e => e is DemonRoared); // that phase has been roared for
    }

    [Fact]
    public void It_leaps_to_an_anchor_across_on_the_opposite_wall_along_a_scripted_arc()
    {
        var b = With(d => d.Leap.Chance = 1, start: 5); // east wall, x = 10
        var g = Graph;
        var players = new[] { P(1, 0, 0) };
        var wound = false; var flew = false;
        var prev = b.Pose.Position;
        var land = -1;
        for (var i = 0; i < 30 * 40 && land < 0; i++)
        {
            b.Step(1f / 30, players, new List<DemonEvent>());
            var pose = b.Pose;
            Assert.True(float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z) && MathF.Abs(pose.Normal.Length() - 1) < 1e-3f && MathF.Abs(pose.Forward.Length() - 1) < 1e-3f);
            if (b.Phase == DemonPhase.LeapWind) { wound = true; Assert.Equal(prev, pose.Position); }
            if (b.Phase == DemonPhase.Leaping)
            {
                flew = true;
                Assert.True(Vector3.Dot(g[b.LeapTarget].Normal, g[5].Normal) <= Full.Leap.MaxNormalDot, "lands on another surface: the west wall or the ceiling");
                Assert.True(Vector3.Distance(prev, pose.Position) <= Full.Leap.SpeedMps / 30 * 1.6f + 0.2f, "scripted speed");
            }
            else if (flew) land = i;
            prev = pose.Position;
        }
        Assert.True(wound && flew && land >= 0);
        Assert.True(Vector3.Dot(g[b.Anchor].Normal, g[5].Normal) <= Full.Leap.MaxNormalDot);
        Assert.True(Vector3.Distance(b.Pose.Position, g[b.Anchor].Pos) < 1e-3f);
        Assert.Equal(g[b.Anchor].Normal, b.Pose.Normal);
    }

    [Fact]
    public void It_breathes_fire_on_a_near_target_and_the_cone_burns_only_who_stands_in_it()
    {
        var b = With(d => d.Flame.DurationS = Full.Flame.DurationS);
        var f = Full.Flame;
        var players = new[] { P(1, 4, 0), P(2, 10, 9) }; // 6 m off the east wall, and one far along it
        var ev = new List<DemonEvent>();
        var started = -1; var ended = -1;
        for (var i = 0; i < 30 * 12; i++)
        {
            b.Step(1f / 30, players, ev);
            if (started < 0 && b.Phase == DemonPhase.Breathing) started = i;
            if (started >= 0 && ended < 0 && b.Phase != DemonPhase.Breathing) ended = i;
        }
        Assert.True(started >= 0 && ended > started);
        Assert.Equal((ushort)1, ev.OfType<DemonFlameStarted>().First().Target);
        Assert.InRange((ended - started) / 30f, f.WindupS + f.DurationS - 0.2f, f.WindupS + f.DurationS + 0.2f);
        var hits = ev.OfType<DemonFlameHit>().Select(h => h.Player).ToList();
        Assert.True(hits.Count >= 4 && hits.All(p => p == 1), "only the target stands in the cone");
        Assert.Contains(ev, e => e is DemonFlameHit);
    }

    // ---- the real church's anchor layout: leaps and the ceiling ----

    // church_interior.py: u across (inner walls IU0..IU1), v along (bays -5, -1, 3, 7), per bay low wall, high wall, vault, high wall, low wall; heights above the building base
    const float IU0 = -2.48f, IU1 = 4.46f, VaultZ = 6.85f, FloorZ = 0.25f;

    static string NaveJson()
    {
        var slots = new (float u, float z, string n)[] { (IU0, 2.2f, "[1,0,0]"), (IU0, 4.4f, "[1,0,0]"), ((IU0 + IU1) / 2, VaultZ, "[0,0,-1]"), (IU1, 4.4f, "[-1,0,0]"), (IU1, 2.2f, "[-1,0,0]") };
        var bays = new[] { -5f, -1f, 3f, 7f };
        var items = new List<string>();
        for (var i = 0; i < bays.Length; i++)
            for (var j = 0; j < 5; j++)
            {
                var links = new List<int>();
                foreach (var k in new[] { j - 1, j + 1 }) if (k >= 0 && k < 5) links.Add(i * 5 + k);
                foreach (var k in new[] { i - 1, i + 1 }) if (k >= 0 && k < bays.Length) links.Add(k * 5 + j);
                var (u, z, n) = slots[j];
                items.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{ \"id\": {i * 5 + j}, \"pos\": [{u}, {bays[i]}, {z}], \"normal\": {n}, \"links\": [{string.Join(",", links)}] }}"));
            }
        return "{ \"anchors\": [" + string.Join(",", items) + "] }";
    }

    /// <summary>The nave as an empty box: a line is clear when both ends are inside it (it is convex). Game axes: z is minus the v along the nave.</summary>
    static bool InNave(Vector3 p) => p.X >= IU0 - 0.01f && p.X <= IU1 + 0.01f && p.Y >= FloorZ && p.Y <= VaultZ + 0.01f && p.Z <= 7.3f && p.Z >= -13.7f;

    /// <summary>The shipped numbers, all of it on (roar, fireballs, flame), two people on the nave's floor, 300 s of fight: when it leaps, from where to where, and when it gets up on the ceiling.</summary>
    static (List<(float t, int from, int to)> leaps, List<float> ceiling) Fight(int seed, float seconds = 300)
    {
        var g = AnchorGraph.FromJson(NaveJson());
        var d = Full; d.Pounce.PinS = 0; // the leaps in question are the ones it picks, not the pounce's leap away
        var b = new DemonBrain(d, g, new Random(seed), (a, c) => InNave(a) && InNave(c), 7);
        var players = new[]
        {
            new DemonPlayer(1, new Vector3(0.985f, FloorZ, -3f), true), new DemonPlayer(2, new Vector3(2.2f, FloorZ, 2f), true),
        };
        var leaps = new List<(float, int, int)>(); var ceiling = new List<float>();
        var inLeap = false; var onCeiling = false; var from = -1;
        var ev = new List<DemonEvent>();
        for (var i = 0; i < (int)(seconds * 30); i++)
        {
            b.Step(1f / 30, players, ev);
            if (b.Phase == DemonPhase.LeapWind && !inLeap) { inLeap = true; from = b.Anchor; }
            if (inLeap && b.Phase is not (DemonPhase.LeapWind or DemonPhase.Leaping)) { inLeap = false; leaps.Add((i / 30f, from, b.Anchor)); }
            var up = b.Pose.Normal.Y < -0.7f && b.Phase is DemonPhase.Dwelling or DemonPhase.Crawling;
            if (up && !onCeiling) ceiling.Add(i / 30f);
            onCeiling = up;
        }
        return (leaps, ceiling);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void It_leaps_every_ten_to_fifteen_seconds_between_walls_and_ceiling(int seed)
    {
        var g = AnchorGraph.FromJson(NaveJson());
        var (leaps, _) = Fight(seed);
        Assert.InRange(leaps.Count, 18, 40); // 300 s: about one in 10 to 15 s
        var times = leaps.Select(l => l.t).Prepend(0f).ToList();
        for (var i = 1; i < times.Count; i++) Assert.True(times[i] - times[i - 1] < 25f, $"{times[i] - times[i - 1]:0} s without a leap");
        bool Ceil(int a) => g[a].Normal.Y < -0.7f;
        Assert.True(leaps.Count(l => !Ceil(l.from) && !Ceil(l.to)) >= 3, "wall to wall, the leap it is known for"); // in 300 s
        Assert.Contains(leaps, l => Ceil(l.from) && !Ceil(l.to)); // ceiling to wall
        Assert.All(leaps, l => Assert.True(Vector3.Dot(g[l.from].Normal, g[l.to].Normal) <= Full.Leap.MaxNormalDot));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void It_goes_up_on_the_ceiling_regularly_and_takes_both_walls(int seed)
    {
        var g = AnchorGraph.FromJson(NaveJson());
        var (leaps, ceiling) = Fight(seed);
        Assert.True(ceiling.Count >= 10, $"only {ceiling.Count} visits to the ceiling in 300 s");
        Assert.True(ceiling.Prepend(0f).Zip(ceiling, (a, b) => b - a).Max() < 45f, "a long time without the ceiling");
        Assert.True(leaps.Any(l => g[l.to].Normal.X > 0.9f) && leaps.Any(l => g[l.to].Normal.X < -0.9f), "it lands on both walls");
    }

    [Fact]
    public void It_also_leaps_from_a_wall_up_to_the_ceiling()
    {
        var g = AnchorGraph.FromJson(NaveJson());
        var up = Enumerable.Range(1, 6).Sum(seed => Fight(seed).leaps.Count(l => g[l.from].Normal.Y > -0.7f && g[l.to].Normal.Y < -0.7f));
        Assert.True(up >= 3, $"{up} wall to ceiling leaps in six fights");
    }

    [Fact]
    public void A_scaled_demon_scales_its_body_its_mouth_its_muzzle_and_its_hit_volume()
    {
        var d = Full;
        Assert.Equal(1.6f, d.Scale, 3);
        Assert.Equal(d.Fight.BodyHeightM * 1.6f, d.BodyM, 4);
        Assert.Equal(d.Fireball.MuzzleOffsetM * 1.6f, d.MuzzleM, 4);
        var b = new DemonBrain(d, AnchorGraph.FromJson(NaveJson()), new Random(1), null, 0);
        var mouth = b.FlameOrigin - b.Pose.Position;
        Assert.Equal(d.Flame.HeadUpM * 1.6f, Vector3.Dot(mouth, b.Pose.Normal), 3);
        Assert.Equal(d.Flame.HeadForwardM * 1.6f, Vector3.Dot(mouth, b.Pose.Forward), 3);
        var small = DemonRules.Target(Vector3.Zero, Vector3.UnitX, d.Fight.BodyHeightM, 0f);
        var big = DemonRules.Target(Vector3.Zero, Vector3.UnitX, d.BodyM, 0f, d.Scale);
        var from = new Vector3(8, 0f, 0.8f); // a shot that passes 0.8 m beside the body's axis
        Assert.Null(HitMath.Raycast(from, -Vector3.UnitX, 30, float.PositiveInfinity, new[] { small }, 1));
        Assert.NotNull(HitMath.Raycast(from, -Vector3.UnitX, 30, float.PositiveInfinity, new[] { big }, 1));
        Assert.True(HitMath.Centre(big).X > HitMath.Centre(small).X, "its middle is further off the surface");
    }
}
