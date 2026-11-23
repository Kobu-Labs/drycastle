namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// What strikes a Thief at the top of a climb when its target's kind says so (<see cref="IStealSource.AloftHazard"/>): the member goes Shocked for <see cref="ShockSeconds"/> at the top, falls to the
/// base (Falling), lies for <see cref="DownSeconds"/> (Down), takes <see cref="GetUpSeconds"/> to get up (GetUp) and climbs the same target again. Costs no hits. Pure data: the kind decides whether and with what times.
/// </summary>
public readonly record struct AloftShock(float ShockSeconds, float DownSeconds, float GetUpSeconds);
