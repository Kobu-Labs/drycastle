using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The FPS counter in the HUD's top right corner (#340), toggled by the client-side /fps chat command and remembered in
/// <see cref="GameSettings.ShowFps"/>. It is text on a panel (readable on any background, never colour alone), ignores the
/// mouse, has a fixed size, and refreshes twice a second (<see cref="FpsReadout"/>). The kill feed sits below it while it is on.
/// </summary>
public partial class Hud
{
    /// <summary>Panel height: one body line plus the panel's padding.</summary>
    const int FpsChipHeight = 40, FpsChipWidth = 128;

    PanelContainer _fpsChip = null!;
    Label _fpsLabel = null!;
    readonly FpsReadout _fps = new();

    void BuildFps()
    {
        _fpsChip = Place(P("HudPanel"), 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin, FpsChipWidth, FpsChipHeight);
        _fpsLabel = L(FpsReadout.Format(0), null, HorizontalAlignment.Right);
        _fpsChip.AddChild(_fpsLabel);
        _root.AddChild(_fpsChip);
        ApplyFps();
    }

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
        if (_fps.Sample(now, Engine.GetFramesPerSecond()) is { } text) Set(_fpsLabel, text);
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
