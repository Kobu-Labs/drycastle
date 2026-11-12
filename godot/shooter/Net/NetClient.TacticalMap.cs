using Godot;

namespace Drycastle.Shooter;

/// <summary>The tactical map key (M, rebindable): toggles the HUD's map. Esc closes it through <see cref="CloseTacticalMap"/>.</summary>
public partial class NetClient
{
    /// <summary>Closes the tactical map. True when it was open (the shell then does not open Pause for this Esc).</summary>
    public bool CloseTacticalMap() => _hud?.CloseTacticalMap() ?? false;

    /// <summary>The map key toggles the map, but not while a menu is open or the chat box is (the HUD checks the chat). True when the key was used.</summary>
    bool HandleMapInput(InputEvent e)
    {
        if (_hud == null || Bot || e.IsEcho() || !e.IsActionPressed("map")) return false;
        if (MenuOpen?.Invoke() == true) return false;
        if (!_hud.ToggleTacticalMap()) return false;
        GetViewport().SetInputAsHandled();
        return true;
    }
}
