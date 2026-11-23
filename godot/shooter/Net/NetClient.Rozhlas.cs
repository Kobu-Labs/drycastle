using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The village loudspeaker on the client: the server's phase (<see cref="EventKind.RozhlasState"/>) goes to <see cref="Rozhlas.Apply"/>, which plays the broadcast from the offset it names.
/// And the set (<see cref="EventKind.RozhlasSet"/>): who is on the mic. When that is this player the mic opens (<see cref="RozhlasMicInput"/>) and, while they hold the talk action
/// (<c>rozhlas_talk</c>), their voice goes upstream (<see cref="Wire.TRozhlasMic"/>) and the HUD says ON AIR. While the mic is hijacked and the set is free, a player at the dictation
/// table sees the take prompt and, holding the interact key, the ring (<see cref="RozhlasSet.TakeHoldSeconds"/>); the server counts the same hold and decides.
/// </summary>
public partial class NetClient
{
    /// <summary>The player who holds the rozhlas set (0: nobody).</summary>
    public ushort RozhlasHolder { get; private set; }

    /// <summary>This player holds the set.</summary>
    public bool HoldsRozhlasSet => RozhlasHolder != RozhlasSet.None && RozhlasHolder == _myId;

    RozhlasMicInput? _mic;
    bool _talking;
    /// <summary>The loudspeaker's phase as the server last said it, and the dictation table (world metres) once a hijack named it.</summary>
    RozhlasPhase _rozhlasPhase;
    System.Numerics.Vector3? _rozhlasTable;
    /// <summary>The local picture of the hold the server counts: for the prompt and the ring only.</summary>
    InteractHold _setHold;

    void OnRozhlasEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.RozhlasSet && RozhlasSetCodec.Decode(payload) is { } set) { RozhlasHolder = set.Holder; _rozhlasTable = set.Table ?? _rozhlasTable; return; }
        if (kind != EventKind.RozhlasState || RozhlasCodec.Decode(payload) is not { } m) return;
        _rozhlasPhase = m.Phase;
        Rozhlas.Apply(m);
        GD.Print($"client {PlayerName}: rozhlas {m.Phase}" + (m.Phase == RozhlasPhase.OnAir ? $", broadcast {m.Variant} from {m.OffsetS:0.#} s" : ""));
    }

    /// <summary>A new connection: nobody holds the set until the server says so, and the mic is shut.</summary>
    void RozhlasSetReset()
    {
        RozhlasHolder = RozhlasSet.None;
        _rozhlasPhase = RozhlasPhase.Idle;
        _rozhlasTable = null;
        _setHold.Reset();
        _talking = false;
        _mic?.Close();
    }

    /// <summary>Once a frame: opens the mic while this player holds the set, shuts it otherwise, sends the voice while they talk, and tells the HUD.</summary>
    void RozhlasMicFrame()
    {
        var holder = HoldsRozhlasSet;
        _talking = RozhlasMicRules.Transmitting(holder, Input.IsActionPressed("rozhlas_talk"), Dead, _driving || _myState == EntityState.InVehicle, Input.MouseMode == Input.MouseModeEnum.Captured);
        if (!Bot && Sfx.Enabled)
        {
            if (_mic == null && holder) { _mic = new RozhlasMicInput(pkt => { if (_peer != null) Tx(_peer, Wire.ChanRozhlasFeed, pkt, 0); }); AddChild(_mic); }
            if (_mic != null)
            {
                if (RozhlasMicRules.DeviceOpen(holder)) _mic.Open(); else if (_mic.IsOpen) _mic.Close();
                if (_mic.IsOpen) _mic.Pump(_talking);
            }
        }
        BotMicTick(holder);
        _hud?.SetRozhlas(holder, _talking);
    }

    /// <summary>The set can be taken from where this player stands (the server's own rule, on the client's picture of it).</summary>
    bool RozhlasSetInReach => _me is { } me && !Dead && RozhlasSet.CanTakeHere(_rozhlasPhase == RozhlasPhase.Hijacked, RozhlasHolder != RozhlasSet.None, _rozhlasTable,
        new System.Numerics.Vector3(me.GlobalPosition.X, me.GlobalPosition.Y, me.GlobalPosition.Z), _driving || _myState == EntityState.InVehicle);

    /// <summary>The take prompt at the table while the key is not held; the ring carries the hold.</summary>
    string? RozhlasSetPrompt() => RozhlasSetInReach && _setHold.Held <= 0 ? Strings.F("hud.rozhlas.take", InputBindings.Name("interact")) : null;

    /// <summary>The input tick: follows the hold of the interact key at the table. Nothing is sent: the server sees the same key in the inputs.</summary>
    void TickRozhlasSetHold(bool use, double dt) => _setHold.Tick(use, RozhlasSetInReach, dt, RozhlasSet.TakeHoldSeconds);

    /// <summary>How full the hold ring is for the set (0 when not holding at the table).</summary>
    float RozhlasSetHoldFraction => _setHold.Held > 0 && RozhlasSetInReach ? (float)Math.Min(1.0, _setHold.Held / RozhlasSet.TakeHoldSeconds) : 0f;
}
