namespace Drycastle.ShooterCore;

/// <summary>Where one mesh of a pack tree tile is on its way to the screen (MapLoader.PackTrees.cs owns the meshes; the enum and spec are here so the catalog sees them).</summary>
public enum PackLayerState
{
    /// <summary>Not asked for, or freed: nothing is held.</summary>
    Idle,
    /// <summary>A worker thread is making its triangles.</summary>
    Building,
    /// <summary>Built; its surfaces go to the renderer a few per frame.</summary>
    Handing,
    /// <summary>All of it is in the mesh (or there was nothing to make): it may be drawn, and the cut may pass it.</summary>
    Ready,
}

public static class PackLayerMachine
{
    public static readonly StateMachineSpec<PackLayerState> Spec = StateMachineSpec<PackLayerState>.Define(
            "PackTile.Layer",
            "One mesh of a pack tree tile (its trees, its undergrowth, its far undergrowth, or the tree cards of a far tile): built on a worker, handed to the renderer a surface at a time, drawn, freed when far.",
            PackLayerState.Idle)
        .Allow(PackLayerState.Idle, PackLayerState.Building, PackLayerState.Ready)
        .Allow(PackLayerState.Building, PackLayerState.Handing, PackLayerState.Ready, PackLayerState.Idle)
        .Allow(PackLayerState.Handing, PackLayerState.Ready, PackLayerState.Idle)
        .Allow(PackLayerState.Ready, PackLayerState.Idle)
        .Note(PackLayerState.Idle, "Nothing held. Straight to Ready when the tile has nothing of this layer to make.")
        .Note(PackLayerState.Building, "A worker makes the triangles. Ready at once when it made none or failed (a warning); Idle when the tile is freed first (the result is dropped).")
        .Note(PackLayerState.Handing, "Surfaces are added to the mesh within budget_ms per frame; the mesh is not shown yet.")
        .Note(PackLayerState.Ready, "Shown inside its radius. The cut radius never passes a layer that is not Ready.")
        .Build();
}
