using Godot;
using Drycastle.Shooter.HeadsUp.RoundFlow;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.HeadsUp.Badges;

/// <summary>
/// The FPS counter in the HUD's top right corner (#340), toggled by the client-side /fps chat command and remembered in
/// <see cref="GameSettings.ShowFps"/>. One card: the frame rate, and under it the server ticks received per second (TPS).
/// It is text on a panel (readable on any background, never colour alone), ignores the mouse, has a fixed size, and refreshes
/// twice a second (<see cref="FpsReadout"/>). The kill feed sits below it while it is on.
/// </summary>
internal sealed class FpsCounter
{
    /// <summary>Panel height: two body lines plus the panel's padding (FPS over TPS).</summary>
    const int ChipHeight = 64, ChipWidth = 128;

    readonly KillFeed _feed;
    readonly HudState _state;
    readonly PanelContainer _chip;
    readonly Label _fpsLabel, _tpsLabel;
    readonly FpsReadout _readout = new();
    long _snapshots;

    /// <param name="feed">Moved clear of the card while it is on.</param>
    public FpsCounter(Control root, KillFeed feed, HudState state)
    {
        _feed = feed; _state = state;
        _chip = HudUi.Place(HudUi.P("HudPanel"), 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin, ChipWidth, ChipHeight);
        var card = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _fpsLabel = HudUi.L(FpsReadout.Format(0), null, HorizontalAlignment.Right);
        _tpsLabel = HudUi.L(FpsReadout.NoTps, null, HorizontalAlignment.Right);
        card.AddChild(_fpsLabel);
        card.AddChild(_tpsLabel);
        _chip.AddChild(card);
        root.AddChild(_chip);
        Apply();
    }

    /// <summary>The running count of server snapshots received; the TPS line is its rate (set by the client every frame).</summary>
    public void SetSnapshotCount(long count) => _snapshots = count;

    /// <summary>Shows or hides the counter to match the setting, and moves the kill feed clear of it.</summary>
    public void Apply()
    {
        var on = GameSettings.Current.ShowFps;
        _chip.Visible = on;
        _readout.Reset();
        _feed.Shift(on && !_state.ControlMode ? ChipHeight + ShooterTheme.Space : 0); // the feed outlives the panels in a control session, where the chip is hidden
    }

    public void Tick(double now)
    {
        if (!GameSettings.Current.ShowFps) return;
        if (_readout.Sample(now, Engine.GetFramesPerSecond(), _snapshots) is not { } r) return;
        HudUi.Set(_fpsLabel, r.Fps);
        HudUi.Set(_tpsLabel, r.Tps);
    }

    /// <summary>Flips the counter and saves the choice; true when it is on now.</summary>
    public bool Toggle()
    {
        var s = GameSettings.Current.Clone();
        s.ShowFps = !s.ShowFps;
        GameSettings.Apply(s); // raises Changed: Apply runs
        s.Save();
        return s.ShowFps;
    }
}
