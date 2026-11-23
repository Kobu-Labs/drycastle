using Godot;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Glass;

namespace Drycastle.Shooter;

/// <summary>
/// Client side of glass panes (ADR 0007, docs/glass.md). The server sends a pane's hit points when they change and, on join, every pane
/// that is not whole; this keeps them (<see cref="GlassReplica"/>) and shows them (<see cref="GlassFx"/>): a crack by HP fraction, the
/// shattered look at zero, and the fragments flying out at the moment a pane shatters in view. A building's pane is placed from the map's
/// windows.json, a vehicle's from vehicles.json "glass" under the vehicle's node. A vehicle that is not drawn yet gets its panes when it is.
/// A vehicle model that has the pane as its own node (<see cref="PaneNodeName"/>: the generated bus, flak truck and helicopter) has that node
/// hidden while the pane is shattered, so the original glass does not show through the opening; it is shown again when the pane is whole.
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
            if (MechanicGarage.Current is { } garage) MechanicGarageSite.AddPane(_mapPanes, garage.FloorY); // the mechanic garage's plate (#320), as the server's list
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
        if (HasPaneNode(s.Key) || IsGaragePlate(s.Key)) hole = 0; // the model has a real opening: nothing to darken (nor the garage's, the room behind is real)
        if (!_glassOverlays.TryGetValue(s.Key, out var overlay) || !GodotObject.IsInstanceValid(overlay))
            _glassOverlays[s.Key] = overlay = GlassFx.MakeOverlay(parent, rect, s.Key, hole, IsGaragePlate(s.Key) ? GlassFx.OsbShards : null);
        SetPaneGeometry(s.Key, !s.Shattered, animate);
        GlassFx.SetOverlay(overlay, GlassRules.CrackStage(s.Fraction, GlassData.CrackStages), GlassRules.MaxCrackStage(GlassData.CrackStages), s.Shattered);
        if (animate && parent.IsVisibleInTree()) GlassFx.Shatter(Owned, parent.GlobalTransform, rect, GlassData, s.Key, IsGaragePlate(s.Key) ? GlassFx.OsbShards : null, s.Key.Owner == GlassPaneKey.Map ? null : parent);
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
        parent = GlassHost(v.Node); rect = rects[key.Index]; hole = VehicleHole;
        return true;
    }

    /// <summary>
    /// The node a vehicle's pane overlays and shards hang under, in the vehicle's frame: its hull when the model has one (a driven vehicle's body
    /// leans and sways over its wheels, the root does not), else the root itself.
    /// </summary>
    static Node3D GlassHost(Node3D vehicle)
    {
        if (vehicle.FindChild("hull", true, false) is not Node3D hull) return vehicle;
        if (hull.GetNodeOrNull<Node3D>("GlassHost") is { } host) return host;
        var rest = Transform3D.Identity; // the hull's frame in the vehicle's, unleaned
        for (Node? n = hull; n is Node3D n3 && n3 != vehicle; n = n.GetParent())
            rest = (n == hull ? new Transform3D(Basis.FromScale(n3.Scale), n3.Position) : n3.Transform) * rest;
        host = new Node3D { Name = "GlassHost", Transform = rest.AffineInverse() };
        hull.AddChild(host);
        return host;
    }

    /// <summary>The name of the node of a vehicle model that holds the glass of pane <paramref name="index"/> (the pane's place in vehicles.json "glass").</summary>
    public static string PaneNodeName(int index) => $"glass_{index}";

    /// <summary>Shows or hides the model's own glass of a vehicle's pane. A model without a node for it (the loose legacy models) and a pane of the map are left as they are.</summary>
    void SetPaneGeometry(GlassPaneKey key, bool visible, bool animate = false)
    {
        if (IsGaragePlate(key)) MechanicGarage.Current?.SetPlate(standing: visible, animate); // the plate's box and mesh go with its pane, and the room's lights start
        if (key.Owner == GlassPaneKey.Map || !_cars.TryGetValue(key.Owner, out var v) || !GodotObject.IsInstanceValid(v.Node)) return;
        if (v.Node.FindChild(PaneNodeName(key.Index), true, false) is Node3D glass) glass.Visible = visible;
    }

    bool HasPaneNode(GlassPaneKey key) =>
        key.Owner != GlassPaneKey.Map && _cars.TryGetValue(key.Owner, out var v) && GodotObject.IsInstanceValid(v.Node) && v.Node.FindChild(PaneNodeName(key.Index), true, false) != null;

    /// <summary>The pane is the mechanic garage's breakable plate (a map pane of the shared list).</summary>
    bool IsGaragePlate(GlassPaneKey key) => key.Owner == GlassPaneKey.Map && key.Index < MapPanes.Count && MapPanes[key.Index].Node == MechanicGarageSite.PlateNode;

    void DropGlassOverlay(GlassPaneKey key)
    {
        SetPaneGeometry(key, true);
        if (_glassOverlays.Remove(key, out var overlay) && GodotObject.IsInstanceValid(overlay)) overlay.QueueFree();
    }

    /// <summary>Forgets the glass state when the client leaves (the overlays go with the nodes they are under).</summary>
    void GlassTeardown()
    {
        _glass.Reset(GlassCodec.Everything);
        _glassOverlays.Clear();
    }
}
