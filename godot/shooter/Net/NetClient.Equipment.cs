using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The equipment key (F, rebindable "inventory"): toggles the HUD's Equipment screen (issue #268, <see cref="EquipmentScreen"/>). Esc closes it through
/// <see cref="CloseEquipment"/>. While it is open the shell leaves the mouse free (<see cref="EquipmentOpen"/>) and the legs keep working (<see cref="Player.MoveWithFreeMouse"/>).
/// </summary>
public partial class NetClient
{
    /// <summary>The Equipment screen is up: the mouse is released for it.</summary>
    public bool EquipmentOpen => _hud?.EquipmentOpen ?? false;
    /// <summary>The tactical map is open: the shell frees the mouse so a waypoint can be clicked.</summary>
    public bool TacticalMapOpen => _hud?.TacticalMapOpen ?? false;

    /// <summary>Closes the Equipment screen. True when it was open (the shell then does not open Pause for this Esc).</summary>
    public bool CloseEquipment() => _hud?.CloseEquipment() ?? false;

    /// <summary>The equipment key toggles the screen, but not while a menu, the chat box or the corpse chooser is up (the HUD checks the chat). True when the key was used.</summary>
    bool HandleEquipmentInput(InputEvent e)
    {
        if (_hud == null || Bot || e.IsEcho() || !e.IsActionPressed("inventory")) return false;
        if (MenuOpen?.Invoke() == true || ChooserOpen) return false;
        if (!_hud.ToggleEquipment()) return false;
        GetViewport().SetInputAsHandled();
        return true;
    }
}
