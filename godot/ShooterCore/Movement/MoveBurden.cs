namespace Drycastle.ShooterCore.Movement;

/// <summary>
/// A load on the body that changes how it moves (a carried Duffel: no sprint, slower by its weight). Set on the body by its owner (the server from what the player carries, the client from what it
/// is told the player carries) and applied to every input of the shared step, so prediction, replay and the server move the same way. <see cref="None"/> (and the default) change nothing.
/// </summary>
public readonly record struct MoveBurden(float SpeedScale, bool NoSprint)
{
    public static readonly MoveBurden None = new(1f, false);

    public bool IsNone => !NoSprint && (SpeedScale <= 0f || SpeedScale == 1f);

    /// <summary>The input the step runs with: the sprint key dropped when it is barred, the speed scaled (on top of the weapon's).</summary>
    public MoveInput Apply(in MoveInput input)
    {
        if (IsNone) return input;
        var o = input;
        if (NoSprint) o.Buttons &= ~MoveButtons.Sprint;
        if (SpeedScale > 0f) o.SpeedScale = (input.SpeedScale > 0f ? input.SpeedScale : 1f) * SpeedScale;
        return o;
    }
}
