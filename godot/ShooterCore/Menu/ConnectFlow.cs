using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Menu;

/// <summary>The three faces of the connect screen.</summary>
public enum ConnectState
{
    /// <summary>Address, port and name are being typed.</summary>
    Form,
    /// <summary>A reachability probe is running (visible timeout; Esc / Cancel aborts).</summary>
    Connecting,
    /// <summary>What happened and what to do; Back returns to the form, Try again probes again.</summary>
    Error,
}

/// <summary>What Esc / Back on the connect screen does in its current state.</summary>
public enum ConnectBack { CancelAttempt, LeaveError, LeaveScreen }

/// <summary>
/// The connect screen's state machine (Godot-free): form, connecting, error. It owns the probe clock (<see cref="ProbeTimeout"/>) so the screen keeps no
/// <c>_elapsed</c>, and raises <see cref="LeftConnecting"/> when an attempt ends so the probe's sockets are released in one place whichever way it ended.
/// Timeouts of one connect attempt, for the record: this probe 6 s, then the join in <c>NetClient</c> (10 s, a separate handshake after loading), and
/// loading itself has none (it blocks the main thread until the map is built).
/// </summary>
public sealed class ConnectFlow
{
    /// <summary>Seconds the reachability probe waits for the server before it reports a timeout.</summary>
    public const double ProbeTimeout = 6.0;

    public static readonly StateMachineSpec<ConnectState> Spec = StateMachineSpec<ConnectState>.Define(
            "Menu.Connect", "The connect screen: edit the target, probe it with a visible timeout, show what went wrong.", ConnectState.Form)
        .Allow(ConnectState.Form, ConnectState.Connecting, ConnectState.Error)
        .Allow(ConnectState.Connecting, ConnectState.Form, ConnectState.Error)
        .Allow(ConnectState.Error, ConnectState.Form, ConnectState.Connecting)
        .Note(ConnectState.Form, "Fields editable. Connect is enabled only for a valid form. A failed join or lost connection reopens the screen as Error instead.")
        .Note(ConnectState.Connecting, "Probe running; its sockets are released when this state ends, however it ends. Success hands over to loading (the screen is replaced).")
        .Note(ConnectState.Error, "Probe failed (timeout, refused, unresolvable, local socket) or the last join failed. Try again goes straight back to Connecting.")
        .Build();

    readonly StateMachine<ConnectState> _fsm = new(Spec);

    public ConnectFlow() => _fsm.Changed += (from, _) => { if (from == ConnectState.Connecting) LeftConnecting?.Invoke(); };

    public ConnectState State => _fsm.Current;
    public bool Is(ConnectState s) => _fsm.Is(s);

    /// <summary>Seconds since the probe started (0 outside Connecting).</summary>
    public double Elapsed => State == ConnectState.Connecting ? _fsm.Elapsed : 0;

    /// <summary>Raised when Connecting ends (back to the form, or to the error): release the probe here.</summary>
    public event Action? LeftConnecting;

    /// <summary>Connect pressed (or Enter): starts a probe from the form or from the error. False while one is already running.</summary>
    public bool Begin() => _fsm.Enter(ConnectState.Connecting);

    /// <summary>The probe failed or the join failed: shows the error. Allowed from any state (the screen can open with the error of a failed join).</summary>
    public void Fail() => _fsm.Enter(ConnectState.Error);

    /// <summary>Error or cancelled attempt back to the form.</summary>
    public void ToForm() => _fsm.Enter(ConnectState.Form);

    /// <summary>Advances the probe clock. True once the probe has waited <see cref="ProbeTimeout"/>.</summary>
    public bool Tick(double dt)
    {
        if (State != ConnectState.Connecting) return false;
        _fsm.Tick((float)dt);
        return _fsm.Elapsed >= ProbeTimeout;
    }

    /// <summary>Esc / Back: the one-level-up rule. Cancelling an attempt or leaving the error already moves to the form.</summary>
    public ConnectBack Back()
    {
        switch (State)
        {
            case ConnectState.Connecting: ToForm(); return ConnectBack.CancelAttempt;
            case ConnectState.Error: ToForm(); return ConnectBack.LeaveError;
            default: return ConnectBack.LeaveScreen;
        }
    }
}
