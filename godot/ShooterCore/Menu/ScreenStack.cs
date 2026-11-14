namespace Drycastle.ShooterCore;

/// <summary>
/// The menu shell's screen stack, Godot-free. A screen closes itself by identity (<see cref="Pop"/>), never "whatever is on top": two closes in one frame
/// (Esc and a dialog's auto-timer, a button and Esc) would otherwise pop the screen underneath. A close for a screen that is not the top is a no-op and says so.
/// </summary>
public sealed class ScreenStack<T> where T : class
{
    readonly List<T> _items = new();

    public int Count => _items.Count;
    public T? Top => _items.Count > 0 ? _items[^1] : null;
    public bool IsTop(T screen) => _items.Count > 0 && ReferenceEquals(_items[^1], screen);
    public bool Contains(T screen) => _items.Contains(screen);

    /// <summary>Puts <paramref name="screen"/> on top. False (and nothing happens) when it is already in the stack.</summary>
    public bool Push(T screen)
    {
        if (_items.Contains(screen)) return false;
        _items.Add(screen);
        return true;
    }

    /// <summary>Removes <paramref name="screen"/> when it is the top. False when it is not (already closed, or covered by another screen): the caller then does nothing.</summary>
    public bool Pop(T screen)
    {
        if (!IsTop(screen)) return false;
        _items.RemoveAt(_items.Count - 1);
        return true;
    }

    /// <summary>Empties the stack; returns the screens that were in it (bottom first) so the caller can free them.</summary>
    public IReadOnlyList<T> Clear()
    {
        var all = _items.ToArray();
        _items.Clear();
        return all;
    }
}

/// <summary>What Esc does in the menu shell: the topmost UI layer gets it first.</summary>
public enum EscAction
{
    /// <summary>A menu screen is open: it goes one level up (<c>MenuScreen.Back</c>).</summary>
    ScreenBack,
    /// <summary>No screen, and the match closed something of its own (chat box, Equipment screen, tactical map).</summary>
    MatchClosed,
    /// <summary>No screen, nothing of the match to close, a match is running: open Pause.</summary>
    OpenPause,
    /// <summary>Nothing to do (menu with no screen, or no pause screen available).</summary>
    None,
}

/// <summary>
/// Esc routing. A shell screen sits above the match's own panels (chat, Equipment, tactical map), so with one open Esc closes the screen and leaves
/// the panels behind it alone; only with no screen open does the match close its own first and Pause come last.
/// </summary>
public static class MenuEsc
{
    /// <param name="screenOpen">The shell stack is not empty.</param>
    /// <param name="matchBack">Closes the match's own topmost panel; true when it closed one. Called only when no screen is open (it has side effects).</param>
    public static EscAction Decide(bool screenOpen, Func<bool>? matchBack, bool gameActive, bool hasPause)
    {
        if (screenOpen) return EscAction.ScreenBack;
        if (matchBack?.Invoke() == true) return EscAction.MatchClosed;
        return gameActive && hasPause ? EscAction.OpenPause : EscAction.None;
    }
}
