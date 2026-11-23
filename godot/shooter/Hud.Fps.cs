using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter;

/// <summary>
/// The FPS counter in the HUD's top right corner (#340), toggled by the client-side /fps chat command and remembered in
/// <see cref="GameSettings.ShowFps"/>. One card: the frame rate, and under it the server ticks received per second (TPS).
/// It is text on a panel (readable on any background, never colour alone), ignores the mouse, has a fixed size, and refreshes
/// twice a second (<see cref="FpsReadout"/>). The kill feed sits below it while it is on.
/// </summary>
public partial class Hud
{
    /// <summary>Panel height: two body lines plus the panel's padding (FPS over TPS).</summary>
    const int FpsChipHeight = 64, FpsChipWidth = 128;

    PanelContainer _fpsChip = null!;
    Label _fpsLabel = null!, _tpsLabel = null!;
    readonly FpsReadout _fps = new();
    long _snapshots;

    void BuildFps()
    {
        _fpsChip = Place(P("HudPanel"), 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin, FpsChipWidth, FpsChipHeight);
        var card = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _fpsLabel = L(FpsReadout.Format(0), null, HorizontalAlignment.Right);
        _tpsLabel = L(FpsReadout.NoTps, null, HorizontalAlignment.Right);
        card.AddChild(_fpsLabel);
        card.AddChild(_tpsLabel);
        _fpsChip.AddChild(card);
        _root.AddChild(_fpsChip);
        ApplyFps();
    }

    /// <summary>The running count of server snapshots received; the TPS line is its rate (set by the client every frame).</summary>
    public void SetSnapshotCount(long count) => _snapshots = count;

    /// <summary>Shows or hides the counter to match the setting, and moves the kill feed clear of it.</summary>
    void ApplyFps()
    {
        if (_fpsChip == null) return; // before the HUD is built
        var on = Settings.ShowFps;
        _fpsChip.Visible = on;
        _fps.Reset();
        var shift = on && !ControlMode ? FpsChipHeight + ShooterTheme.Space : 0; // the feed outlives the panels in a control session, where the chip is hidden
        _feedBox.OffsetTop = ShooterTheme.Margin + shift;
        _feedBox.OffsetBottom = _feedBox.OffsetTop + FeedRows * 40 + (FeedRows - 1) * 4;
    }

    void UpdateFps(double now)
    {
        if (!Settings.ShowFps) return;
        if (_fps.Sample(now, Engine.GetFramesPerSecond(), _snapshots) is not { } r) return;
        Set(_fpsLabel, r.Fps);
        Set(_tpsLabel, r.Tps);
    }

    /// <summary>Flips the counter, saves the choice and answers in the chat log.</summary>
    void ToggleFps()
    {
        var s = GameSettings.Current.Clone();
        s.ShowFps = !s.ShowFps;
        GameSettings.Apply(s); // raises Changed: ApplyFps runs
        s.Save();
        AddChatLine(new ChatLine(ChatLineKind.Info, "", Strings.T(s.ShowFps ? "hud2.fps.on" : "hud2.fps.off")));
    }
}
