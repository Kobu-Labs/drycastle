using Godot;
using Drycastle.Shooter.Corpses;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Sandbox;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The HUD's screens a key opens: the Equipment screen (F, #268), the tactical map (M), the Hunter's Log (J, #336) and the Mineral Log (N). Their keys (not while a menu, the chat box or the corpse chooser
/// is up, <see cref="OverlayArbiter"/>), Esc through the Close methods (the shell then does not open Pause), and who owns the mouse meanwhile (<see cref="WantsFreeMouse"/>: the shell asks it every frame).
/// </summary>
public sealed class ScreensClient
{
    readonly ClientContext _ctx;
    readonly ChatClient _chat;
    readonly SandboxClient _sandbox;
    readonly CorpseChooser _chooser;
    bool _mapCursor;

    public ScreensClient(ClientContext ctx, ChatClient chat, SandboxClient sandbox, CorpseChooser chooser)
    {
        _ctx = ctx;
        _chat = chat;
        _sandbox = sandbox;
        _chooser = chooser;
    }

    public bool EquipmentOpen => _ctx.Hud?.EquipmentOpen ?? false;
    public bool TacticalMapOpen => _ctx.Hud?.TacticalMapOpen ?? false;
    public bool HuntersLogOpen => _ctx.Hud?.HuntersLogOpen ?? false;
    public bool MineralLogOpen => _ctx.Hud?.MineralLogOpen ?? false;

    /// <summary>Each Close is for Esc: true when the screen was open. The Hunter's Log goes up one level of the log first.</summary>
    public bool CloseEquipment() => _ctx.Hud?.CloseEquipment() ?? false;
    public bool CloseTacticalMap() => _ctx.Hud?.CloseTacticalMap() ?? false;
    public bool CloseHuntersLog() => _ctx.Hud?.BackHuntersLog() ?? false;
    public bool CloseMineralLog() => _ctx.Hud?.BackMineralLog() ?? false;

    /// <summary>The pause menu's entries.</summary>
    public void OpenHuntersLog() => _ctx.Hud?.OpenHuntersLog();
    public void OpenMineralLog() => _ctx.Hud?.OpenMineralLog();

    /// <summary>Who wants the cursor now.</summary>
    public bool WantsFreeMouse => Owners.WantsFreeMouse;
    MouseOwners Owners => new(_ctx.Dead, _chat.IsOpen, EquipmentOpen, TacticalMapOpen, _chooser.HasItems, HuntersLogOpen, MineralLogOpen);

    /// <summary>The map key, then the Equipment, Hunter's Log and Mineral Log keys. True when one was used.</summary>
    public bool HandleInput(InputEvent e) => HandleMapKey(e) || HandleEquipmentKey(e) || HandleHuntersLogKey(e) || HandleMineralLogKey(e);

    /// <summary>The map key toggles the map, but not while a menu is open or the chat box is. True when the key was used.</summary>
    bool HandleMapKey(InputEvent e)
    {
        if (_ctx.Hud is not { } hud || _ctx.Bot) return false;
        if (e.IsEcho() || !e.IsActionPressed("map")) return false;
        if (!hud.ToggleTacticalMap(_ctx.MenuIsOpen)) return false; // not under a menu: the arbiter says
        _ctx.Root.GetViewport().SetInputAsHandled();
        return true;
    }

    /// <summary>The equipment key (rebindable "inventory") toggles the screen, but not while a menu, the chat box or the corpse chooser is up.</summary>
    bool HandleEquipmentKey(InputEvent e)
    {
        if (_ctx.Hud is not { } hud || _ctx.Bot || e.IsEcho() || !e.IsActionPressed("inventory")) return false;
        if (!hud.ToggleEquipment(_ctx.MenuIsOpen, _chooser.IsOpen)) return false;
        _ctx.Root.GetViewport().SetInputAsHandled();
        return true;
    }

    bool HandleHuntersLogKey(InputEvent e)
    {
        if (_ctx.Hud is not { } hud || _ctx.Bot || e.IsEcho() || !e.IsActionPressed("hunters_log")) return false;
        if (!hud.ToggleHuntersLog(_ctx.MenuIsOpen, _chooser.IsOpen)) return false;
        _ctx.Root.GetViewport().SetInputAsHandled();
        return true;
    }

    bool HandleMineralLogKey(InputEvent e)
    {
        if (_ctx.Hud is not { } hud || _ctx.Bot || e.IsEcho() || !e.IsActionPressed("mineral_log")) return false;
        if (!hud.ToggleMineralLog(_ctx.MenuIsOpen, _chooser.IsOpen)) return false;
        _ctx.Root.GetViewport().SetInputAsHandled();
        return true;
    }

    /// <summary>
    /// Per frame. Sandbox: the open map frees the mouse (a cursor to pick the teleport point with; looking and firing stop meanwhile) and takes it
    /// back when it closes, unless a menu has opened in between and owns the mouse now.
    /// </summary>
    public void UpdateMapCursor()
    {
        var want = _ctx.Hud != null && _ctx.Hud.TacticalMapOpen && _sandbox.Granted;
        if (want == _mapCursor) return;
        _mapCursor = want;
        if (want) Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (Owners.CapturesWhenMapCloses(_ctx.MenuIsOpen)) Input.MouseMode = Input.MouseModeEnum.Captured; // a menu, the respawn screen or the chat box keep the cursor (the shell owns the mode then)
    }

    /// <summary>Sandbox: a middle click on the sheet (the map's own control gets it) teleports there; the map closes so the player sees where they landed.</summary>
    public void OnMapTeleport(Vector2 g)
    {
        if (!_sandbox.Granted) return;
        _ctx.Send(EventKind.SandboxTeleportTo, SandboxEvents.EncodeTeleportTo(g.X, g.Y));
        _ctx.Hud?.CloseTacticalMap();
    }

    /// <summary>
    /// Per frame, after the HUD is fed: an open screen frees the legs from the mouse (the player moves while it is up), and a menu or the corpse chooser takes the screen from the
    /// Equipment screen, a menu from the map and the logs.
    /// </summary>
    public void Update()
    {
        var hud = _ctx.Hud;
        if (_ctx.Me != null) _ctx.Me.MoveWithFreeMouse = EquipmentOpen || TacticalMapOpen || HuntersLogOpen || MineralLogOpen;
        if (hud is not ({ EquipmentOpen: true } or { TacticalMapOpen: true } or { HuntersLogOpen: true } or { MineralLogOpen: true })) return;
        var overlays = hud.Overlays(_ctx.MenuIsOpen, _chooser.IsOpen);
        if (hud.EquipmentOpen && OverlayArbiter.YieldsToMenu(Overlay.Equipment, overlays)) hud.CloseEquipment();
        if (hud.TacticalMapOpen && OverlayArbiter.YieldsToMenu(Overlay.TacticalMap, overlays)) hud.CloseTacticalMap();
        if (hud.HuntersLogOpen && OverlayArbiter.YieldsToMenu(Overlay.HuntersLog, overlays)) hud.CloseHuntersLog();
        if (hud.MineralLogOpen && OverlayArbiter.YieldsToMenu(Overlay.MineralLog, overlays)) hud.CloseMineralLog();
    }
}
