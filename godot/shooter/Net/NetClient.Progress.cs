using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The XP thresholds from levels.json (<see cref="XpCurve"/>, set by the host). The in-match HUD no longer shows level or XP.
/// </summary>
public partial class NetClient
{
    /// <summary>The XP curve (levels.json), so the XP bar knows where the current level starts and ends. Null hides the bar's span.</summary>
    public LevelCurve? XpCurve;
}
