using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Player statuses on the client (Holy Mass reward): what the server last said about everyone (<see cref="EventKind.StatusState"/>: the
/// blessing and curse mask, burning), drawn as flames on a burning remote player and, for the local player, the HUD chips
/// (<see cref="Hud.SetStatus"/>). A blessing that saved someone (<see cref="EventKind.BlessingSaved"/>) bursts gold on their body, and flashes
/// the owner's screen. The server decides everything.
/// </summary>
public sealed class StatusClient
{
    readonly ClientContext _ctx;
    readonly PlantationFile _plantation;
    /// <summary>The status mask and burning flag of each player the server told about (absent: none).</summary>
    readonly Dictionary<ushort, (byte Mask, bool Burning)> _statusOf = new();
    /// <summary>The flame emitter on each burning remote player's body.</summary>
    readonly Dictionary<ushort, CpuParticles3D> _flames = new();

    /// <param name="plantation">plantation.json: what the high's cigarette and eyes look like.</param>
    public StatusClient(ClientContext ctx, PlantationFile plantation)
    {
        _ctx = ctx;
        _plantation = plantation;
    }

    /// <summary>The player has this status according to the last message.</summary>
    public bool Has(ushort player, StatusKind kind) => _statusOf.TryGetValue(player, out var s) && (s.Mask & (1 << (int)kind)) != 0;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.StatusState when StatusCodec.TryDecodeState(payload, out var player, out var mask, out var burning):
                var before = _statusOf.TryGetValue(player, out var old) ? old.Mask : (byte)0;
                if (mask == 0 && !burning) _statusOf.Remove(player); else _statusOf[player] = (mask, burning);
                SyncFx(player);
                if (player == _ctx.MyId) OwnChanged(before, mask, burning);
                break;
            case EventKind.BlessingSaved when StatusCodec.TryDecodeSaved(payload, out var saved):
                if (_ctx.PositionOf(saved) is { } at) StatusFx.GoldBurst(_ctx.Owned, at);
                if (saved == _ctx.MyId)
                {
                    _ctx.Hud?.FlashBlessing();
                    UiShell.Instance?.Toast(Strings.T("toast.status.saved"));
                }
                break;
        }
    }

    void OwnChanged(byte before, byte mask, bool burning)
    {
        _ctx.Hud?.SetStatus(mask, burning);
        var gained = (byte)(mask & ~before);
        if ((gained & (1 << (int)StatusKind.Blessing)) != 0) UiShell.Instance?.Toast(Strings.T("toast.status.blessed"));
        if ((gained & (1 << (int)StatusKind.Curse)) != 0) UiShell.Instance?.Toast(Strings.T("toast.status.cursed"), ToastKind.Warn);
    }

    /// <summary>Puts the flames and the high's cigarette and eyes on (or takes them off) a remote player's body to match what the server said. Also called for a body made after the message.</summary>
    public void SyncFx(ushort player)
    {
        if (player == _ctx.MyId || !_ctx.Remotes.TryGetValue(player, out var r)) return;
        r.View?.SetHigh(Has(player, StatusKind.High) ? _plantation : null); // the cigarette and the red eyes (CharacterView.High.cs)
        var burning = _statusOf.TryGetValue(player, out var s) && s.Burning;
        if (_flames.TryGetValue(player, out var flames) && !GodotObject.IsInstanceValid(flames)) { _flames.Remove(player); flames = null; }
        if (flames == null && burning)
        {
            flames = StatusFx.MakeFlames(r.Node);
            if (flames != null) _flames[player] = flames;
        }
        StatusFx.SetBurning(flames, burning);
    }

    /// <summary>A player left: forget their statuses (the flames go with their body).</summary>
    public void Forget(ushort player)
    {
        _statusOf.Remove(player);
        _flames.Remove(player);
    }
}
