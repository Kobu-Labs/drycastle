namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// A top speed a driver's throttle may not push a vehicle past (a Cartel van with a Duffel loaded: cartel.json <c>loaded_max_kmh</c>, docs/cartel.md "Cargo vehicles"), as a governor on the throttle <i>input</i>: the
/// forward key is eased off over the last <see cref="BandMps"/> below the cap and is nothing at it, so the vehicle's own drive step (<see cref="DriveModel"/>, untouched) coasts instead of pulling. Brake, reverse,
/// steering and the handbrake pass as they are; a vehicle already faster than the cap (bags loaded while it drove) is not braked, it only gets no more push. Pure: the server's step
/// (<c>ServerCar.StepDrive</c>) and the driver's prediction (<see cref="DrivePrediction"/>) call the same function with the speed of the state they are about to step, so they agree.
/// </summary>
public static class SpeedGovernor
{
    /// <summary>The throttle goes from full to nothing over this many metres a second below the cap.</summary>
    public const float BandMps = 2f;

    /// <summary>
    /// <paramref name="input"/> as the drive step gets it: its throttle, when it drives <paramref name="speed"/> (signed, along the heading) further the way it pushes, scaled down to nothing at <paramref name="capMps"/>.
    /// With no cap (null) it is the input as it came.
    /// </summary>
    public static DriveInput Apply(in DriveInput input, float speed, float? capMps)
    {
        if (capMps is not { } cap || input.Throttle == 0f) return input;
        var along = speed * MathF.Sign(input.Throttle); // the speed in the direction the key pushes: a key against the travel brakes, which is no speed gained
        if (along <= cap - BandMps) return input;
        var governed = input;
        governed.Throttle = input.Throttle * Math.Clamp((cap - along) / BandMps, 0f, 1f);
        return governed;
    }
}
