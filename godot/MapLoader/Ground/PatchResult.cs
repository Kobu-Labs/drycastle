namespace Drycastle.MapLoader.Ground;

/// <summary>Result of <see cref="MapLoader.ApplyPatches"/>: the patch IDs whose baked nodes were hidden and the new nodes added.</summary>
public sealed record PatchResult(IReadOnlyList<string> Replaced, int Nodes);
