using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>
/// The governor on a throttle input (<see cref="SpeedGovernor"/>; a Cartel van with a Duffel loaded goes at most cartel.json <c>loaded_max_kmh</c>): the pure function, the cap read from the shipped file, and the van on the
/// real drive model with the governor on the server's side (<see cref="DriveModel"/> stepped with the governed input) and the driver's (<see cref="DrivePrediction"/> with <see cref="DrivePrediction.SpeedCapMps"/>), which must agree.
/// </summary>
public class SpeedGovernorTests
{
    const float Dt = 1f / Wire.TickRate;
    const float Cap = 60f / 3.6f;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Vehicles = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static readonly CartelFile Cartel = CartelFile.Load(Path.Combine(DataDir, "cartel.json"));
    static VehicleDef Van => Vehicles.Vehicles.Single(v => v.Id == "cartel_van");

    static DriveInput Keys(float throttle = 0, float steer = 0) => new() { Throttle = throttle, Steer = steer };

    // ---- the function ----

    [Fact]
    public void With_no_cap_the_input_is_as_it_came()
    {
        var input = new DriveInput { Throttle = 1, Steer = -0.5f, Handbrake = true };
        Assert.Equal(input, SpeedGovernor.Apply(input, 40f, null));
    }

    [Fact]
    public void Below_the_band_the_throttle_is_whole_in_the_band_it_eases_off_and_at_the_cap_it_is_nothing()
    {
        Assert.Equal(1f, SpeedGovernor.Apply(Keys(1), Cap - SpeedGovernor.BandMps - 0.1f, Cap).Throttle);
        Assert.Equal(0.5f, SpeedGovernor.Apply(Keys(1), Cap - SpeedGovernor.BandMps / 2, Cap).Throttle, 3);
        Assert.Equal(0f, SpeedGovernor.Apply(Keys(1), Cap, Cap).Throttle);
        Assert.Equal(0f, SpeedGovernor.Apply(Keys(0.5f), Cap + 5f, Cap).Throttle); // faster than the cap (bags loaded while it drove): no push, no brake
    }

    [Fact]
    public void Brake_reverse_steering_and_handbrake_pass_as_they_are()
    {
        // the key against the travel brakes: it gains no speed, so the governor leaves it
        Assert.Equal(-1f, SpeedGovernor.Apply(Keys(-1), Cap + 5f, Cap).Throttle);
        Assert.Equal(1f, SpeedGovernor.Apply(Keys(1), -Cap, Cap).Throttle);
        // reversing: the key's own direction counts, the cap holds a van that backs up too fast
        Assert.Equal(0f, SpeedGovernor.Apply(Keys(-1), -Cap, Cap).Throttle);
        var turned = SpeedGovernor.Apply(new DriveInput { Throttle = 1, Steer = 0.7f, Handbrake = true }, Cap, Cap);
        Assert.Equal(0.7f, turned.Steer);
        Assert.True(turned.Handbrake);
    }

    // ---- the cap of a van ----

    [Fact]
    public void The_shipped_file_caps_the_Cartel_van_at_sixty_kmh_and_no_other_type()
    {
        Assert.Equal(Cap, Cartel.LoadedCapMps("cartel_van")!.Value, 3);
        Assert.Null(Cartel.LoadedCapMps("humvee"));
        Assert.Null(Cartel.LoadedCapMps("car"));
        Assert.Null(CartelFile.Default.LoadedCapMps("humvee"));
        Assert.Equal(Cap, CartelFile.Default.LoadedCapMps("cartel_van")!.Value, 3); // a data directory without the file: the shipped default
    }

    [Fact]
    public void A_loaded_cap_that_is_not_a_speed_is_refused_by_the_file()
    {
        var bad = new HeistData { LoadedMaxKmh = 0 };
        Assert.Throws<DataFileException>(() => bad.Validate("cartel.json: jobs.harvest", 5));
    }

    // ---- the van on the drive model ----

    sealed class Flat : IDriveWorld
    {
        public float GroundY(Vector3 from) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    [Fact]
    public void Empty_the_van_goes_past_sixty_loaded_it_holds_just_under_it_at_full_throttle()
    {
        var empty = new DriveState(); var loaded = new DriveState(); var world = new Flat();
        for (var t = 0f; t < 40f; t += Dt)
        {
            DriveModel.Step(ref empty, Van, Keys(1), Dt, world);
            DriveModel.Step(ref loaded, Van, SpeedGovernor.Apply(Keys(1), loaded.Speed, Cap), Dt, world); // the server's step: StepDrive's governed input
            Assert.True(loaded.Speed <= Cap + 1e-3f, $"at {t:F1} s: {loaded.Speed * 3.6f:F1} km/h");
        }
        Assert.True(empty.Speed * 3.6f > 100f);
        Assert.InRange(loaded.Speed * 3.6f, 57f, 60.01f); // it does get there: the band is not a brake
    }

    [Fact]
    public void The_driver_prediction_with_the_cap_steps_exactly_as_the_server_does()
    {
        var server = new DriveState(); var world = new Flat();
        var predicted = new DrivePrediction(Van, world, new DriveState()) { SpeedCapMps = Cap };
        ushort seq = 0;
        for (var t = 0f; t < 25f; t += Dt)
        {
            var key = Keys(1, MathF.Sin(t));
            DriveModel.Step(ref server, Van, SpeedGovernor.Apply(key, server.Speed, Cap), Dt, world);
            predicted.Step(++seq, key, Dt);
            Assert.Equal(server.Speed, predicted.State.Speed);
            Assert.Equal(server.Position, predicted.State.Position);
        }
        Assert.InRange(predicted.State.Speed * 3.6f, 57f, 60.01f);
    }

    [Fact]
    public void Taking_the_cap_off_lets_the_van_go_on()
    {
        var predicted = new DrivePrediction(Van, new Flat(), new DriveState()) { SpeedCapMps = Cap };
        ushort seq = 0;
        for (var t = 0f; t < 15f; t += Dt) predicted.Step(++seq, Keys(1), Dt);
        Assert.True(predicted.State.Speed <= Cap + 1e-3f);
        predicted.SpeedCapMps = null; // the last bag taken out
        for (var t = 0f; t < 10f; t += Dt) predicted.Step(++seq, Keys(1), Dt);
        Assert.True(predicted.State.Speed > Cap + 3f);
    }
}
