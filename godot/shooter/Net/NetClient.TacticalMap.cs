using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The tactical map key (M, rebindable): toggles the HUD's map. Esc closes it through <see cref="CloseTacticalMap"/>.</summary>
public partial class NetClient
{
    /// <summary>Closes the tactical map. True when it was open (the shell then does not open Pause for this Esc).</summary>
    public bool CloseTacticalMap() => _hud?.CloseTacticalMap() ?? false;

    /// <summary>The map key toggles the map, but not while a menu is open or the chat box is (the HUD checks the chat). True when the key was used.</summary>
    bool _mapCursor;

    /// <summary>
    /// Per frame. Sandbox: the open map frees the mouse (a cursor to pick the teleport point with; looking and firing stop meanwhile) and takes it
    /// back when it closes, unless a menu has opened in between and owns the mouse now.
    /// </summary>
    void UpdateMapCursor()
    {
        var want = _hud != null && _hud.TacticalMapOpen && SandboxGranted;
        if (want == _mapCursor) return;
        _mapCursor = want;
        if (want) Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (MenuOpen?.Invoke() != true) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>Sandbox: a middle click on the sheet (the map's own control gets it) teleports there; the map closes so the player sees where they landed.</summary>
    void OnMapTeleport(Vector2 g)
    {
        if (!SandboxGranted) return;
        SendSandboxEvent(EventKind.SandboxTeleportTo, SandboxEvents.EncodeTeleportTo(g.X, g.Y));
        _hud?.CloseTacticalMap();
    }

    bool HandleMapInput(InputEvent e)
    {
        if (_hud == null || Bot) return false;
        if (e.IsEcho() || !e.IsActionPressed("map")) return false;
        if (MenuOpen?.Invoke() == true) return false;
        if (!_hud.ToggleTacticalMap()) return false;
        GetViewport().SetInputAsHandled();
        return true;
    }
}
