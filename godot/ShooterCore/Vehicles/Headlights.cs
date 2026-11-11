namespace Suchohrad.ShooterCore;

/// <summary>
/// Vehicle headlights (ticket: vehicle headlights). One bit per vehicle, replicated in the record (see <see cref="VehicleRecord.Lights"/>).
/// The server decides: a driver getting in switches them on when it is night (<see cref="DayCurve.NightFactorAt"/> at the server clock),
/// and the H key (<see cref="MoveButtons.Headlights"/>) toggles them on its press edge, like the Use key. Leaving the seat switches them off.
/// </summary>
public static class Headlights
{
    /// <summary>NightFactor (0 full day .. 1 full night) from which entering a vehicle switches the headlights on.</summary>
    public const float NightOn = 0.5f;

    /// <summary>Lights state when a driver gets in at this hour of the server's day clock.</summary>
    public static bool OnEnterAt(double hour) => DayCurve.NightFactorAt(hour) >= NightOn;

    public static bool Toggle(bool on) => !on;

    /// <summary>Rising edge of a held key: true on the tick it goes down, and <paramref name="wasDown"/> follows the key.</summary>
    public static bool Edge(bool down, ref bool wasDown)
    {
        var edge = down && !wasDown;
        wasDown = down;
        return edge;
    }
}
