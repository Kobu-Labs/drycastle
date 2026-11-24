using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The outlines draped on the terrain: named sets of ground segments, each a ribbon mesh under <see cref="Root"/> (visible only while
/// the editor is). The layer itself draws the two of the Select tool: the selected parcels (a property group counts as one shape, its
/// outer perimeter only) and the hovered parcel; every other outline is set by its tool (<see cref="Set"/>).
/// </summary>
public sealed class OutlineLayer
{
    public const string Selected = "outline_selected", Hover = "outline_hover";

    public Node3D Root { get; } = new() { Name = "EditorOutlines", Visible = false };

    readonly IEditorHost _host;
    readonly ParcelGroups _groups;
    readonly Func<string?> _hovered;
    // outline kinds: node name -> segments last built (ground xz pairs)
    readonly Dictionary<string, MeshInstance3D> _nodes = new();
    readonly Dictionary<string, int> _segmentCounts = new();
    // which outline kinds need a rebuild: a hover change does not redraw the selection
    bool _dirty = true, _hoverDirty = true, _selectionDirty = true;

    /// <param name="hovered">The hovered parcel (null: none).</param>
    public OutlineLayer(IEditorHost host, ParcelGroups groups, Func<string?> hovered)
        => (_host, _groups, _hovered) = (host, groups, hovered);

    public int SegmentCount(string kind) => _segmentCounts.GetValueOrDefault(kind);
    public MeshInstance3D? Node(string kind) => _nodes.GetValueOrDefault(kind);

    /// <summary>The outlines of the selection and of the hovered parcel are redrawn on the next frame.</summary>
    public void MarkAll() => _dirty = _hoverDirty = _selectionDirty = true;

    public void MarkHover() { _hoverDirty = true; _dirty = true; }

    /// <summary>The selection changed: the hover outline depends on it too (a selected parcel is not drawn as hover).</summary>
    public void MarkSelection() => _selectionDirty = _hoverDirty = _dirty = true;

    /// <summary>Per frame: rebuild when something was marked.</summary>
    public void Update() { if (_dirty) Rebuild(); }

    /// <summary>Rebuild the outline meshes now (normally done on the next frame after a change). Only the kinds that changed are rebuilt.</summary>
    public void Rebuild()
    {
        _dirty = false;
        if (_hoverDirty)
        {
            _hoverDirty = false;
            var hover = _hovered();
            var hoverSegs = hover != null && !_host.Selection.Contains(hover) ? UnitSegments(new[] { hover }) : new();
            Set(Hover, hoverSegs, new Color(1f, 1f, 1f, 0.55f), 0.25f, 0.30f);
        }
        if (_selectionDirty)
        {
            _selectionDirty = false;
            var selSegs = UnitSegments(_host.Selection.Where(_host.Map.Parcels.ContainsKey));
            Set(Selected, selSegs, new Color(1f, 0.92f, 0.1f), 0.29f, 0.55f);
        }
    }

    /// <summary>Outline of parcels where a property group counts as one shape (outer perimeter only).</summary>
    List<(Vector2, Vector2)> UnitSegments(IEnumerable<string> ids)
    {
        var segs = new List<(Vector2, Vector2)>();
        var seen = new HashSet<string>();
        foreach (var id in ids)
        {
            var g = _groups.GroupOf(id);
            if (g == null) { segs.AddRange(ParcelOutline.Edges(_host.Map.Parcels[id])); continue; }
            if (!seen.Add(g)) continue;
            segs.AddRange(_groups.Perimeter(g));
        }
        return segs;
    }

    /// <summary>Draw a named set of ground segments draped on the terrain; an empty list clears it.</summary>
    public void Set(string name, List<(Vector2 a, Vector2 b)> segs, Color colour, float lift, float width)
    {
        _segmentCounts[name] = segs.Count;
        if (!_nodes.TryGetValue(name, out var mi))
        {
            mi = new MeshInstance3D
            {
                Name = name,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = colour,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency = colour.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
                },
            };
            Root.AddChild(mi);
            _nodes[name] = mi;
        }
        else if (segs.Count > 0 && mi.MaterialOverride is StandardMaterial3D m) // the colour of a kind may change (a selected patch takes its crop's)
        {
            m.AlbedoColor = colour;
            m.Transparency = colour.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
        }
        mi.Mesh = segs.Count == 0 ? null : Gfx.Ribbon(_host.Map, segs, lift, width);
    }
}
