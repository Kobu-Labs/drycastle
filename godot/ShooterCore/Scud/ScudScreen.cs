namespace Drycastle.ShooterCore;

/// <summary>Which screen of a Scud truck the local player has up.</summary>
public enum ScudScreenMode : byte
{
    /// <summary>Their own view (the body, the cockpit or the chase camera).</summary>
    Closed = 0,
    /// <summary>The operator's targeting screen: the bird's-eye camera is theirs to pan, zoom and lock with.</summary>
    Operating = 1,
    /// <summary>The read-only feed: the operator's camera and lock, watched (and confirmed) from another seat, or from the operator's own seat once the rack rises.</summary>
    Watching = 2,
}

/// <summary>What the client must send after a <see cref="ScudScreen.Update"/>.</summary>
public enum ScudScreenAction : byte { None = 0, SendOpen = 1, SendClose = 2 }

/// <summary>
/// The client's screen mode of a Scud truck (GLOSSARY "Launcher"): the one decision of which view the local player has up, fed once a frame with where they sit and the launcher's state.
/// Pure, so the tests run the client's own rules. The operator opens the screen with the screen key (the server answers by moving the launcher to Targeting; until it does the
/// screen waits a moment, then gives up); any other crew member can open the feed while a screen is live. The screen stays through the erect and the ignition (the operator
/// aborts from it, the crew sees the countdown) and closes when the launcher goes to anything else.
/// </summary>
public sealed class ScudScreen
{
    public static readonly StateMachineSpec<ScudScreenMode> Spec = StateMachineSpec<ScudScreenMode>.Define(
            "Scud.Screen", "Which screen of a Scud truck the local player has up: their own view, the operator's targeting screen, or the read-only feed of it.", ScudScreenMode.Closed)
        .Allow(ScudScreenMode.Closed, ScudScreenMode.Operating, ScudScreenMode.Watching)
        .Allow(ScudScreenMode.Operating, ScudScreenMode.Closed)
        .Allow(ScudScreenMode.Watching, ScudScreenMode.Closed)
        .Note(ScudScreenMode.Closed, "The player's own view. Where every end goes: the screen key again, leaving the truck, the launcher leaving its live states.")
        .Note(ScudScreenMode.Operating, "The operator pans, zooms and locks. After the open request the screen waits for the server's answer (a pending answer) and gives up if it does not come.")
        .Note(ScudScreenMode.Watching, "Another seat's read-only view of the operator's camera and lock, where it confirms or withdraws. The operator's own seat watches once the rack rises.")
        .Build();

    /// <summary>How long the screen waits for the server to move the launcher to Targeting before it gives up, seconds.</summary>
    public const double AnswerGiveUpSeconds = 1.5;

    readonly StateMachine<ScudScreenMode> _fsm = new(Spec);
    PendingAnswer _pending;

    public ScudScreenMode Mode => _fsm.Current;
    /// <summary>The open request is out and the launcher has not moved yet.</summary>
    public bool Pending => _pending.Active;

    /// <summary>The launcher states the screens show: the proposal, the erect and the ignition.</summary>
    public static bool Live(ScudLauncherState s) => s is ScudLauncherState.Targeting or ScudLauncherState.Proposed or ScudLauncherState.Erecting or ScudLauncherState.Launching;

    /// <summary>
    /// One frame. <paramref name="aboard"/>: the player sits in a Scud truck and is alive; <paramref name="isOperator"/>: in its operator seat; <paramref name="toggle"/>: the screen key
    /// went down; <paramref name="back"/>: Back (Esc) went down while a screen is up, one level up. Returns what to send the server.
    /// </summary>
    public ScudScreenAction Update(double now, bool aboard, bool isOperator, ScudLauncherState state, bool toggle, bool back = false)
    {
        if (!aboard)
        {
            _pending.End();
            _fsm.Enter(ScudScreenMode.Closed);
            return ScudScreenAction.None;
        }
        var press = toggle || back && Mode != ScudScreenMode.Closed;
        switch (Mode)
        {
            case ScudScreenMode.Closed:
                if (!toggle) return ScudScreenAction.None;
                if (isOperator && state == ScudLauncherState.Stowed)
                {
                    _fsm.Enter(ScudScreenMode.Operating);
                    _pending.Begin(now);
                    return ScudScreenAction.SendOpen;
                }
                if (Live(state)) _fsm.Enter(isOperator ? ScudScreenMode.Operating : ScudScreenMode.Watching);
                return ScudScreenAction.None;
            case ScudScreenMode.Operating:
                if (press) return Close(state);
                if (state != ScudLauncherState.Stowed) _pending.End();
                if (_pending.GiveUp(now, AnswerGiveUpSeconds)) _fsm.Enter(ScudScreenMode.Closed);
                else if (!_pending.Active && !Live(state)) _fsm.Enter(ScudScreenMode.Closed);
                else if (!isOperator) _fsm.Enter(ScudScreenMode.Closed); // the seat was changed
                return ScudScreenAction.None;
            case ScudScreenMode.Watching:
                if (press || !Live(state)) _fsm.Enter(ScudScreenMode.Closed);
                return ScudScreenAction.None;
        }
        return ScudScreenAction.None;
    }

    ScudScreenAction Close(ScudLauncherState state)
    {
        _pending.End();
        _fsm.Enter(ScudScreenMode.Closed);
        return state is ScudLauncherState.Targeting or ScudLauncherState.Proposed or ScudLauncherState.Stowed ? ScudScreenAction.SendClose : ScudScreenAction.None;
    }
}
