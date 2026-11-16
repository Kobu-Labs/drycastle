using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Wild animal size and weight (#309): the roll, the state bits, the scaled body, hit points, hit shape and the weight in the hunt shot.</summary>
public class AnimalSizeTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static AnimalsFile Animals => Shipped.Animals;

    sealed class Flat : IAnimalWorld
    {
        public float? GroundY(float x, float z) => 0f;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => Vector3.Zero;
    }

    static Animal Make(string species, float roll, int variant = 0) =>
        new(Animals, Animals.IndexOf(species), Vector3.Zero, 0, new Flat(), 1, variant, roll);

    // ---- the roll and its steps ----

    [Fact]
    public void The_roll_is_deterministic_and_clusters_in_the_middle()
    {
        var a = new Random(5); var b = new Random(5);
        Assert.Equal(AnimalRules.RollSize(a), AnimalRules.RollSize(b));

        var rng = new Random(11);
        var counts = new int[AnimalRules.SizeSteps];
        const int N = 20000;
        double sum = 0;
        for (var i = 0; i < N; i++)
        {
            var r = AnimalRules.RollSize(rng);
            Assert.InRange(r, 0f, 1f);
            sum += r;
            counts[AnimalRules.StepOf(r)]++;
        }
        Assert.InRange(sum / N, 0.49, 0.51);
        Assert.True(counts[3] > counts[1] && counts[4] > counts[6], "the middle steps are common, the ends rare");
        Assert.True(counts[0] < N / 50 && counts[7] < N / 50, "a trophy is rare");
    }

    [Fact]
    public void Steps_run_from_the_smallest_to_the_largest_size_and_the_roll_picks_the_nearest()
    {
        Assert.Equal(0.88f, AnimalRules.SizeOfStep(0), 4);
        Assert.Equal(1.12f, AnimalRules.SizeOfStep(7), 4);
        for (var s = 1; s < AnimalRules.SizeSteps; s++) Assert.True(AnimalRules.SizeOfStep(s) > AnimalRules.SizeOfStep(s - 1));
        Assert.Equal(0, AnimalRules.StepOf(0f));
        Assert.Equal(7, AnimalRules.StepOf(1f));
        Assert.Equal(4, AnimalRules.StepOf(0.5f));
        Assert.Equal(0, AnimalRules.StepOf(-3f));
        Assert.Equal(7, AnimalRules.StepOf(9f));
    }

    [Fact]
    public void The_size_step_travels_in_bits_5_to_7_beside_the_state_and_the_hit_bit()
    {
        var a = Make("deer", 1f);
        var s = a.Serialize();
        Assert.Equal(7, AnimalRules.SizeStepOf(s.State));
        Assert.Equal(AnimalState.Idle, AnimalRules.StateOf(s.State));
        Assert.False(AnimalRules.HitToggled(s.State));

        a.OnDamage(1, 3); // a hit that does not kill: the hit bit flips and the animal flees, the size stays
        var hit = a.Serialize();
        Assert.Equal(7, AnimalRules.SizeStepOf(hit.State));
        Assert.True(AnimalRules.HitToggled(hit.State));
        Assert.Equal(AnimalState.Flee, AnimalRules.StateOf(hit.State));

        a.OnDamage(1000, 3);
        Assert.Equal(7, AnimalRules.SizeStepOf(a.Serialize().State));
    }

    // ---- the animal ----

    [Fact]
    public void A_species_with_a_weight_scales_its_box_and_weighs_within_its_range()
    {
        var small = Make("deer", 0f);
        var big = Make("deer", 1f);
        var def = Animals.Find("deer")!;
        Assert.Equal(60f, small.WeightKg, 3);
        Assert.Equal(120f, big.WeightKg, 3);
        Assert.Equal(0.88f, small.Size, 4);
        Assert.Equal(1.12f, big.Size, 4);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(def.HitBox[i] * 0.88f, small.HitBox[i], 4);
            Assert.Equal(def.HitBox[i] * 1.12f, big.HitBox[i], 4);
        }
        Assert.Equal(90f, Make("deer", 0.5f).WeightKg, 3);
    }

    [Fact]
    public void A_look_of_the_species_may_have_its_own_weight_range()
    {
        var moose = Animals.Find("moose")!;
        var bull = moose.VariantIndex("moose_male_01");
        Assert.True(bull > 0);
        Assert.Equal(270f, Make("moose", 0f, 0).WeightKg, 3);   // the cow, the base look
        Assert.Equal(400f, Make("moose", 1f, 0).WeightKg, 3);
        Assert.Equal(380f, Make("moose", 0f, bull).WeightKg, 3);
        Assert.Equal(650f, Make("moose", 1f, bull).WeightKg, 3);
    }

    [Fact]
    public void Hit_points_grow_with_the_volume_for_a_species_that_has_them_and_stay_for_a_one_hit_one()
    {
        var deer = Animals.Find("deer")!;
        Assert.Equal(MathF.Round(deer.Hp * 1.12f * 1.12f * 1.12f), Make("deer", 1f).MaxHp);
        Assert.Equal(MathF.Round(deer.Hp * 0.88f * 0.88f * 0.88f), Make("deer", 0f).MaxHp);
        Assert.True(Make("deer", 1f).MaxHp > Make("deer", 0f).MaxHp);
        Assert.Equal(Make("deer", 1f).MaxHp, Make("deer", 1f).Hp);
        Assert.Equal(Make("deer", 1f).MaxHp, Make("deer", 1f).Serialize().Hp);

        Assert.True(Animals.Find("bear")!.OneHit);
        Assert.Equal(Animals.Find("bear")!.Hp, Make("bear", 1f).MaxHp); // one hit kills whatever it weighs
        Assert.Equal(Animals.Find("bear")!.Hp, Make("bear", 0f).MaxHp);
    }

    [Fact]
    public void Hit_points_never_pass_what_the_wire_byte_holds()
    {
        var file = AnimalsFile.Parse("""
            {"version":1,"animals":[{"id":"ox","name":"Ox","model":"a/ox.glb","hp":250,"weight_kg":[400,600],
            "walk_speed":1,"run_speed":5,"flee_radius":10,"wander_radius":20,"hit_box":[1,1.5,2.5]}]}
            """);
        var ox = new Animal(file, 0, Vector3.Zero, 0, new Flat(), 1, 0, 1f);
        Assert.Equal(255, ox.MaxHp);
        Assert.Equal(255, ox.Serialize().Hp);
    }

    [Fact]
    public void A_species_without_a_weight_has_no_size()
    {
        var pet = new Animal(Animals, Animals.IndexOf("husky"), Vector3.Zero, 0, new Flat(), 1, 0, 1f);
        Assert.Null(Animals.Find("husky")!.WeightKg);
        Assert.Equal(1f, pet.Size);
        Assert.Equal(0, pet.SizeStep);
        Assert.Equal(0f, pet.WeightKg);
        Assert.Equal(Animals.Find("husky")!.Hp, pet.MaxHp);
        Assert.Equal(Animals.Find("husky")!.HitBox, pet.HitBox);
    }

    // ---- the keeper ----

    [Fact]
    public void Every_wild_animal_has_a_size_and_a_weight_in_its_range_and_its_target_is_scaled()
    {
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        var cfg = WildlifeFile.Parse("""{"version":1,"max_animals":60,"species":[{"id":"deer","count":40},{"id":"moose","count":20}]}""").CheckAgainst(Animals);
        var keeper = new WildlifeKeeper(cfg, Animals, entities, 9);
        keeper.Populate(_ => new[] { new Vector3(1, 0, 1) }, new Flat());
        var living = new List<(ushort HitId, Animal Animal)>();
        keeper.Living(living);
        Assert.Equal(60, living.Count);

        var steps = new HashSet<int>();
        foreach (var (hitId, a) in living)
        {
            var range = a.Def.WeightKgOf(a.Variant)!;
            Assert.InRange(a.WeightKg, range[0], range[1]);
            Assert.InRange(a.Size, AnimalRules.MinSize, AnimalRules.MaxSize);
            steps.Add(a.SizeStep);
            var t = keeper.TargetOf(hitId, a);
            Assert.Equal(a.Size, t.ShapeScale);
            Assert.Equal(a.HitBox[1], t.Box.Y, 4);
            Assert.Equal(a.Def.HitBoxAhead * a.Size, t.BoxAhead, 4);
        }
        Assert.True(steps.Count >= 4, "animals differ in size");
    }

    [Fact]
    public void The_same_seed_rolls_the_same_animals()
    {
        List<(int Step, float Kg, int Variant)> Roll()
        {
            var registry = new WorldEntityRegistry();
            AnimalRules.Register(registry);
            var cfg = WildlifeFile.Parse("""{"version":1,"max_animals":30,"species":[{"id":"moose","count":10}]}""").CheckAgainst(Animals);
            var keeper = new WildlifeKeeper(cfg, Animals, new WorldEntityManager(registry), 4);
            keeper.Populate(_ => new[] { new Vector3(1, 0, 1) }, new Flat());
            return keeper.Animals.Select(a => (a.SizeStep, a.WeightKg, a.Variant)).ToList();
        }
        Assert.Equal(Roll(), Roll());
    }

    // ---- the shipped data ----

    [Fact]
    public void Every_wild_species_of_wildlife_json_has_a_weight_range()
    {
        foreach (var sp in Shipped.Wildlife.Species)
        {
            var def = Animals.Find(sp.Id)!;
            Assert.True(def.WeightKg != null, $"{sp.Id} has no weight_kg");
            Assert.True(def.WeightKg![0] > 0 && def.WeightKg[1] >= def.WeightKg[0]);
        }
    }

    [Fact]
    public void Hit_point_scaling_of_the_species_that_have_them_stays_within_the_wire_byte()
    {
        foreach (var def in Animals.Animals.Where(a => a.WeightKg != null && !a.OneHit))
            Assert.True(def.Hp * AnimalRules.MaxSize * AnimalRules.MaxSize * AnimalRules.MaxSize <= 255, $"{def.Id}: hp x size^3 passes 255 and would be cut");
    }

    // ---- the schema ----

    static string Species(string extra, string variants = "") => $$"""
        {"version":1,"animals":[{"id":"deer","name":"Deer","model":"a/deer.glb","hp":60,{{extra}}
        "walk_speed":1,"run_speed":5,"flee_radius":10,"wander_radius":20,"hit_box":[1,1.5,2.5]{{variants}}}]}
        """;

    [Fact]
    public void A_weight_range_is_two_positive_numbers_in_order_below_the_wire_limit()
    {
        Assert.NotNull(AnimalsFile.Parse(Species("\"weight_kg\":[60,120],")).Animals[0].WeightKg);
        Assert.NotNull(AnimalsFile.Parse(Species("\"weight_kg\":[5,5],")).Animals[0].WeightKg);
        foreach (var bad in new[] { "[60]", "[60,120,200]", "[0,10]", "[-5,10]", "[120,60]", "[1,6001]" })
            Assert.Throws<DataFileException>(() => AnimalsFile.Parse(Species($"\"weight_kg\":{bad},")));
    }

    [Fact]
    public void A_variant_may_have_a_weight_only_when_its_species_has_one()
    {
        const string ok = ",\"variants\":[{\"id\":\"big\",\"model\":\"a/big.glb\",\"weight_kg\":[100,200]}]";
        Assert.Equal(200f, AnimalsFile.Parse(Species("\"weight_kg\":[60,120],", ok)).Animals[0].WeightKgOf(1)![1]);
        Assert.Throws<DataFileException>(() => AnimalsFile.Parse(Species("", ok)));
        Assert.Throws<DataFileException>(() => AnimalsFile.Parse(Species("\"weight_kg\":[60,120],", ",\"variants\":[{\"id\":\"big\",\"model\":\"a/big.glb\",\"weight_kg\":[200,100]}]")));
    }

    // ---- the hit shape follows the size ----

    static readonly AnimalShapes Shapes = AnimalShapes.Load(Path.Combine(DataDir, "animal_shapes.json"));

    static (float Z, float Y) HeadOf(AnimalShape s)
    {
        float z = 0, y = 0; var n = 0;
        for (var r = 0; r < s.Rows; r++)
            for (var c = 0; c < s.Cols; c++)
                if (s.IsHead(c, r)) { z += s.Z0 + (c + 0.5f) * s.Cell; y += (r + 0.5f) * s.Cell; n++; }
        return (z / n, y / n);
    }

    static RayHit? Broadside(AnimalShape s, float size, float ahead, float up)
    {
        var t = new HitTarget(WildlifeRules.FirstHitId, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Box: new Vector3(0.48f, 1.41f, 1.45f) * size, BoxAhead: 0.19f * size, Shape: s, ShapeScale: size);
        return HitMath.Raycast(new Vector3(40, up, -ahead), -Vector3.UnitX, 200, float.PositiveInfinity, new[] { t }, shooter: 1);
    }

    [Fact]
    public void A_bigger_animal_is_hit_where_its_scaled_outline_is()
    {
        var shape = Shapes.At("deer", AnimalPose.Idle, 0)!.Value.Shape;
        var (hz, hy) = HeadOf(shape);
        const float Size = 1.12f;

        // the head of a 12 % bigger deer is 12 % further forward and higher: a bullet at the scaled spot meets the head, at the old spot it no longer does
        var head = Broadside(shape, Size, hz * Size, hy * Size);
        Assert.NotNull(head);
        Assert.True(head!.Value.ShapeHead);
        Assert.Null(Broadside(shape, Size, hz * Size + 0.3f, hy * Size + 0.5f)); // above and beyond the scaled head

        // the same cell is met 12 % deeper into the animal's width: its flank is Size times as far from the middle
        var plain = Broadside(shape, 1f, 0.1f, 0.85f)!.Value.Distance;
        var big = Broadside(shape, Size, 0.1f * Size, 0.85f * Size)!.Value.Distance;
        Assert.Equal((40f - big) / (40f - plain), Size, 2);

        // a wall in front of the flank still stops it: the limit is in metres, not in the shape's frame
        var t = new HitTarget(WildlifeRules.FirstHitId, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Box: new Vector3(0.48f, 1.41f, 1.45f) * Size, BoxAhead: 0.19f * Size, Shape: shape, ShapeScale: Size);
        Assert.Null(HitMath.Raycast(new Vector3(40, 0.85f * Size, -0.1f * Size), -Vector3.UnitX, 200, big - 0.01f, new[] { t }, shooter: 1));
        Assert.NotNull(HitMath.Raycast(new Vector3(40, 0.85f * Size, -0.1f * Size), -Vector3.UnitX, 200, big + 0.01f, new[] { t }, shooter: 1));
    }

    [Fact]
    public void The_hit_diagram_reads_the_scaled_hit_as_the_same_place_on_the_baked_shape()
    {
        var found = Shapes.At("deer", AnimalPose.Idle, 0)!.Value;
        var (hz, hy) = HeadOf(found.Shape);
        const float Size = 0.88f;
        var t = new HitTarget(WildlifeRules.FirstHitId, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Box: new Vector3(0.48f, 1.41f, 1.45f) * Size, BoxAhead: 0.19f * Size,
            Shape: found.Shape, ShapePose: AnimalPose.Idle, ShapeFrame: found.Frame, ShapeScale: Size);
        var msg = HuntShot.From(t, new Vector3(-0.2f, hy * Size, -hz * Size), 3, 100, 0, weightKg: 71.4f);
        var plain = HuntShot.From(t with { ShapeScale = 1f }, new Vector3(-0.2f, hy, -hz), 3, 100, 0);
        Assert.Equal(plain.Along, msg.Along, 3);
        Assert.Equal(plain.Height, msg.Height, 3);
        Assert.Equal(71.4f, msg.WeightKg, 3);
    }

    // ---- the hunt shot carries the weight ----

    [Fact]
    public void The_hunt_shot_carries_the_weight_in_tenths_of_a_kilo()
    {
        foreach (var kg in new[] { 0f, 1.3f, 87.3f, 650f, 6000f })
        {
            var back = HuntShot.Decode(HuntShot.Encode(new HuntShotMsg(4, 0.3f, 0.7f, true, 455, 12, WeightKg: kg)))!.Value;
            Assert.Equal(kg, back.WeightKg, 1);
        }
        Assert.Equal(6553.5f, HuntShot.Decode(HuntShot.Encode(new HuntShotMsg(1, 0, 0, false, 0, 0, WeightKg: 99999f)))!.Value.WeightKg, 1); // never wraps round
        Assert.Null(HuntShot.Decode(new byte[10])); // the older, shorter message
    }
}
