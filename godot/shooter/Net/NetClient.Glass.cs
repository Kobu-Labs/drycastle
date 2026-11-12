using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Client side of glass panes (ADR 0007, docs/glass.md). The server sends a pane's hit points when they change and, on join, every pane
/// that is not whole; this keeps them (<see cref="GlassReplica"/>) and shows them (<see cref="GlassFx"/>): a crack by HP fraction, the
/// shattered look at zero, and the fragments flying out at the moment a pane shatters in view. A building's pane is placed from the map's
/// windows.json, a vehicle's from vehicles.json "glass" under the vehicle's node. A vehicle that is not drawn yet gets its panes when it is.
/// </summary>
public partial class NetClient
{
    /// <summary>glass.json; the defaults when the host sets none.</summary>
    public GlassConfig GlassData = new();

    /// <summary>Opacity of a shattered pane's opening: a building has nothing behind its pane, a vehicle's cabin is only darkened.</summary>
    const float BuildingHole = 0.92f, VehicleHole = 0.6f;

    readonly GlassReplica _glass = new();
    readonly Dictionary<GlassPaneKey, MeshInstance3D> _glassOverlays = new();
    List<MapGlassPane>? _mapPanes;

    /// <summary>The building panes of the map, in wire order (as the server's). A broken windows.json is logged and counts as none.</summary>
    List<MapGlassPane> MapPanes
    {
        get
        {
            if (_mapPanes != null) return _mapPanes;
            try { _mapPanes = MapGlass.Parse(Map?.WindowsJson ?? ""); }
            catch (DataFileException e) { GD.PrintErr($"client: {e.Message}; building glass is not shown"); _mapPanes = new(); }
            return _mapPanes;
        }
    }

    void OnGlassPane(GlassPaneState s)
    {
        var before = _glass.Set(s);
        ShowGlass(s, animate: s.Shattered && before != 0);
    }

    void OnGlassSync(GlassSyncMsg m)
    {
        foreach (var key in _glass.Reset(m.Scope)) DropGlassOverlay(key);
        foreach (var s in m.Panes) { _glass.Set(s); ShowGlass(s, animate: false); }
    }

    /// <summary>The panes of a vehicle that has just been drawn for the first time (their state may have arrived before it).</summary>
    void ShowGlassOf(ushort vehicle)
    {
        foreach (var s in _glass.Of(vehicle)) ShowGlass(s, animate: false);
    }

    void ShowGlass(GlassPaneState s, bool animate)
    {
        if (!ShotFx.Enabled) return;
        if (s.Hp == GlassRules.Whole) { DropGlassOverlay(s.Key); return; }
        if (!GlassPlace(s.Key, out var parent, out var rect, out var hole)) return;
        if (!_glassOverlays.TryGetValue(s.Key, out var overlay) || !GodotObject.IsInstanceValid(overlay))
            _glassOverlays[s.Key] = overlay = GlassFx.MakeOverlay(parent, rect, s.Key, hole);
        GlassFx.SetOverlay(overlay, GlassRules.CrackStage(s.Fraction, GlassData.CrackStages), GlassRules.MaxCrackStage(GlassData.CrackStages), s.Shattered);
        if (animate && parent.IsVisibleInTree()) GlassFx.Shatter(Owned, parent.GlobalTransform, rect, GlassData, s.Key);
    }

    /// <summary>Where a pane is drawn: the node its rectangle is in and the rectangle. False for a pane this client cannot place (an unknown index, a vehicle not drawn yet).</summary>
    bool GlassPlace(GlassPaneKey key, out Node3D parent, out GlassPaneRect rect, out float hole)
    {
        parent = null!; rect = default; hole = BuildingHole;
        if (key.Owner == GlassPaneKey.Map)
        {
            var panes = MapPanes;
            if (key.Index >= panes.Count) return false;
            parent = Owned; rect = panes[key.Index].Rect;
            return true;
        }
        if (Vehicles == null || !_cars.TryGetValue(key.Owner, out var v) || !GodotObject.IsInstanceValid(v.Node)) return false;
        var rects = Vehicles.Vehicles[v.Kind].GlassRects;
        if (key.Index >= rects.Count) return false;
        parent = v.Node; rect = rects[key.Index]; hole = VehicleHole;
        return true;
    }

    void DropGlassOverlay(GlassPaneKey key)
    {
        if (_glassOverlays.Remove(key, out var overlay) && GodotObject.IsInstanceValid(overlay)) overlay.QueueFree();
    }

    /// <summary>Forgets the glass state when the client leaves (the overlays go with the nodes they are under).</summary>
    void GlassTeardown()
    {
        _glass.Reset(GlassCodec.Everything);
        _glassOverlays.Clear();
    }
}
