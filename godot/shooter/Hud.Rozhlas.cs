using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The rozhlas set holder's cues (docs/events/README.md, "The set"): a control hint chip while the local player holds the set ("You hold the radio set. Hold V to talk", bottom centre above
/// the spawn-protection chip, the key follows the rebinding) and an ON AIR chip while they talk (top centre under the timer). Both are reserved space that fades in and out, so nothing
/// shifts; the word carries the state, never colour alone; theme variations only. Nothing else about the set is shown (no timer, no meter, no local monitoring). Written per frame as two
/// fades and, when the text changes, one string; nothing is built or allocated per frame.
/// </summary>
public partial class Hud
{
    const float RozhlasHintWidth = 520, RozhlasOnAirWidth = 224, RozhlasChipHeight = 48;

    PanelContainer _rozhlasHint = null!, _rozhlasOnAir = null!;
    Label _rozhlasHintText = null!;
    bool _rozhlasHintStale = true;

    void BuildRozhlas()
    {
        _rozhlasHint = Place(P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin - RozhlasChipHeight - ShooterTheme.Space, RozhlasHintWidth, RozhlasChipHeight);
        _rozhlasHintText = L("", "HudAccent", HorizontalAlignment.Center);
        _rozhlasHint.AddChild(_rozhlasHintText);
        _rozhlasHint.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(_rozhlasHint);

        _rozhlasOnAir = Place(P("HudPanel"), 0.5f, 0, 0, ShooterTheme.Margin + 64 + ShooterTheme.Space, RozhlasOnAirWidth, RozhlasChipHeight);
        _rozhlasOnAir.AddChild(L(Strings.T("hud.rozhlas.onair"), "HudDanger", HorizontalAlignment.Center));
        _rozhlasOnAir.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(_rozhlasOnAir);
    }

    /// <summary>Shows the hold-the-set hint while <paramref name="holder"/>, and ON AIR while <paramref name="talking"/> (the player holds the set and the talk key).</summary>
    public void SetRozhlas(bool holder, bool talking)
    {
        if (holder && _rozhlasHintStale)
        {
            _rozhlasHintStale = false;
            Set(_rozhlasHintText, Strings.F("hud.rozhlas.hint", InputBindings.Name("rozhlas_talk")));
        }
        Fade(_rozhlasHint, holder ? 1 : 0);
        Fade(_rozhlasOnAir, talking ? 1 : 0);
    }
}
