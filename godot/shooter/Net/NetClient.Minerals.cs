using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Mineral Log on the client (N, action "mineral_log"; crystals.md): the key and the pause menu open the screen, Esc goes through <see cref="CloseMineralLog"/>, and
/// while it is open the shell leaves the mouse free (<see cref="MineralLogOpen"/>) and the legs keep working. Nothing is asked of the server yet: no mineral can be found.
/// </summary>
public partial class NetClient
{
    /// <summary>The Mineral Log is up: the mouse is released for it.</summary>
    public bool MineralLogOpen => _hud?.MineralLogOpen ?? false;

    /// <summary>Esc: closes it. True when it was open (the shell then does not open Pause for this Esc).</summary>
    public bool CloseMineralLog() => _hud?.BackMineralLog() ?? false;

    /// <summary>Opens the log (the pause menu's entry).</summary>
    public void OpenMineralLog() => _hud?.OpenMineralLog();

    /// <summary>The key toggles the screen, but not while a menu, the chat box or the corpse chooser is up (<see cref="OverlayArbiter"/>). True when the key was used.</summary>
    bool HandleMineralLogInput(InputEvent e)
    {
        if (_hud == null || Bot || e.IsEcho() || !e.IsActionPressed("mineral_log")) return false;
        if (!_hud.ToggleMineralLog(MenuOpen?.Invoke() == true, ChooserOpen)) return false;
        GetViewport().SetInputAsHandled();
        return true;
    }
}
