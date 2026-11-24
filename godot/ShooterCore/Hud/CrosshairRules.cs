namespace Drycastle.ShooterCore.Hud;

/// <summary>
/// Whether the HUD crosshair shows. Six things can hide it (the setting, a panel in the middle of the screen, the scope, the binoculars, a pinning pounce, a seat
/// that cannot fire: #373); the view keeps one flag for each and asks here after any of them changes, so no writer can show it over another's cover.
/// </summary>
public static class CrosshairRules
{
    public static bool Visible(bool show, bool covered, bool scoped, bool binoculars, bool pounce, bool unarmedSeat = false) =>
        show && !covered && !scoped && !binoculars && !pounce && !unarmedSeat;
}
