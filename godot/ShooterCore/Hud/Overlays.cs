namespace Drycastle.ShooterCore;

/// <summary>
/// Who wants the mouse released (a cursor to click with) instead of captured (looking around). The shell asks it every frame and writes
/// <c>Input.MouseMode</c>; any other writer must ask the same question or it fights the shell (last writer in the frame wins).
/// </summary>
public readonly record struct MouseOwners(bool Dead, bool ChatOpen, bool EquipmentOpen, bool TacticalMapOpen, bool ChooserFreesMouse, bool HuntersLogOpen = false)
{
    /// <summary>The respawn screen, the chat box, the Equipment screen, the tactical map (click to set the waypoint), the Hunter's Log and the corpse chooser's item list release the mouse.</summary>
    public bool WantsFreeMouse => Dead || ChatOpen || EquipmentOpen || TacticalMapOpen || ChooserFreesMouse || HuntersLogOpen;

    /// <summary>
    /// The sandbox map closes and gives the mouse back: captured unless a menu has opened meanwhile or another owner still wants the cursor (death, the chat box).
    /// The old code only asked the menu, so it captured the mouse for one frame under the respawn screen until the shell took it back.
    /// </summary>
    public bool CapturesWhenMapCloses(bool menuOpen) => !menuOpen && !WantsFreeMouse;
}

/// <summary>The overlays of the HUD that never show together (the Hunter's Log is a side panel, not full-screen).</summary>
public enum Overlay { Equipment, TacticalMap, HuntersLog }

/// <summary>What the arbiter needs to know. <c>ControlMode</c>: a turret or the missile owns the screen. <c>Ending</c>: the match is over.</summary>
public readonly record struct OverlayContext(bool ChatOpen, bool Dead, bool Ending, bool ControlMode, bool MenuOpen, bool ChooserOpen);

/// <summary>What the overlay key does.</summary>
public enum OverlayKeyVerdict
{
    /// <summary>Not used (typing, a menu is up, the screen is taken): the key stays free for others.</summary>
    Ignore,
    /// <summary>Close it.</summary>
    Close,
    /// <summary>Open it, and close the other overlay (the two never show together).</summary>
    Open,
}

/// <summary>
/// One place that says which HUD overlay may be open given the others, the menu, the chat box and the match: it replaces the "close if blocked" polls and the
/// "do nothing while blocked" key guards that each overlay carried. Pure; the HUD and the client ask it and act on the answer.
/// </summary>
public static class OverlayArbiter
{
    /// <summary>Death, the end of the match or a control session takes the screen: no overlay shows, and an open one closes (silently).</summary>
    public static bool ScreenTaken(in OverlayContext c) => c.Dead || c.Ending || c.ControlMode;

    /// <summary>
    /// An open overlay gives way to a menu (and the Equipment screen and the Hunter's Log also to the corpse chooser). The map did not give way to a menu before: Esc
    /// closed it first, behind the pause menu's back, and a menu that opened by itself sat on top of a map that still held the cursor.
    /// </summary>
    public static bool YieldsToMenu(Overlay o, in OverlayContext c) => o != Overlay.TacticalMap ? c.MenuOpen || c.ChooserOpen : c.MenuOpen;

    /// <summary>The per-frame verdict: should the open overlay close now.</summary>
    public static bool ShouldClose(Overlay o, in OverlayContext c) => ScreenTaken(c) || YieldsToMenu(o, c);

    /// <summary>What the overlay's key does. The Equipment key is also ignored under the corpse chooser; both are ignored under a menu and while typing in the chat box.</summary>
    public static OverlayKeyVerdict Key(Overlay o, bool isOpen, in OverlayContext c)
    {
        if (YieldsToMenu(o, c) || c.ChatOpen) return OverlayKeyVerdict.Ignore;
        if (isOpen) return OverlayKeyVerdict.Close;
        return ScreenTaken(c) ? OverlayKeyVerdict.Ignore : OverlayKeyVerdict.Open;
    }

    /// <summary>The chat key (or "/") opens the box unless it is open or a menu is up.</summary>
    public static bool CanOpenChat(bool chatOpen, bool menuOpen) => !chatOpen && !menuOpen;
}

/// <summary>Where a fading panel is.</summary>
public enum PanelPhase
{
    /// <summary>Hidden, not processing.</summary>
    Closed,
    /// <summary>Wanted open, fading in (or visible at less than full alpha).</summary>
    Opening,
    /// <summary>Wanted open at full alpha.</summary>
    Open,
    /// <summary>No longer wanted, fading out; hidden when it reaches zero.</summary>
    Closing,
}

/// <summary>
/// The fade every full-screen overlay (Equipment, tactical map) did by hand: <c>_open</c>, <c>_alpha</c>, MoveToward per frame, FinishClose, the reduce-motion
/// shortcut. A value type owned by the panel; the panel keeps the Godot side (Visible, Modulate, MouseFilter, SetProcess) and follows <see cref="Alpha"/>
/// and the step results. The arithmetic is the old one (<see cref="Ramp.MoveToward"/> of the alpha by dt / seconds).
/// </summary>
public struct PanelFade
{
    public static readonly StateMachineSpec<PanelPhase> Spec = StateMachineSpec<PanelPhase>.Define("Hud.PanelFade", "A full-screen overlay's open/close fade (Equipment screen, tactical map).", PanelPhase.Closed)
        .Allow(PanelPhase.Closed, PanelPhase.Opening, PanelPhase.Open)
        .Allow(PanelPhase.Opening, PanelPhase.Open, PanelPhase.Closing, PanelPhase.Closed)
        .Allow(PanelPhase.Open, PanelPhase.Closing, PanelPhase.Closed)
        .Allow(PanelPhase.Closing, PanelPhase.Closed, PanelPhase.Opening, PanelPhase.Open)
        .Note(PanelPhase.Closed, "Hidden and not processing; the first frame after Show makes it visible.")
        .Note(PanelPhase.Opening, "Wanted open, alpha ramping up (the fade is 150 ms, none with reduce motion).")
        .Note(PanelPhase.Open, "Wanted open at full alpha. Reduce motion jumps Closed -> Open and Open -> Closed.")
        .Note(PanelPhase.Closing, "Not wanted, alpha ramping down; Show during it turns back to Opening from the current alpha.")
        .Build();

    /// <summary>The panel is wanted open (the old <c>_open</c>): input and focus belong to it.</summary>
    public bool Wanted { get; private set; }
    public float Alpha { get; private set; }
    /// <summary>Visible and processing: from <see cref="Show"/> until the fade-out finishes.</summary>
    public bool Shown { get; private set; }

    public readonly PanelPhase Phase =>
        Wanted ? (Alpha >= 1f ? PanelPhase.Open : PanelPhase.Opening)
        : Shown && Alpha > 0f ? PanelPhase.Closing
        : Shown ? PanelPhase.Closing // zero alpha, not yet finished by a tick: the frame before it hides
        : PanelPhase.Closed;

    /// <summary>Wants it open: visible now; with reduce motion at full alpha at once, else from the current alpha.</summary>
    public void Show(bool reduceMotion)
    {
        Wanted = true; Shown = true;
        if (reduceMotion) Alpha = 1f;
    }

    /// <summary>No longer wanted. With reduce motion it is gone at once (true: the panel hides now); else it fades and <see cref="Tick"/> finishes it.</summary>
    public bool Hide(bool reduceMotion)
    {
        Wanted = false;
        if (!reduceMotion) return false;
        Finish();
        return true;
    }

    /// <summary>
    /// One frame. <c>AlphaChanged</c>: the panel must write <see cref="Alpha"/> to its Modulate. <c>Finished</c>: it faded out, so hide it and stop processing
    /// (the alpha is zero and has been written).
    /// </summary>
    public (bool AlphaChanged, bool Finished) Tick(float dt, float fadeSeconds)
    {
        var target = Wanted ? 1f : 0f;
        var changed = false;
        if (!IsEqualApprox(Alpha, target)) { Alpha = Ramp.MoveToward(Alpha, target, dt / fadeSeconds); changed = true; }
        if (!Wanted && Alpha <= 0f) { Finish(); return (true, true); }
        return (changed, false);
    }

    void Finish() { Alpha = 0f; Shown = false; }

    // Godot's Mathf.IsEqualApprox (relative tolerance of 1e-5, never below it), so the fade stops where the engine's version stopped.
    static bool IsEqualApprox(float a, float b)
    {
        if (a == b) return true;
        var tolerance = 1e-5f * MathF.Abs(a);
        if (tolerance < 1e-5f) tolerance = 1e-5f;
        return MathF.Abs(a - b) < tolerance;
    }
}
