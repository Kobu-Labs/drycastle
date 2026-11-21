using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Phoenix (#396, GLOSSARY "Phoenix"): the night counter, the phase machine, the crash site, the keeper's spawn and the wire. Small and fast: no clock, no scene.</summary>
public class PhoenixTests
{
    const float Dt = 0.05f;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Data = GameData.Load(DataDir);
    static PhoenixRules Rules => Data.Wildlife.Phoenix!;

    sealed class World : IAnimalWorld
    {
        public Func<float, float, float?> Ground = (_, _) => 0f;
        public readonly List<(float X, float Z)> Buildings = new();
        public Func<float, float, float, bool> Free = (_, _, _) => true;
        public FlightBounds? Box = new FlightBounds(0, 2000, 0, 2000);
        public float? GroundY(float x, float z) => Ground(x, z);
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
        public FlightBounds? MapBounds => Box;
        public bool Fits(float x, float z, float radius) => Ground(x, z) != null && Free(x, z, radius);
        public float NearestBuildingM(float x, float z, float maxM)
        {
            var best = float.PositiveInfinity;
            foreach (var (bx, bz) in Buildings) best = MathF.Min(best, MathF.Sqrt((bx - x) * (bx - x) + (bz - z) * (bz - z)));
            return best <= maxM ? best : float.PositiveInfinity;
        }
    }

    static (Animal Phoenix, World World) NewPhoenix(int seed = 1, World? world = null)
    {
        world ??= new World();
        var species = Data.Animals.IndexOf("phoenix");
        var spawn = new PhoenixSpawn(Rules, new Vector3(1000, 0, 1000), 350);
        return (new Animal(Data.Animals, species, spawn.Centre, 0, world, seed, 0, 0.5f, spawn), world);
    }

    static void Run(Animal a, float seconds) { for (var t = 0f; t < seconds - 0.001f; t += Dt) a.Tick(Dt); }

    static Animal Crashed(int seed = 1)
    {
        var (p, _) = NewPhoenix(seed);
        p.OnDamage(1, 7);
        Run(p, Rules.FallSeconds + 0.5f);
        Assert.Equal(AnimalState.Crashed, p.State);
        return p;
    }

    // ---- the night counter ----

    static PhoenixSchedule Schedule(int between = 5) => new(new PhoenixRules { NightsBetween = between });

    /// <summary>Runs the clock from <paramref name="from"/> for <paramref name="hours"/> game hours in one-minute steps; every tick the schedule saw.</summary>
    static List<PhoenixTick> Clock(PhoenixSchedule s, double from, double hours)
    {
        var ticks = new List<PhoenixTick>();
        for (var h = 0.0; h <= hours + 1e-9; h += 1 / 60.0) ticks.Add(s.Advance(from + h));
        return ticks;
    }

    [Fact]
    public void The_first_phoenix_is_due_at_the_midnight_of_the_5th_night_then_every_5th()
    {
        var s = Schedule();
        var due = new List<double>();
        var hour = 8.0; // a match starts at 08:00 (DayClock.StartMinute)
        for (var i = 0; i < 11 * 24 * 60; i++, hour += 1 / 60.0)
            if (s.Advance(hour).Due) due.Add(hour);
        Assert.Equal(new[] { 5 * 24.0, 10 * 24.0 }, due.Select(x => Math.Round(x))); // the midnights that end day 5 and day 10
        Assert.Equal(11, s.Nights); // 08:00 + 11 days = 08:00, 11 midnights passed
    }

    [Fact]
    public void Nights_count_on_every_midnight_and_a_due_one_is_flagged_once()
    {
        var s = Schedule(2);
        var ticks = Clock(s, 20, 24 * 4);
        Assert.Equal(4, s.Nights);
        Assert.Equal(2, ticks.Count(t => t.Due));
    }

    [Fact]
    public void A_stopped_clock_and_a_jump_of_the_hour_count_nothing()
    {
        var s = Schedule(1);
        for (var i = 0; i < 100; i++) s.Advance(23.9); // frozen just before midnight
        Assert.Equal(0, s.Nights);
        s.Advance(12); // an admin sets noon...
        s.Advance(0.2); // ...then midnight's side of the day: still a jump
        Assert.Equal(0, s.Nights);
        s.Advance(23.99);
        Assert.False(s.Advance(23.995).Due);
        Assert.True(s.Advance(24.001).Due); // running through midnight is a night (the admin's test route)
        Assert.Equal(1, s.Nights);
    }

    [Fact]
    public void Dawn_is_flagged_once_a_day_at_the_dawn_hour_and_the_spawn_hour_can_be_moved()
    {
        var s = new PhoenixSchedule(new PhoenixRules { NightsBetween = 1, SpawnHour = 22, DawnHour = 5 });
        var ticks = Clock(s, 20, 24);
        Assert.Equal(1, ticks.Count(t => t.Dawn));
        Assert.Equal(1, ticks.Count(t => t.Due));
        Assert.Equal(1, s.Nights);
    }

    // ---- the phase machine ----

    [Fact]
    public void A_phoenix_takes_off_flying_high_and_circles_the_map()
    {
        var (p, _) = NewPhoenix();
        Assert.Equal(AnimalState.Flying, p.State);
        Assert.InRange(p.Position.Y, Rules.AltitudeMinM, Rules.AltitudeMaxM);
        var y = p.Position.Y;
        var from = p.Position;
        Run(p, 10);
        Assert.Equal(y, p.Position.Y, 0.01f);
        Assert.InRange(Vector3.Distance(p.Position, from), 50f, 150f); // 14 m/s for 10 s, along a circle
        Assert.Equal(350f, MathF.Sqrt((p.Position.X - 1000) * (p.Position.X - 1000) + (p.Position.Z - 1000) * (p.Position.Z - 1000)), 0.5f);
        Assert.True(p.Alive);
        Assert.Equal(0f, p.Pitch);
    }

    [Fact]
    public void The_first_hit_only_makes_it_fall_whatever_the_damage_and_it_never_dies_from_the_air()
    {
        var (p, _) = NewPhoenix();
        var died = 0;
        p.Died += (_, _) => died++;
        p.OnDamage(100000, 3);
        Assert.Equal(AnimalState.Falling, p.State);
        Assert.True(p.Alive);
        Assert.Equal(0, died);
        Assert.Equal(0, p.Killer);
    }

    [Fact]
    public void A_falling_phoenix_is_not_damaged_further_and_crashes_at_its_site_alive_and_still()
    {
        var (p, _) = NewPhoenix();
        (Vector3 From, Vector3 To, float Seconds)? fell = null;
        var landed = 0;
        p.Fell += (_, f, t, s) => fell = (f, t, s);
        p.Landed += _ => landed++;
        var start = p.Position;
        p.OnDamage(1, 3);
        Assert.Equal(start, fell!.Value.From);
        Assert.Equal(Rules.FallSeconds, fell.Value.Seconds);
        Assert.True(p.Pitch < -0.3f, "nose down along the dive line");
        Assert.Equal(PhoenixFlight.DivePitch(start, fell.Value.To), p.Pitch, 1e-5f);
        Run(p, Rules.FallSeconds / 2);
        Assert.Equal(AnimalState.Falling, p.State);
        p.OnDamage(1000, 4); // a hit on a Falling one changes nothing
        Assert.Equal(AnimalState.Falling, p.State);
        Assert.True(p.Alive);
        Assert.InRange(p.Position.Y, 0.5f, start.Y - 1f);
        Run(p, Rules.FallSeconds);
        Assert.Equal(AnimalState.Crashed, p.State);
        Assert.Equal(1, landed);
        Assert.Equal(fell.Value.To, p.Position); // the ground at the site
        Assert.Equal(fell.Value.To, p.CrashSite);
        Assert.Equal(0f, p.Pitch);
        Assert.True(p.Alive);
        var at = p.Position;
        Run(p, 30);
        Assert.Equal(AnimalState.Crashed, p.State); // it stays until killed
        Assert.Equal(at, p.Position);
    }

    [Fact]
    public void The_second_hit_counts_only_while_crashed_and_burns_it_with_the_shooter_credited()
    {
        var p = Crashed();
        (Animal, ushort)? died = null;
        p.Died += (a, by) => died = (a, by);
        p.OnDamage(1, 9);
        Assert.Equal(AnimalState.Burning, p.State);
        Assert.False(p.Alive);
        Assert.Equal((ushort)9, p.Killer);
        Assert.Equal((p, (ushort)9), died);
        p.OnDamage(1000, 10); // burning: no target any more
        Assert.Equal((ushort)9, p.Killer);
    }

    [Fact]
    public void A_burning_phoenix_dies_after_the_flame_drops_its_mineral_and_the_ash_goes()
    {
        var p = Crashed();
        string? dropped = null;
        p.Dropped += (_, m) => dropped = m;
        p.OnDamage(1, 9);
        Run(p, Rules.BurnSeconds - 0.5f);
        Assert.Equal(AnimalState.Burning, p.State);
        Assert.Null(dropped);
        Run(p, 1);
        Assert.Equal(AnimalState.Dead, p.State);
        Assert.Equal("pheonixite", dropped);
        Assert.Null(p.Ended);
        Run(p, Data.Wildlife.Phoenix!.DeadSeconds + 1);
        Assert.Equal(WorldDespawnReason.Expired, p.Ended);
    }

    [Fact]
    public void Dawn_sends_a_flying_phoenix_away_but_not_one_that_was_hit()
    {
        var (p, _) = NewPhoenix();
        Assert.True(p.FlyOff());
        Assert.Equal(WorldDespawnReason.Expired, p.Ended);
        var (q, _) = NewPhoenix();
        q.OnDamage(1, 1);
        Assert.False(q.FlyOff());
        Run(q, Rules.FallSeconds + 0.5f);
        Assert.False(q.FlyOff());
        Assert.Null(q.Ended);
    }

    [Fact]
    public void An_undeclared_phase_change_is_refused_by_the_spec()
    {
        var s = Animal.Spec;
        Assert.True(s.Allows(AnimalState.Flying, AnimalState.Falling));
        Assert.True(s.Allows(AnimalState.Falling, AnimalState.Crashed));
        Assert.True(s.Allows(AnimalState.Crashed, AnimalState.Burning));
        Assert.True(s.Allows(AnimalState.Burning, AnimalState.Dead));
        Assert.False(s.Allows(AnimalState.Flying, AnimalState.Dead)); // never killed from the air
        Assert.False(s.Allows(AnimalState.Flying, AnimalState.Crashed));
        Assert.False(s.Allows(AnimalState.Falling, AnimalState.Burning)); // the second hit is valid only while crashed
        Assert.False(s.Allows(AnimalState.Falling, AnimalState.Dead));
        Assert.False(s.Allows(AnimalState.Crashed, AnimalState.Dead));
        Assert.False(s.Allows(AnimalState.Crashed, AnimalState.Flying));
        var m = new StateMachine<AnimalState>(s);
        m.Enter(AnimalState.Flying);
        Assert.Throws<InvalidOperationException>(() => m.Enter(AnimalState.Dead)); // Strict in tests
    }

    // ---- hit target ----

    [Fact]
    public void A_flying_phoenixs_hit_box_is_centred_on_its_position_and_a_crashed_ones_stands_on_the_ground()
    {
        var (p, _) = NewPhoenix();
        var box = p.HitBox;
        Assert.Equal(new[] { 3.0f, 1.1f, 4.0f }, box);
        Assert.Equal(p.Position.Y - 0.55f, p.Feet.Y, 1e-4f);
        Assert.Equal(p.Position.Y, p.Bounds.Centre.Y, 1e-4f);
        var c = Crashed();
        Assert.Equal(c.Position, c.Feet);
        Assert.Equal(c.Position.Y + 0.55f, c.Bounds.Centre.Y, 1e-4f);
        Assert.True(AnimalRules.Stands(AnimalState.Crashed));
        Assert.False(AnimalRules.Stands(AnimalState.Flying));
        Assert.False(p.Obstructs);
        Assert.True(c.Obstructs);
    }

    [Fact]
    public void A_phoenix_is_not_thrown_by_a_vehicle()
    {
        var (p, _) = NewPhoenix();
        Assert.False(p.Throw(new Vector3(5, 5, 0), Vector3.Zero));
        Assert.Equal(AnimalState.Flying, p.State);
        var c = Crashed();
        Assert.False(c.Throw(new Vector3(5, 5, 0), Vector3.Zero));
        Assert.Equal(AnimalState.Crashed, c.State);
    }

    [Fact]
    public void The_wire_state_has_the_phase_in_its_low_bits_and_the_dive_pitch()
    {
        var (p, _) = NewPhoenix();
        Assert.Equal(AnimalState.Flying, AnimalRules.StateOf(p.Serialize().State));
        Assert.Equal(11, p.Serialize().State & AnimalRules.StateMask);
        p.OnDamage(1, 1);
        var s = p.Serialize();
        Assert.Equal(12, s.State & AnimalRules.StateMask);
        Assert.Equal(p.Pitch, s.PitchRad, 1e-3f);
        Assert.Equal(AnimalRules.Look(Data.Animals.IndexOf("phoenix"), 0), s.Owner);
        Assert.Equal(0, AnimalRules.SizeStepOf(s.State)); // no weight: no size
        Assert.Equal(0, s.State & AnimalRules.HitToggle);
    }

    // ---- the crash site ----

    [Fact]
    public void The_crash_site_is_clear_ground_away_from_buildings_not_water_and_not_steep()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var w = new World { Ground = (x, z) => x < 900 ? null : 0.01f * x }; // water in the west; a 1% slope
            w.Buildings.Add((1100, 1000));
            var (site, clear) = PhoenixCrash.FindSite(w, new Vector3(1000, 100, 1000), 0f, Rules, new Random(seed));
            Assert.True(clear, $"seed {seed}");
            Assert.True(site.X >= 900 + Rules.CrashClearM, $"seed {seed}: {site}");
            Assert.True(w.NearestBuildingM(site.X, site.Z, 500) >= Rules.CrashBuildingM, $"seed {seed}");
            Assert.Equal(0.01f * site.X, site.Y, 1e-3f);
            Assert.True(MathF.Sqrt((site.X - 1000) * (site.X - 1000) + (site.Z - 1000) * (site.Z - 1000)) <= Rules.CrashMaxM * 2 + 1);
        }
    }

    [Fact]
    public void The_crash_site_is_ahead_of_the_dive_when_the_ground_there_is_clear()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var (site, clear) = PhoenixCrash.FindSite(new World(), new Vector3(1000, 100, 1000), 0f, Rules, new Random(seed)); // yaw 0 faces -Z
            Assert.True(clear);
            var d = MathF.Sqrt((site.X - 1000) * (site.X - 1000) + (site.Z - 1000) * (site.Z - 1000));
            Assert.InRange(d, Rules.CrashMinM - 0.01f, Rules.CrashMaxM + 0.01f);
            Assert.True(site.Z < 1000 - Rules.CrashMinM * MathF.Cos(PhoenixCrash.AheadHalfAngle) + 0.01f, $"seed {seed}: {site}");
        }
    }

    [Fact]
    public void A_site_needs_the_whole_circle_flat_and_free_of_solids_and_with_no_room_it_still_crashes_on_ground()
    {
        var steep = new World { Ground = (x, _) => x * 0.5f }; // 27 degrees everywhere
        Assert.Null(PhoenixCrash.IsClear(steep, 1000, 1000, Rules));
        var blocked = new World { Free = (_, _, _) => false };
        Assert.Null(PhoenixCrash.IsClear(blocked, 1000, 1000, Rules));
        var (site, clear) = PhoenixCrash.FindSite(steep, new Vector3(1000, 100, 1000), 0, Rules, new Random(1));
        Assert.False(clear);
        Assert.Equal(1000 * 0.5f, site.Y, 0.01f); // the nearest ground: right under the hit
        var island = new World { Ground = (x, z) => MathF.Abs(x - 1000) < 1 && MathF.Abs(z - 1000) < 1 ? 0f : null };
        Assert.False(PhoenixCrash.FindSite(island, new Vector3(1000, 100, 1000), 0, Rules, new Random(1)).Clear);
    }

    [Fact]
    public void The_dive_line_speeds_up_and_ends_on_the_site()
    {
        var a = new Vector3(0, 100, 0);
        var b = new Vector3(60, 0, 80);
        Assert.Equal(a, PhoenixFlight.Dive(a, b, 0));
        Assert.Equal(b, PhoenixFlight.Dive(a, b, 1));
        Assert.True(Vector3.Distance(a, PhoenixFlight.Dive(a, b, 0.5f)) < Vector3.Distance(a, b) / 2, "slower at the start: more of the line is left");
        Assert.Equal(MathF.Atan2(-100, 100), PhoenixFlight.DivePitch(a, b), 1e-5f);
    }

    // ---- the keeper ----

    const string Json = """{"version":1,"max_animals":10,"phoenix":{"id":"phoenix","nights_between":2,"spawn_hour":0,"dawn_hour":5}}""";

    static (WildlifeKeeper Keeper, WorldEntityManager Entities) Keeper(string json = Json, int seed = 1)
    {
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        return (new WildlifeKeeper(WildlifeFile.Parse(json).CheckAgainst(Data.Animals), Data.Animals, entities, seed), entities);
    }

    static void Hours(WildlifeKeeper k, World w, double from, double to) { for (var h = from; h <= to; h += 1 / 60.0) k.UpdateSky(h, w); }

    [Fact]
    public void The_keeper_spawns_a_phoenix_at_midnight_of_the_second_night_only_while_a_round_runs()
    {
        var (k, e) = Keeper();
        var w = new World();
        Hours(k, w, 8, 8 + 20); // midnight 1
        Assert.Equal(0, k.Count);
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.Active);
        Hours(k, w, 28, 28 + 22); // midnight 2 at 48
        Assert.Equal(2, k.Nights!.Nights);
        var p = Assert.Single(k.Animals);
        Assert.Equal("phoenix", p.Def.Id);
        Assert.Equal(AnimalState.Flying, p.State);
        Assert.Equal(1, e.Count);
        Assert.Equal(1, k.PhoenixAlive);
    }

    [Fact]
    public void A_night_with_no_round_is_counted_but_spawns_nothing_and_a_living_phoenix_blocks_the_next()
    {
        var (k, _) = Keeper();
        var w = new World();
        Hours(k, w, 8, 8 + 50); // nights 1 and 2 pass with no round
        Assert.Equal(2, k.Nights!.Nights);
        Assert.Equal(0, k.Count);
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.SpawnPhoenix(w));
        var p = k.Animals.Single();
        var living = new List<(ushort HitId, Animal Animal)>();
        k.Living(living);
        k.Hit(living.Single().HitId, 1, 1); // falls
        Run(p, Rules.FallSeconds + 1); // crashed, and left there
        Hours(k, w, 58, 58 + 48); // nights 3 and 4: due, but one is still alive
        Assert.Equal(1, k.PhoenixAlive);
        Assert.Equal(1, k.Count);
    }

    [Fact]
    public void At_dawn_a_flying_phoenix_leaves_and_a_crashed_one_stays()
    {
        var (k, e) = Keeper();
        var w = new World();
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.SpawnPhoenix(w));
        Assert.True(k.SpawnPhoenix(w));
        var living = new List<(ushort HitId, Animal Animal)>();
        k.Living(living);
        var (hit, hitAnimal) = living[0];
        k.Hit(hit, 1, 1);
        Run(hitAnimal, Rules.FallSeconds + 1);
        Hours(k, w, 3, 6); // dawn at 5
        Assert.Equal(AnimalState.Crashed, hitAnimal.State);
        Assert.Equal(1, k.Count);
        Assert.Equal(1, e.Count);
    }

    [Fact]
    public void The_keeper_reports_the_fall_the_kill_and_the_mineral_on_the_ground()
    {
        var (k, e) = Keeper();
        var w = new World();
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.SpawnPhoenix(w));
        (ushort, Vector3, Vector3, float)? fell = null;
        var killed = new List<ushort>();
        var drops = new List<MineralDrop>();
        k.PhoenixFell += (id, f, t, s) => fell = (id, f, t, s);
        k.Killed += (_, by) => killed.Add(by);
        k.MineralDropped += drops.Add;
        var living = new List<(ushort HitId, Animal Animal)>();
        k.Living(living);
        var (hit, p) = living.Single();
        Assert.Equal(DamageResult.Damaged, k.Hit(hit, 5, 1)); // from the air: only a fall
        Assert.Equal(1, fell!.Value.Item1);
        Assert.Empty(killed);
        Assert.Equal(DamageResult.Ignored, k.Hit(hit, 5, 1)); // falling: not a target
        Run(p, Rules.FallSeconds + 0.5f);
        Assert.Empty(killed);
        Assert.Equal(DamageResult.Killed, k.Hit(hit, 6, 1)); // crashed: the second hit
        Assert.Equal(new ushort[] { 6 }, killed);
        Assert.Empty(drops);
        Run(p, Rules.BurnSeconds + 0.5f);
        var drop = Assert.Single(drops);
        Assert.Equal("pheonixite", drop.MineralId);
        Assert.Equal(p.Position, drop.At);
        Assert.Equal(1, drop.Entity);
        Assert.Equal(drop, Assert.Single(k.Drops));
        k.Living(living);
        Assert.Single(living); // not a target any more: the list from before is the only one
        var after = new List<HitTarget>();
        k.AddTargets(after);
        Assert.Empty(after);
        k.Clear();
        Assert.Empty(k.Drops);
        Assert.False(k.Active);
    }

    [Fact]
    public void The_mineral_a_dead_phoenix_drops_becomes_a_pickup_node_at_the_crash_site_anybody_can_take()
    {
        var (k, _) = Keeper();
        var w = new World();
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.SpawnPhoenix(w));
        var set = new MineralNodeSet();
        k.MineralDropped += d => set.AddDrop(d);
        var living = new List<(ushort HitId, Animal Animal)>();
        k.Living(living);
        var (hit, p) = living.Single();
        k.Hit(hit, 5, 1);
        Run(p, Rules.FallSeconds + 0.5f);
        k.Hit(hit, 6, 1); // the killer is player 6
        Assert.Empty(set.Nodes);
        Run(p, Rules.BurnSeconds + 0.5f);
        var node = Assert.Single(set.Nodes);
        Assert.Equal(("pheonixite", p.Position), (node.MineralId, node.Position));
        var book = new MineralBook();
        var near = p.Position + new Vector3(1, 0, 0);
        Assert.Equal(MineralPickResult.Ok, book.Check(5, node, MineralActivity.IsActive(node.MineralId, default), near)); // not the killer
        Assert.Equal(MineralPickResult.Ok, book.Check(6, node, true, near)); // the killer is no different
    }

    [Fact]
    public void A_falling_phoenix_is_no_hit_target_so_bullets_pass_through_it_and_it_is_a_target_again_once_crashed()
    {
        var (k, _) = Keeper();
        var w = new World();
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.SpawnPhoenix(w));
        var living = new List<(ushort HitId, Animal Animal)>();
        k.Living(living);
        var (hit, p) = living.Single();
        Assert.True(p.Targetable);
        Assert.Equal(DamageResult.Damaged, k.Hit(hit, 5, 1));
        Assert.Equal(AnimalState.Falling, p.State);
        Assert.False(p.Targetable);
        Assert.True(p.Alive);
        var targets = new List<HitTarget>();
        k.AddTargets(targets);
        Assert.Empty(targets);
        var from = new Vector3(p.Position.X, 1.5f, p.Position.Z + 10);
        Assert.Null(HitMath.Raycast(from, Vector3.Normalize(p.Position - from), 500, float.PositiveInfinity, targets, shooter: 1)); // passes through
        living.Clear();
        k.Living(living);
        Assert.Empty(living);
        Assert.Equal(DamageResult.Ignored, k.Hit(hit, 5, 100));
        Assert.Equal(AnimalState.Falling, p.State);
        Run(p, Rules.FallSeconds + 0.5f);
        Assert.Equal(AnimalState.Crashed, p.State);
        Assert.True(p.Targetable);
        k.AddTargets(targets);
        Assert.Single(targets);
    }

    [Fact]
    public void The_target_of_a_flying_phoenix_is_its_box_in_the_air_so_a_knife_never_reaches_it()
    {
        var (k, _) = Keeper();
        var w = new World();
        k.Populate(_ => Array.Empty<Vector3>(), w);
        Assert.True(k.SpawnPhoenix(w));
        var p = k.Animals.Single();
        var targets = new List<HitTarget>();
        k.AddTargets(targets);
        var t = Assert.Single(targets);
        Assert.Equal(p.Position.Y - 0.55f, t.Feet.Y, 1e-3f);
        Assert.Equal(new Vector3(3, 1.1f, 4), t.Box);
        Assert.Null(HitMath.Melee(new Vector3(p.Position.X, 0, p.Position.Z), 0, 3f, targets, 1)); // right under it, 80 m or more up
        // a bullet from the ground to the body centre hits it
        var from = new Vector3(p.Position.X, 1.5f, p.Position.Z + 10);
        var dir = Vector3.Normalize(p.Position - from);
        Assert.NotNull(HitMath.Raycast(from, dir, 500, float.PositiveInfinity, targets, shooter: 1));
    }

    // ---- data and wire ----

    [Fact]
    public void The_shipped_phoenix_is_a_flyer_with_a_rule_block_the_drop_is_a_mineral_and_the_mystery_is_registered()
    {
        var def = Data.Animals.Find("phoenix")!;
        Assert.True(def.Flies);
        Assert.Equal("phoenix", def.Fx);
        Assert.Equal(new[] { AnimalClip.Idle, AnimalClip.Cruise, AnimalClip.Fall, AnimalClip.Perched }, AnimalClips.Needed(def).ToArray());
        Assert.Equal(5, Rules.NightsBetween);
        Assert.Equal(0f, Rules.SpawnHour);
        Assert.NotNull(Minerals.Find(Rules.Drops));
        Assert.True(Mysteries.Find(Mysteries.Phoenix) is { Live: true });
        Assert.Equal("mystery-4", Mysteries.HiddenId(Mysteries.Phoenix));
        Assert.Equal("mystery-2", Mysteries.HiddenId(Mysteries.Unicorn)); // the others' hidden ids did not move
        Assert.Equal(Data.Animals.Animals.Count - 1, Data.Animals.IndexOf("phoenix")); // last: no species index moved
        var hunt = HuntRecord.ForMysteryAnimal(5, Mysteries.Phoenix, 41.23f, "sniper_rifle", null, 1, 2, 3, DateTime.UnixEpoch);
        Assert.True(hunt.IsMystery);
        Assert.Equal((Mysteries.Phoenix, Mysteries.Phoenix, (float?)null, (float?)41.2f), (hunt.Species, hunt.Exact, hunt.Kg, hunt.DistanceM));
    }

    [Theory]
    [InlineData("""{"nights_between":0}""")]
    [InlineData("""{"spawn_hour":24}""")]
    [InlineData("""{"altitude_min_m":150,"altitude_max_m":100}""")]
    [InlineData("""{"crash_min_m":50,"crash_max_m":10}""")]
    [InlineData("""{"drops":"unobtainium"}""")]
    [InlineData("""{"fall_seconds":0}""")]
    public void A_bad_phoenix_block_is_refused(string block) =>
        Assert.Throws<DataFileException>(() => WildlifeFile.Parse("""{"version":1,"max_animals":5,"phoenix":""" + block.Replace("{", "{\"id\":\"phoenix\",") + "}"));

    [Fact]
    public void A_phoenix_id_that_is_no_flyer_is_refused()
    {
        var f = WildlifeFile.Parse("""{"version":1,"max_animals":5,"phoenix":{"id":"fox"}}""");
        Assert.Throws<DataFileException>(() => f.CheckAgainst(Data.Animals));
    }

    [Fact]
    public void The_fall_event_round_trips_in_thirty_bytes()
    {
        var b = PhoenixFallCodec.Encode(513, new Vector3(1.5f, 100, -2), new Vector3(60, 3.25f, 80), 4f);
        Assert.Equal(30, b.Length);
        Assert.True(PhoenixFallCodec.TryDecode(b, out var id, out var from, out var site, out var seconds));
        Assert.Equal((ushort)513, id);
        Assert.Equal(new Vector3(1.5f, 100, -2), from);
        Assert.Equal(new Vector3(60, 3.25f, 80), site);
        Assert.Equal(4f, seconds);
        Assert.False(PhoenixFallCodec.TryDecode(b.AsSpan(1), out _, out _, out _, out _));
        Assert.True(Wire.ProtocolVersion >= 80);
    }
}
