using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Churches;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Holy Mass on the client: the state the server broadcasts (phase, chant, the pastor's kills) drives the chant sound and the pastor's and monks' views;
/// the player's own seat state (a free seat in reach, or seated) drives the prompt and stops the body from being predicted while seated; the bells and
/// the blessing are a toast each. The server owns all of it, the client only shows it.
/// </summary>
public sealed class MassClient
{
    readonly ClientContext _ctx;
    readonly HolyMassFile _data;
    readonly Action _dropPrediction;
    Change<byte> _phase; // the MassPhase last heard, as a byte: MassState also goes out for every kill and chant change
    MassSeatState _seat;
    ChurchChant? _chant;

    /// <param name="dropPrediction">Called when the server seats the player: the inputs waiting for an ack and a pending reconcile are void.</param>
    public MassClient(ClientContext ctx, HolyMassFile data, Action dropPrediction)
    {
        _ctx = ctx;
        _data = data;
        _dropPrediction = dropPrediction;
    }

    /// <summary>The last Mass state the server sent (the chant's place, the pastor's kills).</summary>
    public MassStateMsg State { get; private set; }

    /// <summary>Seated at the Mass: the server holds the body, so it is not predicted and not moved by input.</summary>
    public bool Seated => _seat == MassSeatState.Seated;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.MassState when MassCodec.DecodeState(payload) is { } m:
                State = m;
                if (_phase.Update((byte)m.Phase) && m.Phase == MassPhase.Gather) ChurchDecor.Current?.ResetBlood(); // a new Mass: the last one's stains are gone (once, not on every state while it gathers)
                var chant = Chant();
                chant?.SetChant(m.Chanting, m.Phase == MassPhase.Mass, new Vector3(m.ChantAt.X, m.ChantAt.Y, m.ChantAt.Z));
                if (m.Bells)
                {
                    chant?.RingBells();
                    var text = Strings.T("mass.bells");
                    UiShell.Instance?.Toast(text, ToastKind.Warn, null, 6.0);
                    _ctx.Hud?.AddChatLine(new ChatLine(ChatLineKind.System, "", text));
                }
                break;
            case EventKind.MassSeat when MassCodec.DecodeSeat(payload) is { } s:
                _seat = s.State;
                if (s.State == MassSeatState.Seated && _ctx.Me != null) { _ctx.Me.Yaw = s.Yaw; _ctx.Me.Pitch = 0; _dropPrediction(); }
                break;
            case EventKind.MassBlessing when MassCodec.DecodeBlessing(payload) is { } yaw:
                _seat = MassSeatState.None;
                if (_ctx.Me != null) { _ctx.Me.Yaw = yaw; _ctx.Me.Pitch = 0; }
                UiShell.Instance?.Toast(Strings.T("mass.blessed"), ToastKind.Info, null, 5.0);
                break;
        }
    }

    ChurchChant? Chant()
    {
        if (!Sfx.Enabled) return null;
        if (_chant == null || !GodotObject.IsInstanceValid(_chant)) { _chant = new ChurchChant(_data.Mass); _ctx.Owned.AddChild(_chant); }
        return _chant;
    }

    /// <summary>The seat prompt: sit down at a free seat in reach, stand up while seated.</summary>
    public string? Prompt() => _seat switch
    {
        MassSeatState.CanSit => Strings.F("mass.sit", InputBindings.Name("interact")),
        MassSeatState.Seated => Strings.F("mass.stand", InputBindings.Name("interact"), InputBindings.Name("jump")),
        _ => null,
    };
}
