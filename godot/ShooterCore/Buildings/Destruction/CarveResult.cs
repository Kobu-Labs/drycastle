namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>What one op did: the cells it carved, those that crumbled under load, the components that fell (each a cell list), and the state hash after Settle.</summary>
public sealed record CarveResult(int[] Carved, int[] Crumbled, IReadOnlyList<int[]> Fallen, uint Hash);
