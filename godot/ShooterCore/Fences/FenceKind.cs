namespace Drycastle.ShooterCore.Fences;

/// <summary>How a fence section breaks (docs/fence-destruction.md F1): a thin fence by pieces (plain, picket, wire), a fence wall by the cells of its voxel grid.</summary>
public enum FenceKind : byte { Plain, Picket, Wire, Wall }
