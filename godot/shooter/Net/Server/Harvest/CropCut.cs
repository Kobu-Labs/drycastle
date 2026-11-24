using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Harvest;

/// <summary>What the reels have cut on one crop field and the bales dropped there (<see cref="FieldSwath"/>); the clients derive the same from the stretches sent (<see cref="EventKind.HarvesterCut"/>).</summary>
internal sealed class SwathField
{
    public required FieldSwath Swath;
    public double LastCut;
    public readonly List<HayBale> Bales = new();
    /// <summary>Kept points not sent yet (the first one was, when it is a continuation).</summary>
    public readonly List<SwathPt> Open = new();
}

/// <summary>
/// The ground the machines work: the crop fields of the map, the swath a harvester's reel cuts (its real path, sampled every couple of metres, whoever drives; the kept stretches go out in batches), and the
/// crops every ground vehicle crushes driving over a field (#328, #353: <see cref="CropCrushMask"/>, the same code a client stamps its drawn vehicles with; the new cells go out <see cref="CropCrushConfig.FlushS"/> apart).
/// All cosmetic: a client draws it from what is sent, never per plant.
/// </summary>
internal sealed class CropCut
{
    readonly ServerContext _ctx;
    readonly HarvesterConfig _cfg;
    readonly CropCrushConfig _crushCfg;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Fleet _fleet;

    List<CropField>? _cropFields;
    List<CropField>? _crushFields;
    List<(NVec2 Min, NVec2 Max)>? _cropBounds;
    readonly Dictionary<int, SwathField> _swaths = new();
    readonly Dictionary<ushort, SwathSampler> _samplers = new();
    int _swathTotal;
    double _swathFlushAt;

    CropCrushMask? _crushMask;
    readonly Dictionary<int, List<int>> _crushPending = new();
    readonly List<int> _crushFresh = new(); // a scratch list
    double _crushFlushAt;

    public CropCut(ServerContext ctx, HarvesterConfig cfg, CropCrushConfig crushCfg, Drycastle.MapLoader.MapLoader? map, Fleet fleet)
    {
        _ctx = ctx; _cfg = cfg; _crushCfg = crushCfg; _map = map; _fleet = fleet;
    }

    /// <summary>The crop fields of the map, once (the ground patches of crop types; nothing without a map).</summary>
    public List<CropField> Fields => _cropFields ??= FieldsOf(includeBare: false);

    /// <summary>The fields vehicles leave tracks on (the harvestable ones and the bare ploughed ones); the crush mask's indices are these.</summary>
    List<CropField> CrushFields => _crushFields ??= FieldsOf(includeBare: true);

    List<CropField> FieldsOf(bool includeBare) => _map == null
        ? new List<CropField>()
        : CropFields.From(_map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())), includeBare);

    /// <summary>The fields' cut so far, for the bales' fire to look through (a struct enumerator: nothing is allocated).</summary>
    public Dictionary<int, SwathField>.ValueCollection Swaths => _swaths.Values;

    /// <summary>A harvester stands empty or gone: its sampler is dropped.</summary>
    public void Forget(ushort carId) => _samplers.Remove(carId);

    /// <summary>A new round: the cut and the marks do not outlive it.</summary>
    public void Reset()
    {
        _swaths.Clear(); _samplers.Clear(); _swathTotal = 0;
        _crushMask?.Clear(); _crushPending.Clear();
    }

    /// <summary>A joiner learns the cut so far, field by field (bounded: SwathCodec, <see cref="HarvesterConfig.MaxSwathPoints"/>), and the crushed ground: the whole mask of each field as runs.</summary>
    public void SendOnJoin(ServerClient c)
    {
        foreach (var (field, sf) in _swaths)
        {
            var age = (ushort)Math.Clamp(_ctx.Now - sf.LastCut, 0, ushort.MaxValue - 1);
            for (var i = 0; i < sf.Swath.Strokes.Count; i++)
                foreach (var bytes in SwathCodec.Pack((ushort)field, _cfg.HeaderWidthM, age, i < sf.Swath.FirstCounted, sf.Swath.Strokes[i], replay: true))
                    _ctx.SendEvent(c, EventKind.HarvesterCut, bytes);
        }
        foreach (var (field, grid) in _crushMask?.Grids ?? new Dictionary<int, CrushGrid?>())
            if (grid != null && grid.Count > 0)
                foreach (var bytes in CrushCodec.PackRuns((ushort)field, grid.Runs(), _crushCfg.MaxMessageBytes)) _ctx.SendEvent(c, EventKind.CropCrush, bytes);
    }

    /// <summary>The crop field (index in the field list, the wire id) a point lies on, or -1.</summary>
    int FieldAt(NVec2 p)
    {
        var fields = Fields;
        if (_cropBounds == null || _cropBounds.Count != fields.Count)
            _cropBounds = fields.Select(f => (new NVec2(f.Ring.Min(v => v.X), f.Ring.Min(v => v.Y)), new NVec2(f.Ring.Max(v => v.X), f.Ring.Max(v => v.Y)))).ToList();
        for (var i = 0; i < fields.Count; i++)
        {
            var (lo, hi) = _cropBounds[i];
            if (p.X < lo.X || p.X > hi.X || p.Y < lo.Y || p.Y > hi.Y) continue;
            if (Poly.Contains(fields[i].Ring, p)) return i;
        }
        return -1;
    }

    /// <summary>While somebody drives a harvester forward over a crop field its reel cuts a swath: the real path, sampled every couple of metres, whoever drives. The kept stretches are sent in batches.</summary>
    public void Cut(ServerCar car)
    {
        if (_map == null) return;
        if (!_samplers.TryGetValue(car.Id, out var sampler)) _samplers[car.Id] = sampler = new SwathSampler(_cfg);
        var reel = _cfg.Reel.Centre(new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), car.State.Yaw);
        var active = car.State.Speed >= _cfg.CutMinSpeedMps;
        var field = active ? FieldAt(reel) : -1;
        if (!sampler.Step(field, reel, active, out var a, out var b)) return;
        if (!_swaths.TryGetValue(field, out var sf)) _swaths[field] = sf = new SwathField { Swath = new FieldSwath(_cfg, Fields[field].DropsBales) };
        var before = sf.Swath.PointCount;
        if (!sf.Swath.TryAdd(a, b, _swathTotal)) { Flush(field, sf); sf.Open.Clear(); return; }
        _swathTotal += sf.Swath.PointCount - before;
        sf.LastCut = _ctx.Now;
        if (sf.Open.Count == 0 || sf.Open[^1] != a) { Flush(field, sf); sf.Open.Clear(); sf.Open.Add(a); }
        sf.Open.Add(b);
        for (var i = sf.Bales.Count; i < sf.Swath.Bales.Count; i++)
        {
            var (pos, _) = sf.Swath.Bales[i];
            var ground = _map.GroundHeight(pos.X, pos.Y);
            if (float.IsNaN(ground)) ground = _map.GroundHeightOrMean(pos.X, pos.Y);
            sf.Bales.Add(new HayBale(new NVec3(pos.X, ground, pos.Y), _cfg, (ushort)i));
        }
    }

    void Flush(int field, SwathField sf)
    {
        if (sf.Open.Count < 2) return;
        foreach (var bytes in SwathCodec.Pack((ushort)field, _cfg.HeaderWidthM, 0, false, sf.Open)) _ctx.Broadcast(EventKind.HarvesterCut, bytes);
        sf.Open.RemoveRange(0, sf.Open.Count - 1);
    }

    /// <summary>Twice a second: the kept stretches go out; a field's bales fade <see cref="HarvesterConfig.DroppedBaleFadeS"/> after its last cut (every client counts the same from the age it was told).</summary>
    public void FlushSwaths()
    {
        if (_swaths.Count == 0 || _ctx.Now < _swathFlushAt) return;
        _swathFlushAt = _ctx.Now + 0.5;
        foreach (var (field, sf) in _swaths)
        {
            Flush(field, sf);
            if (sf.Bales.Count > 0 && _ctx.Now - sf.LastCut >= _cfg.DroppedBaleFadeS) { sf.Bales.Clear(); sf.Swath.FadeBales(); sf.Open.Clear(); }
        }
    }

    /// <summary>Every ground vehicle that moves (with a driver, or rolling fast) over a crop field stamps its body's width into the field's mask, from its leading end, a sample every couple of metres; the new cells go out in batches. A client stamped the same from what it draws; this mask is the authority.</summary>
    public void Crush()
    {
        if (_map == null) return;
        var mask = _crushMask ??= new CropCrushMask(_crushCfg, CrushFields);
        foreach (var car in _fleet.Cars)
        {
            var ground = !car.Destroyed && car.Def.Flight == null && !car.Afloat;
            var body = new CrushBody(new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), car.State.Yaw, car.HalfWidth, car.HalfLength, car.State.Speed, car.Seat.Occupied, ground);
            _crushFresh.Clear();
            var field = mask.Step(car.Id, body, _crushFresh);
            if (field < 0) continue;
            if (!_crushPending.TryGetValue(field, out var list)) _crushPending[field] = list = new List<int>();
            list.AddRange(_crushFresh);
        }
        if (_crushPending.Count == 0 || _ctx.Now < _crushFlushAt) return;
        _crushFlushAt = _ctx.Now + _crushCfg.FlushS;
        foreach (var (field, list) in _crushPending)
        {
            list.Sort();
            foreach (var bytes in CrushCodec.PackDelta((ushort)field, list, _crushCfg.MaxMessageBytes)) _ctx.Broadcast(EventKind.CropCrush, bytes);
        }
        _crushPending.Clear();
    }
}
