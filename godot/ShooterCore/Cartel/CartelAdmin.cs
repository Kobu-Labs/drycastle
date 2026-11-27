using System.Globalization;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The words of the admin's tests on their own running Job (<c>/cartel end win|fail</c>, <c>/cartel time &lt;seconds&gt;</c>; docs/cartel.md "Testing"), read by the server's chat command:
/// a Job's late stages without playing it through.
/// </summary>
public static class CartelAdmin
{
    /// <summary>Most seconds <c>/cartel time</c> puts on a timer (an hour: past any kind's).</summary>
    public const int MaxTimerSeconds = 3600;

    /// <summary>"win" (true: it succeeds and pays) or "fail" (false); null for anything else.</summary>
    public static bool? ParseEnd(string value) => value.Trim().ToLowerInvariant() switch
    {
        "win" => true,
        "fail" => false,
        _ => null,
    };

    /// <summary>Whole seconds from 1 to <see cref="MaxTimerSeconds"/>; null for anything else.</summary>
    public static int? ParseSeconds(string value) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 1 and <= MaxTimerSeconds ? s : null;
}
