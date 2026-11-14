using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Holy Mass on the client: the state the server broadcasts (phase, chant, the pastor's kills) drives the chant sound and the pastor's and monks' views;
/// the player's own seat state (a free seat in reach, or seated) drives the prompt and stops the body from being predicted while seated; the bells and
/// the blessing are a toast each. The server owns all of it, the client only shows it.
/// </summary>
public partial class NetClient
{
    MassStateMsg _massNow;
    MassSeatState _massSeat;
    ChurchChant? _chant;

    /// <summary>Seated at the Mass: the server holds the body, so it is not predicted and not moved by input.</summary>
    bool _massSeated => _massSeat == MassSeatState.Seated;

    void OnMassEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.MassState when MassCodec.DecodeState(payload) is { } m:
                _massNow = m;
                if (m.Phase == MassPhase.Gather) ChurchDecor.Current?.ResetBlood(); // a new Mass: the last one's stains are gone
                var chant = Chant();
                chant?.SetChant(m.Chanting, m.Phase == MassPhase.Mass, new Vector3(m.ChantAt.X, m.ChantAt.Y, m.ChantAt.Z));
                if (m.Bells)
                {
                    chant?.RingBells();
                    var text = Strings.T("mass.bells");
                    UiShell.Instance?.Toast(text, ToastKind.Warn, null, 6.0);
                    _hud?.AddChatLine(new ChatLine(ChatLineKind.System, "", text));
                }
                break;
            case EventKind.MassSeat when MassCodec.DecodeSeat(payload) is { } s:
                _massSeat = s.State;
                if (s.State == MassSeatState.Seated && _me != null) { _me.Yaw = s.Yaw; _me.Pitch = 0; _pending.Clear(); _reconcile = null; }
                break;
            case EventKind.MassBlessing when MassCodec.DecodeBlessing(payload) is { } yaw:
                _massSeat = MassSeatState.None;
                if (_me != null) { _me.Yaw = yaw; _me.Pitch = 0; }
                UiShell.Instance?.Toast(Strings.T("mass.blessed"), ToastKind.Info, null, 5.0);
                break;
        }
    }

    ChurchChant? Chant()
    {
        if (!Sfx.Enabled) return null;
        if (_chant == null || !GodotObject.IsInstanceValid(_chant)) { _chant = new ChurchChant(HolyMassData.Mass); Owned.AddChild(_chant); }
        return _chant;
    }

    /// <summary>The seat prompt: sit down at a free seat in reach, stand up while seated.</summary>
    string? MassPrompt() => _massSeat switch
    {
        MassSeatState.CanSit => Strings.F("mass.sit", InputBindings.Name("interact")),
        MassSeatState.Seated => Strings.F("mass.stand", InputBindings.Name("interact"), InputBindings.Name("jump")),
        _ => null,
    };
}
