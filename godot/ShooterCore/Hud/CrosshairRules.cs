namespace Drycastle.ShooterCore;

/// <summary>
/// Whether the HUD crosshair shows. Five things can hide it (the setting, a panel in the middle of the screen, the scope, the binoculars, a pinning pounce); the
/// view keeps one flag for each and asks here after any of them changes, so no writer can show it over another's cover.
/// </summary>
public static class CrosshairRules
{
    public static bool Visible(bool show, bool covered, bool scoped, bool binoculars, bool pounce) =>
        show && !covered && !scoped && !binoculars && !pounce;
}
