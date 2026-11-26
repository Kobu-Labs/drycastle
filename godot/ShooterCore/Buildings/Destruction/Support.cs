namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Structure (D6): detach, then load, repeat until stable. WP-B.</summary>
public static class Support
{
    /// <summary>Removes the cells that crumble under load and the components with no path to the foundation; returns them.</summary>
    public static (int[] Crumbled, IReadOnlyList<int[]> Fallen) Settle(HouseCells c, DestructionConfig cfg) => throw new NotImplementedException("WP-B: Support.Settle");
}
