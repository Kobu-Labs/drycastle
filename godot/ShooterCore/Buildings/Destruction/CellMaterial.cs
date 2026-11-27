namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>What a cell of a hollow house or a fence wall is made of (house_voxels.bin palette, docs/house-destruction.md Contract 1; Brick, Stone, Concrete: the fence walls, docs/fence-destruction.md Contract 2). Air is never solid.</summary>
public enum CellMaterial : byte { Air, Foundation, Floor, Wall, Partition, Ceiling, Roof, Brick, Stone, Concrete }
