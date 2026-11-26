namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>What a cell of a hollow house is made of (house_voxels.bin palette, docs/house-destruction.md Contract 1). Air is never solid.</summary>
public enum CellMaterial : byte { Air, Foundation, Floor, Wall, Partition, Ceiling, Roof }
