using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

/// <summary>
/// HousePlough: a vehicle cuts its way through a hollow house. The fixture house (TestHouses.Box 32 x 24 x 20 at the map origin, 0.25 m cells) has its
/// front wall in the row y = 1 (map y 0.25..0.5 m), walls from z = 2 (0.5 m) up to the ceiling at z = 12; the vehicles drive north (+y) into it from the south.
/// </summary>
public class PloughTests
{
    static readonly DestructionConfig Cfg = DestructionConfig.Default;
    const float Dt = 1f / Wire.TickRate;
    const float FloorZ = 0.5f; // the top of the floor slab: where the wheels stand

    static HouseGrid House() => TestHouses.Box(32, 24, 20);

    /// <summary>A car (2.06 x 4.4 x 1.62 m) facing north at map x <paramref name="x"/>, its front <paramref name="gapM"/> short of the front wall's outer face (map y 0.25), going <paramref name="speed"/> m/s.</summary>
    static PloughQuery Car(float speed, float gapM = 0.1f, float x = 2f, float yawDeg = 0, float mass = 1200, bool ramming = false)
    {
        var yaw = yawDeg * MathF.PI / 180;
        var fwdMap = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
        var feetMap = new Vector3(x, 0.25f - gapM, FloorZ) - fwdMap * 2.2f;
        var motion = RamBox.ToGodot(fwdMap) * (speed * Dt);
        return new PloughQuery(RamBox.ToGodot(feetMap), yaw, motion, speed, mass, 1.03f, 2.2f, 1.62f, 0.2f, ramming);
    }

    static (PloughPlan Plan, List<PloughCell> Cut) PlanOn(HouseCells c, in PloughQuery q)
    {
        var cut = new List<PloughCell>();
        var plan = HousePlough.Plan(q, Cfg, [0], _ => c, (_, i) => c.Solid(i), cut);
        return (plan, cut);
    }

    [Fact]
    public void Head_on_it_cuts_the_shape_of_the_car_and_no_more()
    {
        var c = new HouseCells(House());
        var g = c.Grid;
        var (plan, cut) = PlanOn(c, Car(16.7f));
        Assert.True(plan.Through);
        // the car's cross-section (2.06 + 2 * 0.1 wide, from 0.2 over the floor to 0.1 over its roof) of a one-cell wall: 9 x 6 cells
        Assert.InRange(plan.Count, 45, 65);
        foreach (var p in cut)
        {
            var (x, y, z) = g.Coords(p.Cell);
            var m = g.ToMap(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            Assert.InRange(m.X, 2f - 1.13f, 2f + 1.13f);
            Assert.InRange(m.Z, FloorZ + 0.2f, FloorZ + 1.72f);
            Assert.Equal(1, y); // the wall it hits, nothing behind it
            Assert.Equal(CellMaterial.Wall, g.Material(p.Cell));
        }
        Assert.Equal(plan.Count * 130f / 1200f, plan.Loss, 3); // the wall's cells, resist 1.0
    }

    [Fact]
    public void At_an_angle_it_cuts_only_what_the_turned_box_sweeps()
    {
        var c = new HouseCells(House());
        var g = c.Grid;
        var q = Car(16.7f, gapM: 0.05f, x: 3.0f, yawDeg: 35);
        var (plan, cut) = PlanOn(c, q);
        Assert.True(plan.Count > 0);
        var box = HousePlough.Box(q, Cfg.Ram.MarginM);
        foreach (var p in cut)
        {
            var (x, y, z) = g.Coords(p.Cell);
            var rel = g.ToMap(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) - box.Centre;
            Assert.True(MathF.Abs(Vector3.Dot(rel, box.Right)) <= box.Half.X + 1e-3f); // within its turned width: a corner's notch, not a round hole
        }
        Assert.True(plan.Count < PlanOn(new HouseCells(House()), Car(16.7f, gapM: 0.05f, x: 3.0f)).Plan.Count * 2);
    }

    [Fact]
    public void Short_of_momentum_it_cuts_what_it_can_afford_nearest_first_and_stops()
    {
        var c = new HouseCells(House());
        var (plan, cut) = PlanOn(c, Car(14f, mass: 200f)); // 2 800 kg m/s: 21 cells of wall
        Assert.False(plan.Through);
        Assert.Equal(21, plan.Count);
        Assert.Equal(14f, plan.Loss); // all of its speed
    }

    [Fact]
    public void It_starts_at_min_speed_and_once_cutting_goes_on_to_keep_speed()
    {
        var c = new HouseCells(House());
        Assert.Equal(0, PlanOn(c, Car(10f)).Plan.Count);                // 36 km/h into a wall: a crash, no hole
        Assert.True(PlanOn(c, Car(10f, ramming: true)).Plan.Count > 0); // inside the house it broke into: on through the next wall
        Assert.Equal(0, PlanOn(c, Car(3f, ramming: true)).Plan.Count);  // crawling: it stops
        Assert.Equal(0, PlanOn(c, Car(20f, gapM: 2f)).Plan.Count);      // the wall out of reach this step
    }

    [Fact]
    public void Cut_settles_only_when_asked_and_replays_the_same()
    {
        var server = new HouseCells(House());
        var client = new HouseReplica(House());
        var (_, cut) = PlanOn(server, Car(16.7f));
        var cells = cut.Select(p => p.Cell).ToArray();
        var r = server.Cut(cells, settle: false, Cfg);
        Assert.Equal(cells.Length, r.Carved.Length);
        Assert.Empty(r.Crumbled);
        var msg = HouseCodec.DecodeCut(HouseCodec.EncodeCut(new HouseCutMsg(0, server.OpSeq, r.Hash, false, 77, 5, r.Carved)))!;
        Assert.Equal(r.Carved, msg.Cells);
        Assert.Equal((77u, (ushort)5), (msg.Tick, msg.Car));
        Assert.Equal(HouseReplica.CarveOutcome.Applied, client.OnCut(msg, Cfg).Outcome);
        var settle = server.Cut([], settle: true, Cfg);
        Assert.Equal(HouseReplica.CarveOutcome.Applied, client.OnCut(new HouseCutMsg(0, server.OpSeq, settle.Hash, true, 78, 0, []), Cfg).Outcome);
        Assert.Equal(server.Hash(), client.Cells.Hash());
    }

    [Fact]
    public void Guesses_hide_cells_until_the_server_has_them_and_a_replay_cuts_them_again()
    {
        var rep = new HouseReplica(House());
        var g = rep.Grid;
        var i = g.IndexOf(8, 1, 5);
        rep.Guess(i, 10);
        Assert.False(rep.View.Solid(i));
        Assert.True(rep.Cells.Solid(i));
        Assert.False(rep.PresentFor(i, 11)); // a later input's plough: already cut
        Assert.True(rep.PresentFor(i, 10));  // its own input replayed: still to cut (and to pay for)
        // a replay from input 9 that does not cut it again takes it back
        rep.BeginReplay(9);
        Assert.True(rep.PresentFor(i, 11));
        var back = new List<int>();
        rep.EndReplay(back);
        Assert.Equal([i], back);
        Assert.True(rep.View.Solid(i));
        // guessed, then the server's op has it: the guess goes, the cell stays gone
        rep.Guess(i, 12);
        var server = new HouseCells(House());
        var r = server.Cut([i], false, Cfg);
        rep.OnCut(new HouseCutMsg(0, server.OpSeq, r.Hash, false, 0, 0, r.Carved), Cfg);
        back.Clear();
        rep.Acked(13, back);
        Assert.Empty(back);
        Assert.False(rep.Guessing);
        Assert.False(rep.View.Solid(i));
        // a guess the server never cut comes back once its input is long acknowledged
        var j = g.IndexOf(9, 1, 5);
        rep.Guess(j, 20);
        rep.Acked(21, back);
        Assert.Empty(back);
        rep.Acked((ushort)(21 + HouseReplica.GuessGraceTicks), back);
        Assert.Equal([j], back);
        Assert.True(rep.View.Solid(j));
    }

    /// <summary>
    /// The drive model over the fixture house: the ground is the floor's top everywhere, the front wall stops the hull wherever a cell of it in the car's
    /// width is still there (at the wall's middle height), and the plough cuts it (as the server does, the collider following only from the next step).
    /// </summary>
    sealed class HouseWorld(HouseCells cells, float halfLength) : IDriveWorld
    {
        public int Hits, Cuts;
        bool[] _wallAtStep = [];
        public float GroundY(Vector3 from) => FloorZ;
        public float WaterY(float x, float z) => float.NegativeInfinity;

        /// <summary>The wall as the collider has it: rebuilt between steps, as the colliders are between ticks.</summary>
        public void Commit()
        {
            var g = cells.Grid;
            _wallAtStep = new bool[g.Nx];
            for (var x = 0; x < g.Nx; x++) _wallAtStep[x] = cells.Solid(g.IndexOf(x, 1, 6));
        }

        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion)
        {
            var front0 = -from.Z + halfLength; var front1 = -(from.Z + motion.Z) + halfLength; // map y of the front (faces north)
            if (front0 <= 0.25f && front1 > 0.25f)
                for (var x = (int)((from.X - 1.03f) / 0.25f); x <= (int)((from.X + 1.03f) / 0.25f); x++)
                    if (x >= 0 && x < _wallAtStep.Length && _wallAtStep[x])
                    {
                        Hits++;
                        return new DriveMove(from + motion * ((0.25f - front0) / (front1 - front0)), true, Vector3.UnitZ);
                    }
            return new DriveMove(from + motion, false, Vector3.Zero);
        }

        public float Plough(in PloughQuery q)
        {
            var cut = new List<PloughCell>();
            var plan = HousePlough.Plan(q, Cfg, [0], _ => cells, (_, i) => cells.Solid(i), cut);
            if (plan.Count == 0) return 0;
            cells.Cut(cut.Select(p => p.Cell).ToArray(), false, Cfg);
            Cuts++;
            return plan.Loss;
        }
    }

    [Fact]
    public void A_car_at_60_drives_on_through_the_wall_without_stopping_slowed_by_it()
    {
        var def = Drycastle.ShooterCore.Vehicles.VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Vehicles.Single(v => v.Id == "car");
        var world = new HouseWorld(new HouseCells(House()), def.HalfLength);
        var s = new DriveState { Position = RamBox.ToGodot(new Vector3(2f, -6f, FloorZ)), Speed = 16.7f };
        var minSpeed = s.Speed;
        for (var k = 0; k < 20; k++)
        {
            world.Commit();
            DriveModel.Step(ref s, def, new DriveInput { Throttle = 0 }, Dt, world, default, new PloughStep(true, (ushort)k));
            minSpeed = MathF.Min(minSpeed, s.Speed);
        }
        Assert.Equal(0, world.Hits);              // never stopped by the wall
        Assert.True(world.Cuts >= 1);
        Assert.True(-s.Position.Z + def.HalfLength > 1.5f); // its front is well inside the house
        Assert.InRange(minSpeed, 6f, 13f);        // slowed by the wall (about 5 m/s) and the coasting, far from stopped
    }

    [Fact]
    public void Without_the_plough_the_same_car_stops_at_the_wall()
    {
        var def = Drycastle.ShooterCore.Vehicles.VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Vehicles.Single(v => v.Id == "car");
        var world = new HouseWorld(new HouseCells(House()), def.HalfLength);
        var s = new DriveState { Position = RamBox.ToGodot(new Vector3(2f, -6f, FloorZ)), Speed = 16.7f };
        for (var k = 0; k < 20; k++) { world.Commit(); DriveModel.Step(ref s, def, new DriveInput(), Dt, world); }
        Assert.True(world.Hits > 0);
        Assert.Equal(0, world.Cuts);
    }
}
