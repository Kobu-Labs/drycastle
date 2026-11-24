using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// The village loudspeaker on the client: the server's phase (<see cref="EventKind.RozhlasState"/>) goes to <see cref="Rozhlas.Apply"/>, which plays the broadcast from the offset it names.
/// And the set (<see cref="EventKind.RozhlasSet"/>): who is on the mic. When that is this player the mic opens (<see cref="RozhlasMicInput"/>) and, while they hold the talk action
/// (<c>rozhlas_talk</c>), their voice goes upstream (<see cref="Wire.TRozhlasMic"/>) and the HUD says ON AIR. While the mic is hijacked and the set is free, a player at the dictation
/// table sees the take prompt and, holding the interact key, the ring (<see cref="RozhlasSet.TakeHoldSeconds"/>); the server counts the same hold and decides.
/// </summary>
public sealed class RozhlasClient
{
    readonly ClientContext _ctx;
    readonly Action<byte[]> _sendMic;
    RozhlasMicInput? _mic;
    bool _talking;
    /// <summary>The player who holds the rozhlas set (0: nobody).</summary>
    ushort _holder;
    /// <summary>The loudspeaker's phase as the server last said it, and the dictation table (world metres) once a hijack named it.</summary>
    RozhlasPhase _phase;
    System.Numerics.Vector3? _table;
    /// <summary>The local picture of the hold the server counts: for the prompt and the ring only.</summary>
    InteractHold _hold;

    /// <param name="sendMic">Sends one packet of the player's voice upstream.</param>
    public RozhlasClient(ClientContext ctx, Action<byte[]> sendMic)
    {
        _ctx = ctx;
        _sendMic = sendMic;
    }

    /// <summary>This player holds the set.</summary>
    public bool HoldsSet => _holder != RozhlasSet.None && _holder == _ctx.MyId;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.RozhlasSet && RozhlasSetCodec.Decode(payload) is { } set) { _holder = set.Holder; _table = set.Table ?? _table; return; }
        if (kind != EventKind.RozhlasState || RozhlasCodec.Decode(payload) is not { } m) return;
        _phase = m.Phase;
        Rozhlas.Apply(m);
        GD.Print($"client {_ctx.PlayerName}: rozhlas {m.Phase}" + (m.Phase == RozhlasPhase.OnAir ? $", broadcast {m.Variant} from {m.OffsetS:0.#} s" : ""));
    }

    /// <summary>A new connection: nobody holds the set until the server says so, and the mic is shut.</summary>
    public void Reset()
    {
        _holder = RozhlasSet.None;
        _phase = RozhlasPhase.Idle;
        _table = null;
        _hold.Reset();
        _talking = false;
        _mic?.Close();
    }

    /// <summary>Once a frame: opens the mic while this player holds the set, shuts it otherwise, sends the voice while they talk, and tells the HUD.</summary>
    public void MicFrame()
    {
        var holder = HoldsSet;
        _talking = RozhlasMicRules.Transmitting(holder, Input.IsActionPressed("rozhlas_talk"), _ctx.Dead, _ctx.InVehicle(), Input.MouseMode == Input.MouseModeEnum.Captured);
        if (!_ctx.Bot && Sfx.Enabled)
        {
            if (_mic == null && holder) { _mic = new RozhlasMicInput(_sendMic); _ctx.Root.AddChild(_mic); }
            if (_mic != null)
            {
                if (RozhlasMicRules.DeviceOpen(holder)) _mic.Open(); else if (_mic.IsOpen) _mic.Close();
                if (_mic.IsOpen) _mic.Pump(_talking);
            }
        }
        _ctx.Hud?.SetRozhlas(holder, _talking);
    }

    /// <summary>The set can be taken from where this player stands (the server's own rule, on the client's picture of it).</summary>
    bool InReach => _ctx.Me is { } me && !_ctx.Dead && RozhlasSet.CanTakeHere(_phase == RozhlasPhase.Hijacked, _holder != RozhlasSet.None, _table,
        new System.Numerics.Vector3(me.GlobalPosition.X, me.GlobalPosition.Y, me.GlobalPosition.Z), _ctx.InVehicle());

    /// <summary>The take prompt at the table while the key is not held; the ring carries the hold.</summary>
    public string? Prompt() => InReach && _hold.Held <= 0 ? Strings.F("hud.rozhlas.take", InputBindings.Name("interact")) : null;

    /// <summary>The input tick: follows the hold of the interact key at the table. Nothing is sent: the server sees the same key in the inputs.</summary>
    public void TickHold(bool use, double dt) => _hold.Tick(use, InReach, dt, RozhlasSet.TakeHoldSeconds);

    /// <summary>How full the hold ring is for the set (0 when not holding at the table).</summary>
    public float HoldFraction => _hold.Held > 0 && InReach ? (float)Math.Min(1.0, _hold.Held / RozhlasSet.TakeHoldSeconds) : 0f;
}
