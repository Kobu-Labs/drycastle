using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The Equipment screen in the HUD (issue #268, F): an overlay under the HUD root like the tactical map, above the panels but below the scoreboard.
/// The match owner attaches it once (<see cref="AttachEquipment"/>); the HUD closes it when death, the end of the match or a control session (a turret,
/// the attack missile) takes the screen, as it does the map, and the two never show together. Nothing about it pauses the game: the mouse mode is
/// the shell's (it asks <see cref="EquipmentOpen"/>), and Esc closes it through <see cref="CloseEquipment"/>.
/// </summary>
public partial class Hud
{
    EquipmentScreen? _equipment;

    public bool EquipmentOpen => _equipment?.IsOpen ?? false;

    /// <summary>Builds the screen for the local player's equipment (once per match).</summary>
    public void AttachEquipment(NetClient client)
    {
        if (_equipment != null) return;
        _equipment = new EquipmentScreen(client);
        _root.AddChild(_equipment);
        _root.MoveChild(_board, -1);
        _root.MoveChild(_xp, -1);
    }

    /// <summary>
    /// The equipment key: opens the screen, or closes it when open. Does nothing (false) while the chat box is open, on the respawn screen, after the match
    /// ends and in a missile or turret view.
    /// </summary>
    public bool ToggleEquipment()
    {
        if (_equipment == null || ChatOpen) return false;
        if (_equipment.IsOpen) { _equipment.Close(); return true; }
        if (MapBlocked) return false;
        _tacticalMap.Close();
        _equipment.Open();
        return true;
    }

    /// <summary>Closes the screen (Esc). True when it was open, so the Esc is used and Pause does not open.</summary>
    public bool CloseEquipment()
    {
        if (_equipment is not { IsOpen: true }) return false;
        _equipment.Close();
        Sfx.Ui("ui.back");
        return true;
    }

    /// <summary>Per frame: closes the screen when death, the end of the match or a control session takes the screen. One flag check while closed.</summary>
    void UpdateEquipment()
    {
        if (_equipment is { IsOpen: true } && MapBlocked) _equipment.Close();
    }
}
