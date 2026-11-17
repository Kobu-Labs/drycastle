using Godot;
using Drycastle.ShooterCore;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// Crops crushed by vehicles on the client (#328, #353). Every ground vehicle this client draws stamps the mask from its DRAWN pose (the predicted own car, the interpolated others) with the same pure code the
/// server runs (<see cref="CropCrushMask"/>), so the track lies under the car at once, not a network delay behind it. The server's cells (event <see cref="EventKind.CropCrush"/>, a joiner's whole mask) are
/// <see cref="CropCrushMask.Set"/> into the same mask: a cell already stamped here is skipped, one this client never drew is added. The server stays the authority; no cell is ever un-crushed before the round resets.
/// Headless clients draw no field and do nothing here.
/// </summary>
public partial class NetClient
{
    static readonly CropCrushConfig CrushCfg = new();
    CropCrushMask? _crushMask;
    /// <summary>Cells new this frame (the plants in them are laid down, the ground patch added) and the scratch list a vehicle's step fills.</summary>
    readonly HashSet<(int X, int Z)> _crushCells = new();
    readonly List<Vector2> _crushCentres = new();
    readonly List<int> _crushFresh = new();

    CropCrushMask? CrushMask => _crushMask ??= Map == null ? null : new CropCrushMask(CrushCfg, HarvFields);

    /// <summary>Per frame, after <c>RenderCars</c>: every ground vehicle stamps from the pose it was drawn in. Nothing is allocated after a vehicle's first step.</summary>
    void StampCrush()
    {
        if (CrushMask is not { } mask || Vehicles == null || HarvFields.Count == 0) return;
        foreach (var v in _cars.Values)
        {
            if (v.Destroyed || v.Kind >= Vehicles.Vehicles.Count || !GodotObject.IsInstanceValid(v.Node)) continue;
            var def = Vehicles.Vehicles[v.Kind];
            var drive = DriveOf(v);
            var p = v.Node.GlobalPosition;
            var body = new CrushBody(new NVec2(p.X, p.Z), v.Node.Rotation.Y, def.HalfWidth, def.HalfLength, drive?.Speed ?? v.Kmh / 3.6f, v.Driver != 0, def.Flight == null && drive is not { Afloat: true });
            _crushFresh.Clear();
            var field = mask.Step(v.Id, body, _crushFresh);
            if (field >= 0) Queue(mask, field, _crushFresh);
        }
    }

    /// <summary>The cells a message flattens go in a queue; <see cref="FlushCrush"/> lays the plants down once per frame, not per message.</summary>
    void OnCropCrush(CrushCodec.Msg m)
    {
        if (Map == null || !ShotFx.Enabled || CrushMask is not { } mask || mask.GridOf(m.Field) == null) return;
        _crushFresh.Clear();
        foreach (var i in CrushCodec.Cells(m))
            if (mask.Set(m.Field, i)) _crushFresh.Add(i);
        Queue(mask, m.Field, _crushFresh);
    }

    void Queue(CropCrushMask mask, int field, List<int> cells)
    {
        var grid = mask.GridOf(field);
        if (grid == null) return;
        foreach (var i in cells)
        {
            _crushCells.Add(grid.CellOf(i));
            var c = grid.CentreOf(i);
            _crushCentres.Add(new Vector2(c.X, c.Y));
        }
    }

    void FlushCrush()
    {
        if (_crushCells.Count == 0 || Map == null) return;
        Map.CropCrush(_crushCells, CrushCfg.CellM);
        Map.CropMarkGround(_crushCentres, CrushCfg.CellM);
        _crushCells.Clear(); _crushCentres.Clear();
    }

    /// <summary>A new round: the server forgot every mark; so do the masks and each vehicle's path.</summary>
    void CropCrushRestart()
    {
        _crushMask?.Clear(); _crushCells.Clear(); _crushCentres.Clear();
    }
}
