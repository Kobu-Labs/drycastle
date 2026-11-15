using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class HuntShotTests
{
    // a deer-sized body at the origin, facing -Z (yaw 0): 0.5 wide, 1.4 high, 1.5 long, the box 0.2 m ahead of the feet
    static HitTarget Deer(float yaw = 0, float ahead = 0.2f) =>
        new(WildlifeRules.FirstHitId, Vector3.Zero, Stance.Stand, yaw, HeadGuarded: true, Scale: 1.4f / PlayerHitBox.StandHeight, Box: new Vector3(0.5f, 1.4f, 1.5f), BoxAhead: ahead);

    static RayHit? Shoot(HitTarget t, Vector3 from, Vector3 to) =>
        HitMath.Raycast(from, Vector3.Normalize(to - from), 500, float.PositiveInfinity, new[] { t }, shooter: 1);

    [Fact]
    public void A_bullet_from_the_side_meets_the_flank_of_the_box()
    {
        var hit = Shoot(Deer(), new Vector3(50, 0.7f, -0.2f), new Vector3(0, 0.7f, -0.2f));
        Assert.NotNull(hit);
        Assert.Equal(49.75f, hit!.Value.Distance, 3);
        Assert.False(hit.Value.Head);
    }

    [Fact]
    public void The_box_reaches_from_the_tail_to_the_nose_where_the_capsule_did_not()
    {
        // 0.6 m ahead of the box's centre (0.8 m ahead of the feet): inside the 1.5 m long body, outside a capsule around the feet
        Assert.NotNull(Shoot(Deer(), new Vector3(50, 0.7f, -0.8f), new Vector3(0, 0.7f, -0.8f)));
        // past the nose and behind the tail
        Assert.Null(Shoot(Deer(), new Vector3(50, 0.7f, -1.0f), new Vector3(0, 0.7f, -1.0f)));
        Assert.Null(Shoot(Deer(), new Vector3(50, 0.7f, 0.6f), new Vector3(0, 0.7f, 0.6f)));
        // over its back
        Assert.Null(Shoot(Deer(), new Vector3(50, 1.5f, -0.2f), new Vector3(0, 1.5f, -0.2f)));
    }

    [Fact]
    public void The_box_turns_with_the_animal()
    {
        // facing -X (yaw a quarter turn to the left): the length is now along X
        var t = Deer(MathF.PI / 2);
        Assert.NotNull(Shoot(t, new Vector3(-0.8f, 0.7f, 50), new Vector3(-0.8f, 0.7f, 0)));
        Assert.Null(Shoot(t, new Vector3(0.7f, 0.7f, 50), new Vector3(0.7f, 0.7f, 0)));
    }

    [Fact]
    public void A_wall_before_the_box_stops_the_bullet()
    {
        var from = new Vector3(50, 0.7f, -0.2f);
        Assert.Null(HitMath.Raycast(from, -Vector3.UnitX, 500, 30, new[] { Deer() }, shooter: 1));
    }

    [Fact]
    public void A_target_without_a_box_is_still_the_capsule_with_its_head()
    {
        var player = new HitTarget(7, Vector3.Zero, Stance.Stand, 0);
        var hit = Shoot(player, new Vector3(20, 1.7f, 0), new Vector3(0, 1.7f, 0));
        Assert.True(hit!.Value.Head);
    }

    [Fact]
    public void The_diagram_places_the_entry_along_the_body_and_up_it_and_names_the_flank()
    {
        var t = Deer();
        // from its right (+X when it faces -Z), a quarter of the length behind the nose, at half height
        var from = new Vector3(50, 0.7f, -0.2f - 0.375f);
        var hit = Shoot(t, from, new Vector3(0, 0.7f, -0.2f - 0.375f))!.Value;
        var m = HuntShot.From(t, from - Vector3.UnitX * hit.Distance, species: 3, distance: 312.4f, weapon: 9);
        Assert.Equal(0.75f, m.Along, 2);
        Assert.Equal(0.5f, m.Height, 2);
        Assert.True(m.RightFlank);
        Assert.Equal(312, m.DistanceM);
        Assert.Equal(3, m.Species);
        Assert.Equal(9, m.Weapon);

        var left = new Vector3(-50, 0.2f, -0.2f + 0.6f);
        var back = Shoot(t, left, new Vector3(0, 0.2f, -0.2f + 0.6f))!.Value;
        var other = HuntShot.From(t, left + Vector3.UnitX * back.Distance, 3, 80, 9);
        Assert.False(other.RightFlank);
        Assert.Equal(0.1f, other.Along, 2);
        Assert.Equal(0.2f / 1.4f, other.Height, 2);
    }

    [Fact]
    public void The_message_round_trips_within_a_step_and_a_wrong_length_is_refused()
    {
        var m = new HuntShotMsg(4, 0.31f, 0.77f, true, 455, 12);
        var back = HuntShot.Decode(HuntShot.Encode(m))!.Value;
        Assert.Equal(m.Species, back.Species);
        Assert.Equal(m.Along, back.Along, 2);
        Assert.Equal(m.Height, back.Height, 2);
        Assert.True(back.RightFlank);
        Assert.Equal(455, back.DistanceM);
        Assert.Equal(12, back.Weapon);
        Assert.Null(HuntShot.Decode(new byte[6]));
    }

    [Fact]
    public void Every_wild_species_has_a_body_box_a_bullet_can_meet()
    {
        var animals = AnimalsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "animals.json"));
        var wildlife = WildlifeFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "wildlife.json"));
        foreach (var s in wildlife.Species)
        {
            var def = animals.Animals.Single(a => a.Id == s.Id);
            Assert.True(def.HitBox.Count == 3 && def.HitBox.All(v => v > 0), s.Id);
            Assert.True(MathF.Abs(def.HitBoxAhead) < def.HitBox[2] / 2, s.Id);
        }
    }
}
