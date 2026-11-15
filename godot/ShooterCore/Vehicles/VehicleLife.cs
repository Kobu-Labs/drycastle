namespace Drycastle.ShooterCore;

/// <summary>Where a server-side vehicle is between full health and its return after a blast. The machine is <see cref="VehicleLifecycle.Spec"/>.</summary>
public enum VehicleLife : byte
{
    /// <summary>In the world with hit points left (possibly damaged and smoking).</summary>
    Alive,
    /// <summary>Out of hit points and about to blow: the blast is scheduled (at once, or after the chain-reaction delay).</summary>
    Doomed,
    /// <summary>Blown up: gone from the world (no collision, no driving) until its return time.</summary>
    Destroyed,
}

/// <summary>
/// The hit points and the life of one vehicle, the part of <c>ServerCar</c> that has no Godot in it. It replaces the <c>Hp</c>, <c>DetonateAt = -1</c> (no
/// blast scheduled) and <c>Destroyed</c> + <c>ReturnAt</c> fields that together said which life the vehicle was in: now the state says it, and the times are
/// only valid in the state that owns them. Times are on the server's absolute clock (seconds).
/// <para>
/// Beside this machine, not in it: the thrown flag (the Bigfoot's arc, <c>VehicleThrow</c>), the rest hysteresis (<c>Asleep</c>/<c>RestCount</c>,
/// <see cref="ParkedRules"/>) and the abandon timer (<see cref="AbandonTimer"/>). All three are cleared by a return, whatever life the vehicle was in.
/// </para>
/// </summary>
public sealed class VehicleLifecycle
{
    /// <summary>
    /// Alive until the hit that takes the last hit point; doomed until the blast goes off (<see cref="DetonationDue"/>); destroyed until the return
    /// (<see cref="ReturnDue"/>), which repairs it. A return of any other kind (the abandon timer, falling out of the world) puts a doomed vehicle back too,
    /// repaired: it must not blow up at its origin with no hit points.
    /// </summary>
    public static readonly StateMachineSpec<VehicleLife> Spec = StateMachineSpec<VehicleLife>.Define("Vehicle.Life", "A server-side vehicle: alive, doomed (blast scheduled), destroyed until it returns.", VehicleLife.Alive)
        .Allow(VehicleLife.Alive, VehicleLife.Doomed)
        .Allow(VehicleLife.Doomed, VehicleLife.Destroyed, VehicleLife.Alive)
        .Allow(VehicleLife.Destroyed, VehicleLife.Alive)
        .Note(VehicleLife.Alive, "Hit points above zero. A return that does not repair (abandoned, out of the world) keeps a damaged vehicle damaged.")
        .Note(VehicleLife.Doomed, "Hit points are zero and DetonateAt is set: the blast goes off when the clock reaches it (the chain delay after a neighbour's blast, else this tick). A return repairs it.")
        .Note(VehicleLife.Destroyed, "Blown up, no collision, nobody aboard. ReturnAt is set; the return repairs it and puts it back at its origin.")
        .Build();

    readonly float _maxHp;
    readonly StateMachine<VehicleLife> _life = new(Spec);
    double _detonateAt, _returnAt;

    public VehicleLifecycle(float maxHp)
    {
        _maxHp = maxHp;
        Hp = maxHp;
    }

    /// <summary>Hidden hit points (ticket #103): only the damage stage derived from them ever reaches clients.</summary>
    public float Hp { get; private set; }

    /// <summary>
    /// The wheels the Thieves have taken (GLOSSARY "Loot", docs/events/thieves/04): bit 0 front left, 1 front right, 2 back left, 3 back right. Not part of the machine (a car drives exactly as before) but it
    /// lives and dies with the life: <see cref="Reset"/> puts the wheels back whenever it restores the hit points (a round start, a return after destruction); an abandon return keeps them taken,
    /// as it keeps damage. Replicated in bits 2-5 of the vehicle record's health byte (<see cref="VehicleRecord.Make"/>).
    /// </summary>
    public byte WheelsOff { get; private set; }

    public const int Wheels = 4;

    public bool WheelOff(int wheel) => wheel is >= 0 and < Wheels && (WheelsOff & (1 << wheel)) != 0;

    public void TakeWheel(int wheel) { if (wheel is >= 0 and < Wheels) WheelsOff |= (byte)(1 << wheel); }

    public void ReturnWheel(int wheel) { if (wheel is >= 0 and < Wheels) WheelsOff &= (byte)~(1 << wheel); }

    public VehicleLife State => _life.Current;
    public bool Destroyed => _life.Is(VehicleLife.Destroyed);
    public bool Doomed => _life.Is(VehicleLife.Doomed);

    /// <summary>
    /// Takes <paramref name="amount"/> off the hit points. The hit that leaves none schedules the blast <paramref name="delay"/> seconds after <paramref name="now"/>
    /// (0: this tick's vehicle step); a vehicle that is already doomed keeps its first date. Nothing happens to a destroyed vehicle. True when this hit doomed it.
    /// </summary>
    public bool Damage(float amount, double now, float delay)
    {
        if (Destroyed) return false;
        Hp = MathF.Max(0, Hp - amount);
        if (Hp > 0 || Doomed) return false;
        _detonateAt = now + delay;
        _life.Enter(VehicleLife.Doomed);
        return true;
    }

    /// <summary>The scheduled blast is due at <paramref name="now"/>.</summary>
    public bool DetonationDue(double now) => Doomed && now >= _detonateAt;

    /// <summary>The blast goes off: the vehicle is gone until <paramref name="returnAt"/>.</summary>
    public void Destroy(double returnAt)
    {
        _life.Enter(VehicleLife.Destroyed);
        _returnAt = returnAt;
    }

    /// <summary>The destroyed vehicle's return time has come.</summary>
    public bool ReturnDue(double now) => Destroyed && now >= _returnAt;

    /// <summary>
    /// Back at the origin. A <paramref name="repair"/> (a round start, a return after destruction) restores the hit points; so does any return of a vehicle that
    /// was doomed or destroyed (it cannot come back with none). Otherwise (the abandon timer, falling out of the world) a damaged vehicle stays damaged.
    /// True when the hit points were restored.
    /// </summary>
    public bool Reset(bool repair)
    {
        if (!repair && State == VehicleLife.Alive) return false;
        Hp = _maxHp;
        WheelsOff = 0;
        _life.Enter(VehicleLife.Alive);
        return true;
    }
}
