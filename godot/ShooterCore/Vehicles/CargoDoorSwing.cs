using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// How far a cargo area's doors stand open as a client draws them (the boot lid, the hatch, the van's rear doors: <see cref="VehicleCargoVolumeDef.Doors"/>). One number, 0 closed to 1 open, that
/// moves toward the wanted end in <see cref="Seconds"/> and eases at both ends; a door told to reverse half way turns back from where it is. Not a state machine: whether the doors are open is the
/// server's to say (the caller's <see cref="SetOpen"/>), this is only the swing between the two.
/// </summary>
public sealed class CargoDoorSwing
{
    /// <summary>The time a door takes from closed to open or back, seconds.</summary>
    public const float Seconds = 0.4f;

    float _open, _target;

    /// <summary>0 closed .. 1 open, linear in time.</summary>
    public float Open => _open;
    /// <summary>The door's turn as a share of its open angle: <see cref="Open"/> eased (slow at both ends).</summary>
    public float Eased => _open * _open * (3 - 2 * _open);
    /// <summary>Whether the door still moves toward its end.</summary>
    public bool Moving => _open != _target;

    /// <summary>Wants the doors open or closed: they swing there (from wherever they are). <paramref name="instant"/>: they stand there at once (a vehicle that comes into view already open).</summary>
    public void SetOpen(bool open, bool instant = false)
    {
        _target = open ? 1f : 0f;
        if (instant) _open = _target;
    }

    /// <summary>Advances <paramref name="dt"/> seconds; true while the door moved.</summary>
    public bool Step(float dt)
    {
        if (!Moving) return false;
        _open = Ramp.MoveToward(_open, _target, dt / Seconds);
        return true;
    }
}
