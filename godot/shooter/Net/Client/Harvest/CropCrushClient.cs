using Godot;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter.Net.Client.Harvest;

/// <summary>
/// Crops crushed by vehicles on the client (#328, #353). Every ground vehicle this client draws stamps the mask from its DRAWN pose (the predicted own car, the interpolated others) with the same pure code the
/// server runs (<see cref="CropCrushMask"/>), so the track lies under the car at once, not a network delay behind it. The server's cells (event <see cref="EventKind.CropCrush"/>, a joiner's whole mask) are
/// <see cref="CropCrushMask.Set"/> into the same mask: a cell already stamped here is skipped, one this client never drew is added. The server stays the authority; no cell is ever un-crushed before the round resets.
/// Headless clients draw no field and do nothing here.
/// </summary>
internal sealed class CropCrushClient
{
    static readonly CropCrushConfig CrushCfg = new();

    readonly ClientContext _ctx;
    readonly DriveClient _drive;
    CropCrushMask? _mask;
    List<CropField>? _fields;
    /// <summary>Cells new this frame (the plants in them are laid down, the ground patch added) and the scratch list a vehicle's step fills.</summary>
    readonly HashSet<(int X, int Z)> _cells = new();
    readonly List<Vector2> _centres = new();
    readonly List<int> _fresh = new();

    public CropCrushClient(ClientContext ctx, DriveClient drive) { _ctx = ctx; _drive = drive; }

    /// <summary>The fields vehicles leave tracks on, the same list the server builds (the wire index is its index).</summary>
    List<CropField> Fields => _fields ??= _ctx.Map == null
        ? new List<CropField>()
        : CropFields.From(_ctx.Map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())), includeBare: true);

    CropCrushMask? Mask => _mask ??= _ctx.Map == null ? null : new CropCrushMask(CrushCfg, Fields);

    /// <summary>Per frame, after the cars are drawn: every ground vehicle stamps from the pose it was drawn in. Nothing is allocated after a vehicle's first step.</summary>
    public void Stamp()
    {
        if (Mask is not { } mask || _ctx.Vehicles == null || Fields.Count == 0) return;
        foreach (var v in _ctx.Cars.All)
        {
            if (v.Destroyed || v.Kind >= _ctx.Vehicles.Vehicles.Count || !GodotObject.IsInstanceValid(v.Node)) continue;
            var def = _ctx.Vehicles.Vehicles[v.Kind];
            var drive = _drive.StateOf(v);
            var p = v.Node.GlobalPosition;
            var body = new CrushBody(new NVec2(p.X, p.Z), v.Node.Rotation.Y, def.HalfWidth, def.HalfLength, drive?.Speed ?? v.Kmh / 3.6f, v.Driver != 0, def.Flight == null && drive is not { Afloat: true });
            _fresh.Clear();
            var field = mask.Step(v.Id, body, _fresh);
            if (field >= 0) Queue(mask, field, _fresh);
            // the map line follows the vehicle while it crushes at all (also over ground crushed before), not only while it adds new cells
            if (_ctx.Hud?.Minimap.Trails is { } trails)
            {
                if (body.Ground && CropCrushRules.Crushes(CrushCfg, body.Speed, body.Driven) && mask.FieldAt(body.Pos) >= 0) trails.Extend(v.Id, body.Pos);
                else trails.Break(v.Id);
            }
        }
    }

    /// <summary>The cells a message flattens go in a queue; <see cref="Flush"/> lays the plants down once per frame, not per message.</summary>
    public void OnMessage(CrushCodec.Msg m)
    {
        if (_ctx.Map == null || !ShotFx.Enabled || Mask is not { } mask || mask.GridOf(m.Field) == null) return;
        _fresh.Clear();
        foreach (var i in CrushCodec.Cells(m))
            if (mask.Set(m.Field, i))
            {
                _fresh.Add(i);
                if (mask.GridOf(m.Field) is { } g && _ctx.Hud?.Minimap.Trails is { } trails) { var c = g.CentreOf(i); trails.AddCell(new NVec2(c.X, c.Y)); } // a cell this client did not stamp itself: a dot on the maps
            }
        Queue(mask, m.Field, _fresh);
    }

    void Queue(CropCrushMask mask, int field, List<int> cells)
    {
        var grid = mask.GridOf(field);
        if (grid == null) return;
        foreach (var i in cells)
        {
            _cells.Add(grid.CellOf(i));
            var c = grid.CentreOf(i);
            _centres.Add(new Vector2(c.X, c.Y));
        }
    }

    public void Flush()
    {
        if (_cells.Count == 0 || _ctx.Map == null) return;
        _ctx.Map.CropCrush(_cells, CrushCfg.CellM);
        _ctx.Map.CropMarkGround(_centres, CrushCfg.CellM);
        _cells.Clear(); _centres.Clear();
    }

    /// <summary>A new round: the server forgot every mark; so do the masks and each vehicle's path.</summary>
    public void Restart()
    {
        _mask?.Clear(); _cells.Clear(); _centres.Clear();
        _ctx.Hud?.Minimap.Trails.Clear();
    }
}
