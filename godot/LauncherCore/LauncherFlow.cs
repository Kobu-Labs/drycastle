namespace Drycastle.LauncherCore;

public enum LauncherState { Checking, Ready, UpdateAvailable, Working, Failed }

/// <summary>Working has a point of no return: the swap of the install folder cannot be cancelled.</summary>
public enum WorkStage { Downloading, Installing }

/// <summary>
/// The launcher's flow, Godot-free: the state, which moves are legal, a generation ticket for update checks and the "can it still be cancelled" rule.
/// <para>
/// Checks: <see cref="TryBeginCheck"/> hands out a ticket and every later result is applied only while <see cref="IsCurrent"/> says it is still the newest.
/// Without it, closing Settings (channel changed) while a check is in flight let the older answer overwrite the newer one, or start a second installer on the same root.
/// </para>
/// <para>
/// Install: <see cref="TryBeginInstall"/> is the single gate; while Working no check may start (it would race the installer for the install folder).
/// <see cref="CanCancel"/> turns false once the install enters its swap phase, so the UI stops offering a Cancel that cannot work.
/// </para>
/// LauncherCore deliberately does not reference ShooterCore (the launcher stays small), so this machine has its own table rather than a
/// <c>StateMachineSpec</c>; <see cref="Allows"/> is the single declaration and the tests pin it.
/// </summary>
public sealed class LauncherFlow
{
    static readonly Dictionary<LauncherState, LauncherState[]> Next = new()
    {
        [LauncherState.Checking] = new[] { LauncherState.Checking, LauncherState.Ready, LauncherState.UpdateAvailable, LauncherState.Working, LauncherState.Failed },
        [LauncherState.Ready] = new[] { LauncherState.Checking, LauncherState.Failed },
        [LauncherState.UpdateAvailable] = new[] { LauncherState.Checking, LauncherState.Working, LauncherState.Failed },
        [LauncherState.Working] = new[] { LauncherState.Ready, LauncherState.Failed },
        [LauncherState.Failed] = new[] { LauncherState.Checking, LauncherState.Failed },
    };

    /// <summary>Whether the launcher may go from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool Allows(LauncherState from, LauncherState to) => Next[from].Contains(to);

    int _ticket;

    public LauncherState State { get; private set; } = LauncherState.Checking;
    public WorkStage Stage { get; private set; } = WorkStage.Downloading;

    /// <summary>Working, and the download phase: cancelling still leaves the installed version untouched.</summary>
    public bool CanCancel => State == LauncherState.Working && Stage == WorkStage.Downloading;

    /// <summary>Working, in the swap phase: nothing can stop it and the UI says so.</summary>
    public bool Installing => State == LauncherState.Working && Stage == WorkStage.Installing;

    /// <summary>Starts an update check and returns its ticket. False while an install runs (it owns the install folder); the caller then does nothing.</summary>
    public bool TryBeginCheck(out int ticket)
    {
        ticket = _ticket;
        if (State == LauncherState.Working) return false;
        ticket = ++_ticket;
        State = LauncherState.Checking;
        return true;
    }

    /// <summary>The check with this ticket is still the newest operation (no newer check or install started since).</summary>
    public bool IsCurrent(int ticket) => ticket == _ticket && State == LauncherState.Checking;

    /// <summary>Starts the install. False when one is already running or the state does not allow it (a stale button press, a second trigger).</summary>
    public bool TryBeginInstall()
    {
        if (!Allows(State, LauncherState.Working) || State == LauncherState.Working) return false;
        _ticket++; // any check still in flight is now stale
        State = LauncherState.Working;
        Stage = WorkStage.Downloading;
        return true;
    }

    /// <summary>The install reached its swap phase (no more cancelling).</summary>
    public void MarkInstalling()
    {
        if (State == LauncherState.Working) Stage = WorkStage.Installing;
    }

    /// <summary>Moves to <paramref name="next"/> when legal (never into Checking or Working: those have their own gate). False when not.</summary>
    public bool Finish(LauncherState next)
    {
        if (next is LauncherState.Checking or LauncherState.Working || !Allows(State, next)) return false;
        State = next;
        return true;
    }

    /// <summary>Hint line under the card (translation key): settings first, then what the busy state allows.</summary>
    public string HintKey(bool settingsOpen) =>
        settingsOpen ? "hint_settings"
        : Installing ? "hint_installing"
        : State == LauncherState.Working ? "hint_busy"
        : "hint_main";
}
