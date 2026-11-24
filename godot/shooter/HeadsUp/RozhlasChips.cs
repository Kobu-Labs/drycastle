using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The rozhlas set holder's cues (docs/events/README.md, "The set"): a control hint chip while the local player holds the set ("You hold the radio set. Hold V to talk", bottom centre above
/// the spawn-protection chip, the key follows the rebinding) and an ON AIR chip while they talk (top centre under the timer). Both are reserved space that fades in and out, so nothing
/// shifts; the word carries the state, never colour alone; theme variations only. Nothing else about the set is shown (no timer, no meter, no local monitoring). Written per frame as two
/// fades and, when the text changes, one string; nothing is built or allocated per frame.
/// </summary>
internal sealed class RozhlasChips
{
    const float HintWidth = 520, OnAirWidth = 224, ChipHeight = 48;

    readonly PanelContainer _hint, _onAir;
    readonly Label _hintText;
    bool _hintStale = true;

    public RozhlasChips(Control root)
    {
        _hint = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin - ChipHeight - ShooterTheme.Space, HintWidth, ChipHeight);
        _hintText = HudUi.L("", "HudAccent", HorizontalAlignment.Center);
        _hint.AddChild(_hintText);
        _hint.Modulate = new Color(1, 1, 1, 0);
        root.AddChild(_hint);

        _onAir = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 0, 0, ShooterTheme.Margin + 64 + ShooterTheme.Space, OnAirWidth, ChipHeight);
        _onAir.AddChild(HudUi.L(Strings.T("hud.rozhlas.onair"), "HudDanger", HorizontalAlignment.Center));
        _onAir.Modulate = new Color(1, 1, 1, 0);
        root.AddChild(_onAir);
    }

    /// <summary>The talk key may have been rebound: the hint is written again the next time it shows.</summary>
    public void MarkStale() => _hintStale = true;

    /// <summary>Shows the hold-the-set hint while <paramref name="holder"/>, and ON AIR while <paramref name="talking"/> (the player holds the set and the talk key).</summary>
    public void Set(bool holder, bool talking)
    {
        if (holder && _hintStale)
        {
            _hintStale = false;
            HudUi.Set(_hintText, Strings.F("hud.rozhlas.hint", InputBindings.Name("rozhlas_talk")));
        }
        HudUi.Fade(_hint, holder ? 1 : 0);
        HudUi.Fade(_onAir, talking ? 1 : 0);
    }
}
