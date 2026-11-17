namespace Drycastle.ShooterCore;

/// <summary>Whether a trailer with a load (the rocket cart, <see cref="VehicleTrailerDef.LoadedHp"/>) has it. The machine is <see cref="CartCargo.Spec"/>.</summary>
public enum CartLoad : byte { Loaded, Empty }

/// <summary>
/// What a rocket cart carries (server-owned, replicated as one bit of the vehicle record, <see cref="VehicleRecord.Loaded"/>): the missile on it or not. A loaded cart is fragile and goes
/// off when destroyed (the server's <c>OnRocketCartDetonated</c>); an empty one is a small ordinary vehicle. The hit points that go with each are data
/// (<see cref="VehicleTrailerDef.LoadedHp"/> and the type's <see cref="VehicleDef.Hp"/>). Pure: the server keeps one per cart, the tests see what it says.
/// </summary>
public sealed class CartCargo
{
    /// <summary>Loaded until something takes the missile off (the launcher, an admin); loaded again by putting it back. A round reset or a return loads it again.</summary>
    public static readonly StateMachineSpec<CartLoad> Spec = StateMachineSpec<CartLoad>.Define("RocketCart.Load", "A rocket cart: carrying its missile, or empty.", CartLoad.Loaded)
        .Allow(CartLoad.Loaded, CartLoad.Empty)
        .Allow(CartLoad.Empty, CartLoad.Loaded)
        .Note(CartLoad.Loaded, "The missile is on the cart: low hit points (loaded_hp), and when the cart is destroyed it detonates (the Scud blast).")
        .Note(CartLoad.Empty, "Nothing on it: the type's own hit points, and a destroyed cart blows up like a small ordinary vehicle.")
        .Build();

    readonly StateMachine<CartLoad> _fsm = new(Spec);

    public bool Loaded => _fsm.Is(CartLoad.Loaded);

    /// <summary>Puts the load on or takes it off. True when that changed it.</summary>
    public bool Set(bool loaded) => _fsm.Enter(loaded ? CartLoad.Loaded : CartLoad.Empty);

    /// <summary>Back to the start of a life: loaded, with no change event (a round reset, a return after destruction).</summary>
    public void Reset() => _fsm.Reset(CartLoad.Loaded);

    /// <summary>The full hit points of a trailer of this type with its present load: <see cref="VehicleTrailerDef.LoadedHp"/> while loaded (when the type has a load), else the type's.</summary>
    public float MaxHp(VehicleDef def) => Loaded && def.Trailer is { HasCargo: true } t ? t.LoadedHp : def.Hp;

    /// <summary>Whether a destroyed trailer of this type detonates: it has a load and was carrying it.</summary>
    public bool Detonates(VehicleDef def) => Loaded && def.Trailer is { HasCargo: true };
}

/// <summary>What the key that unhitches did this tick (<see cref="UnhitchKey.Tick"/>).</summary>
public enum UnhitchAction : byte { None, Unhitch, Exit }

/// <summary>The states of <see cref="UnhitchKey"/>.</summary>
public enum UnhitchKeyState : byte { Idle, Holding, Spent }

/// <summary>
/// The interact key of a tractor's driver while a trailer is on the hook: the key leaves the vehicle (as it always does) and also unhitches, so the two are told apart by how long it is
/// held. A tap leaves the vehicle when the key is let go; held for <see cref="HoldSeconds"/> it unhitches, once, and what follows (the key still down) is nothing. A key that is already
/// down when the machine starts (the trailer latched while it was held) must be let go first. Pure, ticked by the server for each input with the input's <c>dt</c>: the same hold on every
/// client's frame rate, since it is the server that counts.
/// </summary>
public sealed class UnhitchKey
{
    /// <summary>How long the key is held to unhitch, seconds.</summary>
    public const float HoldSeconds = 0.6f;

    public static readonly StateMachineSpec<UnhitchKeyState> Spec = StateMachineSpec<UnhitchKeyState>.Define("Trailer.UnhitchKey", "The driver's interact key while towing: a tap leaves the vehicle, a hold unhitches the trailer.", UnhitchKeyState.Idle)
        .Allow(UnhitchKeyState.Idle, UnhitchKeyState.Holding)
        .Allow(UnhitchKeyState.Holding, UnhitchKeyState.Idle, UnhitchKeyState.Spent)
        .Allow(UnhitchKeyState.Spent, UnhitchKeyState.Idle)
        .Note(UnhitchKeyState.Idle, "Key up. A press starts the hold.")
        .Note(UnhitchKeyState.Holding, "Key down, counting HoldSeconds. Let go before the end: a tap, the driver leaves the vehicle. Held to the end: the trailer is unhitched.")
        .Note(UnhitchKeyState.Spent, "The hold did its work (or the key was down when the trailer latched): nothing happens until the key is let go.")
        .Build();

    readonly StateMachine<UnhitchKeyState> _fsm = new(Spec);

    public UnhitchKeyState State => _fsm.Current;

    /// <summary>The trailer was just latched, or the machine is reused for another: a key that is down now must be let go before it counts.</summary>
    public void Arm(bool keyDown) => _fsm.Reset(keyDown ? UnhitchKeyState.Spent : UnhitchKeyState.Idle);

    /// <summary>One input: the key as it is (<paramref name="down"/>) for <paramref name="dt"/> seconds.</summary>
    public UnhitchAction Tick(bool down, float dt)
    {
        switch (_fsm.Current)
        {
            case UnhitchKeyState.Idle:
                if (down) _fsm.Enter(UnhitchKeyState.Holding, HoldSeconds);
                return UnhitchAction.None;
            case UnhitchKeyState.Holding:
                if (!down) { _fsm.Enter(UnhitchKeyState.Idle); return UnhitchAction.Exit; }
                if (_fsm.Tick(dt)) { _fsm.Enter(UnhitchKeyState.Spent); return UnhitchAction.Unhitch; }
                return UnhitchAction.None;
            default:
                if (!down) _fsm.Enter(UnhitchKeyState.Idle);
                return UnhitchAction.None;
        }
    }
}
