using System.Numerics;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Colliders;

/// <summary>ADR 0028 slice 3: a player on foot against standing animals, the demon's Body capsule, and a navmesh walker's detour.</summary>
public class WalkAndBodyTests
{
    // ---- a player on foot against a standing animal's Body box ----

    static BodyBox Cow(float x = 0, float z = 0) => new(x, z, 0.5f, 1.0f, 0); // 1 m wide, 2 m long, facing -Z

    [Fact]
    public void A_player_walking_into_the_box_is_put_back_out_at_its_face()
    {
        var box = Cow();
        var (x, z) = box.KeepOut(0, 1.2f, 0, 3f, 0.35f, out var moved); // came from the rear, now 0.2 m from the rear face (z = 1): inside the radius
        Assert.True(moved);
        Assert.Equal(0, x, 3);
        Assert.Equal(1.35f, z, 3);
    }

    [Fact]
    public void Along_the_face_the_player_slides_it_keeps_its_sideways_part()
    {
        var box = Cow();
        // from beside the box, moving diagonally towards it and along it
        var (x, z) = box.KeepOut(0.7f, 0.0f, 1.2f, 0.2f, 0.35f, out var moved);
        Assert.True(moved);
        Assert.Equal(0.85f, x, 3); // out to the radius on the right face (x = 0.5 + 0.35)
        Assert.Equal(0.0f, z, 3);  // the move along the face was kept
    }

    [Fact]
    public void A_player_already_touching_is_not_pushed_out_only_stopped_from_going_in()
    {
        var box = Cow();
        // standing 0.2 m from the face (inside the 0.35 radius): moving away or along is free, moving in is held at the distance it had
        var from = (X: 0.7f, Z: 0f);
        var away = box.KeepOut(0.8f, 0f, from.X, from.Z, 0.35f, out var movedAway);
        Assert.False(movedAway); Assert.Equal(0.8f, away.X, 3);
        var inward = box.KeepOut(0.6f, 0f, from.X, from.Z, 0.35f, out var movedIn);
        Assert.True(movedIn); Assert.Equal(0.7f, inward.X, 3);
    }

    [Fact]
    public void Resolve_ignores_a_box_the_player_passes_over_or_under()
    {
        var bodies = new[] { new BodyObstacle(Cow(), 0f, 1.2f) };
        var through = new Vector3(0, 0, 0.3f); // inside the box
        Assert.Equal(through.Z, PlayerBodyBlock.Resolve(new Vector3(0, 1.3f, 2f), new Vector3(0, 1.3f, 0.3f), 0.35f, 1.8f, bodies).Z, 3); // feet above its top: a jump over it
        Assert.Equal(1.35f, PlayerBodyBlock.Resolve(new Vector3(0, 0, 2f), through, 0.35f, 1.8f, bodies).Z, 3); // on the ground: back out of the face it came in by
    }

    [Fact]
    public void Resolve_is_the_same_for_the_same_boxes_wherever_it_runs()
    {
        // the server and a client's prediction ask with the same box and the same move: the same answer (the point of one shared test)
        var bodies = new[] { new BodyObstacle(Cow(2, 3), 0f, 1.4f) };
        var from = new Vector3(2f, 0, 5.5f); var to = new Vector3(2.1f, 0, 4.2f);
        var a = PlayerBodyBlock.Resolve(from, to, 0.35f, 1.8f, bodies);
        var b = PlayerBodyBlock.Resolve(from, to, 0.35f, 1.8f, bodies);
        Assert.Equal(a, b);
        Assert.True(a.Z >= 4.35f - 1e-3f); // not through the rear face at z = 4
    }

    [Fact]
    public void Only_an_animal_that_stands_is_a_body_a_player_cannot_walk_through()
    {
        foreach (var s in new[] { AnimalState.Idle, AnimalState.Eat, AnimalState.Sit, AnimalState.Bark }) Assert.True(AnimalRules.Stands(s), s.ToString());
        foreach (var s in new[] { AnimalState.Walk, AnimalState.Flee, AnimalState.Dead, AnimalState.Carried, AnimalState.Thrown, AnimalState.Down }) Assert.False(AnimalRules.Stands(s), s.ToString());
    }

    // ---- the demon's Body capsule ----

    [Fact]
    public void The_demons_capsule_is_the_players_scaled_and_centred_where_its_hit_shape_is()
    {
        var (centre, radius, height) = DemonRules.BodyCapsule(new Vector3(1, 0, 2), Vector3.UnitY, 1.5f, 3f);
        Assert.Equal(new Vector3(1, 1.5f, 2), centre);
        Assert.Equal(MoveConsts.CapsuleRadius * 3f, radius, 4);
        Assert.Equal(MoveConsts.CrouchHeight * 3f, height, 4);
        var hit = DemonRules.Target(new Vector3(1, 0, 2), Vector3.UnitY, 1.5f, 0, 3f);
        Assert.Equal(centre.Y, hit.Feet.Y + height / 2, 3); // the same figure
    }

    [Fact]
    public void The_demon_is_a_body_from_the_climb_until_it_dies()
    {
        Assert.False(DemonRules.Blocks(DemonState.Transforming));
        Assert.False(DemonRules.Blocks(DemonState.Dead));
        foreach (var s in new[] { DemonState.Waiting, DemonState.Dwelling, DemonState.Crawling, DemonState.Leaping, DemonState.Pouncing, DemonState.Pinning }) Assert.True(DemonRules.Blocks(s), s.ToString());
    }

    // ---- a walker's detour ----

    /// <summary>A flat field with a wall: <see cref="Wall"/> is a rectangle (x0, z0, x1, z1) the ray stops at; asks are counted.</summary>
    sealed class Field : IWalkerGround
    {
        public List<(float X0, float Z0, float X1, float Z1)> Walls = new();
        public int Rays;
        public float ClearAhead(Vector3 from, Vector3 dir, float metres)
        {
            Rays++;
            for (var t = 0f; t <= metres; t += 0.05f)
            {
                var (x, z) = (from.X + dir.X * t, from.Z + dir.Z * t);
                foreach (var w in Walls) if (x >= w.X0 && x <= w.X1 && z >= w.Z0 && z <= w.Z1) return t;
            }
            return float.PositiveInfinity;
        }
        public float GroundY(float x, float z, float fallback) => 0;
    }

    static (Vector3 End, int Ticks) Walk(Field field, Vector3 start, Vector3 to, int maxTicks = 600)
    {
        var guard = new WalkGuard();
        var pos = start;
        for (var i = 1; i <= maxTicks; i++)
        {
            guard.Tick(1 / 30f);
            if (guard.Head(pos, to, 0.05f, field) is not { } head) continue;
            var d = Vector3.Distance(pos, head);
            pos = d <= 0.05f ? head : pos + (head - pos) * (0.05f / d); // 1.5 m/s
            if (Vector3.Distance(pos, to) < 0.05f) return (pos, i);
        }
        return (pos, maxTicks);
    }

    [Fact]
    public void A_clear_leg_is_walked_straight_with_one_ray_for_every_few_metres()
    {
        var field = new Field();
        var (end, ticks) = Walk(field, new Vector3(0, 0, 0), new Vector3(0, 0, -30));
        Assert.True(Vector3.Distance(end, new Vector3(0, 0, -30)) < 0.1f);
        Assert.InRange(ticks, 590 - 400, 600); // straight: 30 m at 1.5 m/s is 600 ticks
        Assert.InRange(field.Rays, 5, 14); // about one per WalkGuard.LookAheadM of run, not one per step
    }

    [Fact]
    public void A_trunk_on_the_leg_is_gone_round()
    {
        var field = new Field();
        field.Walls.Add((-0.3f, -10.3f, 0.3f, -9.7f)); // a trunk, 0.6 m across, on the line
        var (end, _) = Walk(field, new Vector3(0, 0, 0), new Vector3(0, 0, -20));
        Assert.True(Vector3.Distance(end, new Vector3(0, 0, -20)) < 0.1f, $"ended at {end}");
    }

    [Fact]
    public void A_parked_vehicle_on_the_leg_is_gone_round()
    {
        var field = new Field();
        field.Walls.Add((-1.1f, -12.15f, 1.1f, -7.85f)); // a car's footprint
        var (end, _) = Walk(field, new Vector3(0, 0, 0), new Vector3(0, 0, -20));
        Assert.True(Vector3.Distance(end, new Vector3(0, 0, -20)) < 0.1f, $"ended at {end}");
    }

    [Fact]
    public void A_wall_with_no_way_round_stops_it_and_it_goes_on_when_the_door_opens()
    {
        var field = new Field();
        field.Walls.Add((-50f, -10.2f, 50f, -9.8f)); // a closed door in a long wall
        var guard = new WalkGuard();
        var pos = new Vector3(0, 0, -9f);
        var to = new Vector3(0, 0, -20);
        // nearly at the wall: it asks, finds both sides blocked a few metres either way? (sidestep 1.6 m along a wall is blocked at the wall's face: the rays run into it)
        Vector3? head = null;
        for (var i = 0; i < 90; i++) { guard.Tick(1 / 30f); head = guard.Head(pos, to, 0.05f, field); if (head is { } h) pos = Vector3.Distance(pos, h) <= 0.05f ? h : pos + Vector3.Normalize(h - pos) * 0.05f; }
        Assert.True(pos.Z > -10.3f, $"it stood at the wall, at {pos}");
        field.Walls.Clear(); // the door opens
        for (var i = 0; i < 60 && Vector3.Distance(pos, to) > 0.1f; i++) { guard.Tick(1 / 30f); if (guard.Head(pos, to, 0.05f, field) is { } h) pos = Vector3.Distance(pos, h) <= 0.05f ? h : pos + Vector3.Normalize(h - pos) * 0.05f; }
        Assert.True(pos.Z < -10.5f, $"it went on through the open door, at {pos}");
    }

    [Fact]
    public void Without_a_ground_the_leg_is_its_own_head()
    {
        var guard = new WalkGuard();
        Assert.Equal(new Vector3(1, 2, 3), guard.Head(Vector3.Zero, new Vector3(1, 2, 3), 0.05f, null));
    }

    [Fact]
    public void A_citizen_with_a_trunk_on_its_way_still_reaches_the_church_door()
    {
        var cfg = new CitizensConfig();
        var field = new Field();
        field.Walls.Add((-0.3f, -10.3f, 0.3f, -9.7f));
        var c = new Citizen(cfg, 1, 0, new[] { new Vector3(0, 0, 0), new Vector3(0, 0, -20) }, new[] { new Vector3(0, 0, -20) }, 0) { Ground = field };
        for (var i = 0; i < 2000 && c.State == CitizenState.Walking; i++) c.Tick(1 / 30f);
        Assert.NotEqual(CitizenState.Walking, c.State);
        Assert.True(field.Rays > 0);
    }
}
