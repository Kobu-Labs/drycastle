using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class VehicleRestRulesTests
{
    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Heli => Shipped.Vehicles.Single(v => v.Id == "utility_helicopter");
    static VehicleDef Car => Shipped.Vehicles.Single(v => v.Id == "amphibious");

    /// <summary>A flat dry field at height 0 that stops a helicopter landing on it.</summary>
    sealed class Field : IFlightWorld, IDriveWorld
    {
        public float GroundY(Vector3 position) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public FlightBounds Bounds => FlightBounds.None;
        public FlightMove Move(Vector3 from, float yaw, Vector3 motion)
        {
            if (motion.Y < 0 && from.Y + motion.Y < 0)
                return new FlightMove(from + motion * Math.Clamp(from.Y / -motion.Y, 0f, 1f), true, Vector3.UnitY);
            return new FlightMove(from + motion, false, Vector3.Zero);
        }
        DriveMove IDriveWorld.Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    static FlightState GroundedHeli() => new() { Position = Vector3.Zero, Grounded = true };

    [Fact]
    public void AHelicopterStandingOnItsSkidsWithItsRotorStoppedRests()
    {
        var s = GroundedHeli();
        Assert.True(FlightRest.IsAtRest(s, s));
    }

    [Fact]
    public void AHelicopterWhoseRotorIsStillTurningDoesNotRest()
    {
        var before = GroundedHeli();
        var after = before; after.Rotor = 0.2f;
        Assert.False(FlightRest.IsAtRest(before, after));
    }

    [Fact]
    public void AHelicopterInTheAirDoesNotRest()
    {
        var s = GroundedHeli(); s.Grounded = false; s.Position.Y = 30;
        Assert.False(FlightRest.IsAtRest(s, s));
    }

    [Fact]
    public void AHelicopterStillMovingOrTiltedDoesNotRest()
    {
        var s = GroundedHeli();
        var moved = s; moved.Position.X += 0.001f;
        Assert.False(FlightRest.IsAtRest(s, moved));
        var turned = s; turned.Yaw = 0.01f;
        Assert.False(FlightRest.IsAtRest(s, turned));
        var tilted = s; tilted.Roll = 0.01f;
        Assert.False(FlightRest.IsAtRest(s, tilted));
        var keyed = s; keyed.InputY = 0.3f;
        Assert.False(FlightRest.IsAtRest(s, keyed));
    }

    [Fact]
    public void AHelicopterInWaterOrAtTheMapEdgeOrHitSomethingDoesNotRest()
    {
        var s = GroundedHeli();
        var wet = s; wet.InWater = true;
        Assert.False(FlightRest.IsAtRest(s, wet));
        var edge = s; edge.AtEdge = true;
        Assert.False(FlightRest.IsAtRest(s, edge));
        var hit = s; hit.Impact = 2f;
        Assert.False(FlightRest.IsAtRest(s, hit));
    }

    [Fact]
    public void AHelicopterStandingStillStaysAtRestOnceItIsGroundedFromAStep()
    {
        // a driverless step on the ground changes nothing once the rotor has stopped: the rule and the model agree
        var s = GroundedHeli();
        var w = new Field();
        var next = s;
        FlightModel.Step(ref next, Heli, FlightInput.Pilotless(false), Dt, w);
        Assert.True(FlightRest.IsAtRest(s, next));
        Assert.Equal(s.Position, next.Position);
    }

    [Fact]
    public void AHelicopterHoveringOrDescendingWithoutAPilotKeepsStepping()
    {
        // airborne, nobody flying: it sinks until it lands, and only then does it rest
        var s = new FlightState { Position = new Vector3(0, 40, 0), Rotor = 1 };
        var w = new Field();
        for (var i = 0; i < 60 * 60; i++)
        {
            var before = s;
            FlightModel.Step(ref s, Heli, FlightInput.Pilotless(false), Dt, w);
            if (s.Grounded) break;
            Assert.False(FlightRest.IsAtRest(before, s));
        }
        Assert.True(s.Grounded);
    }

    [Fact]
    public void ADrivenVehicleOnItsWheelsAtAStandstillRests()
    {
        var s = new DriveState { Position = Vector3.Zero };
        Assert.True(DriveRest.IsAtRest(s, s));
    }

    [Fact]
    public void ADrivenVehicleStillRollingOrSkiddingDoesNotRest()
    {
        var s = new DriveState { Position = Vector3.Zero };
        var rolling = s; rolling.Speed = 0.01f;
        Assert.False(DriveRest.IsAtRest(s, rolling));
        var slipping = s; slipping.Slip = 0.01f;
        Assert.False(DriveRest.IsAtRest(s, slipping));
        var falling = s; falling.Airborne = true; falling.VerticalSpeed = -2f;
        Assert.False(DriveRest.IsAtRest(s, falling));
        var climbing = s; climbing.Sink = 0.3f;
        Assert.False(DriveRest.IsAtRest(s, climbing));
        var turning = s; turning.Yaw = 0.02f;
        Assert.False(DriveRest.IsAtRest(s, turning));
    }

    [Fact]
    public void ABoatFloatingStillRestsButADriftingOneDoesNot()
    {
        var floating = new DriveState { Position = new Vector3(0, 0.3f, -5), Afloat = true };
        Assert.True(DriveRest.IsAtRest(floating, floating));
        var drifting = floating; drifting.Position.X += 0.02f;
        Assert.False(DriveRest.IsAtRest(floating, drifting));
        var enteredWater = floating; enteredWater.EnteredWater = true;
        Assert.False(DriveRest.IsAtRest(floating, enteredWater));
        var leftWater = floating; leftWater.LeftWater = true;
        Assert.False(DriveRest.IsAtRest(floating, leftWater));
    }

    [Fact]
    public void ADrivenVehicleWhoseWheelsAreStillOnTheRampOrHitAWallDoesNotRest()
    {
        var s = new DriveState { Position = Vector3.Zero };
        var keyed = s; keyed.Throttle = 0.5f;
        Assert.False(DriveRest.IsAtRest(s, keyed));
        var hit = s; hit.Impact = 3f;
        Assert.False(DriveRest.IsAtRest(s, hit));
    }

    [Fact]
    public void AnAmphibiousVehicleCoastingToAStopRestsOnlyWhenItHasStopped()
    {
        // hands off, from a roll: it keeps stepping until the speed has gone, then the rule and the model agree on rest
        var s = new DriveState { Position = Vector3.Zero, Speed = 8f };
        var w = new Field();
        var rested = false;
        for (var i = 0; i < 60 * 60 && !rested; i++)
        {
            var before = s;
            DriveModel.Step(ref s, Car, DriveInput.None, Dt, w);
            rested = DriveRest.IsAtRest(before, s);
            if (!rested) Assert.True(MathF.Abs(s.Speed) > 0 || s.Position != before.Position || s.Impact > 0);
        }
        Assert.True(rested);
        Assert.Equal(0f, s.Speed);
    }
}
