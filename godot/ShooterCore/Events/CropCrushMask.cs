using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>One ground vehicle as a pose, for <see cref="CropCrushMask.Step"/>: where it is (game x, z), its yaw (0 faces -Z, positive turns left), its body, how fast it moves and whether it can crush at all.</summary>
/// <param name="Ground">Not destroyed, not flying, not afloat: the caller knows (the server from its car, a client from what it draws).</param>
/// <param name="Driven">Somebody sits at the wheel.</param>
public readonly record struct CrushBody(Vector2 Pos, float Yaw, float HalfWidth, float HalfLength, float Speed, bool Driven, bool Ground);

/// <summary>
/// Everything of the vehicles' crop crush that the server and a client must do the same way (#328, #353): the masks per crop field, the cap over all of them, and for every vehicle its path sampler.
/// The server steps it with the vehicles' real poses and sends what is new; a client steps its own copy with the poses it DRAWS (the predicted own car, the interpolated others), so the track lies under the
/// car with no network delay, and <see cref="Set"/>s what the server sent. The server's mask stays the authority: a cell that is already set is skipped, so both agree without a handshake. Pure: no nodes.
/// </summary>
public sealed class CropCrushMask
{
    readonly CropCrushConfig _cfg;
    readonly IReadOnlyList<CropField> _fields;
    readonly Dictionary<int, CrushGrid?> _grids = new();
    readonly Dictionary<ushort, Tracker> _trackers = new();
    (Vector2 Lo, Vector2 Hi)[]? _bounds;

    /// <summary>The path of one vehicle: its sampler, and which end leads (decided after it moved <see cref="CropCrushConfig.DirectionM"/> from the anchor, so jitter does not flip it).</summary>
    sealed class Tracker
    {
        public readonly SwathSampler Sampler;
        public Vector2 Anchor; public bool Anchored; public float Dir = 1f;
        public Tracker(float sampleM) => Sampler = new SwathSampler(sampleM);
        public void Reset() { Sampler.Reset(); Anchored = false; Dir = 1f; }
    }

    public CropCrushMask(CropCrushConfig cfg, IReadOnlyList<CropField> fields) { _cfg = cfg; _fields = fields; }

    /// <summary>How many cells all fields have crushed (the cap's count).</summary>
    public int Total { get; private set; }

    /// <summary>The fields' masks built so far (a field that nobody crushed has none yet), by field index; a null one is a field too big to track.</summary>
    public IReadOnlyDictionary<int, CrushGrid?> Grids => _grids;

    /// <summary>A new round: no cell is crushed, every vehicle's path starts afresh.</summary>
    public void Clear()
    {
        _grids.Clear(); Total = 0;
        foreach (var t in _trackers.Values) t.Reset();
    }

    /// <summary>The mask of a field (index = the wire id), built on first use; null for a field too big to track or an index that is no field.</summary>
    public CrushGrid? GridOf(int field)
    {
        if ((uint)field >= (uint)_fields.Count) return null;
        if (!_grids.TryGetValue(field, out var grid)) _grids[field] = grid = CrushGrid.For(_fields[field].Ring, _cfg);
        return grid;
    }

    /// <summary>Crushes a cell the server told (a delta or a joiner's runs). True when it was not crushed yet (it counts to the cap); a cell the local stamping already set is skipped.</summary>
    public bool Set(int field, int index)
    {
        if (GridOf(field) is not { } grid || !grid.Set(index, Total)) return false;
        Total++;
        return true;
    }

    /// <summary>The crop field (index) a point lies on, or -1.</summary>
    public int FieldAt(Vector2 p)
    {
        if (_bounds == null || _bounds.Length != _fields.Count)
        {
            _bounds = new (Vector2, Vector2)[_fields.Count];
            for (var i = 0; i < _bounds.Length; i++)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (var v in _fields[i].Ring) { x0 = MathF.Min(x0, v.X); y0 = MathF.Min(y0, v.Y); x1 = MathF.Max(x1, v.X); y1 = MathF.Max(y1, v.Y); }
                _bounds[i] = (new Vector2(x0, y0), new Vector2(x1, y1));
            }
        }
        for (var i = 0; i < _bounds.Length; i++)
        {
            var (lo, hi) = _bounds[i];
            if (p.X < lo.X || p.X > hi.X || p.Y < lo.Y || p.Y > hi.Y) continue;
            if (Poly.Contains(_fields[i].Ring, p)) return i;
        }
        return -1;
    }

    /// <summary>
    /// One step of one vehicle. While it crushes (<see cref="CropCrushRules.Crushes"/>, on the ground, on a crop field) its leading end is sampled every <see cref="CropCrushConfig.SampleM"/> of travel
    /// and the strip along the segment is stamped; the cells that are new are appended to <paramref name="fresh"/> (not cleared, no allocation after the vehicle's first step). Returns the field they are on, or -1 when
    /// none was stamped (the list is unchanged). The leading end is the front bumper going forward, the rear reversing.
    /// </summary>
    public int Step(ushort id, in CrushBody body, List<int> fresh)
    {
        var active = body.Ground && CropCrushRules.Crushes(_cfg, body.Speed, body.Driven);
        if (!_trackers.TryGetValue(id, out var t))
        {
            if (!active) return -1;
            _trackers[id] = t = new Tracker(_cfg.SampleM);
        }
        if (!active) { t.Reset(); return -1; }
        var fwd = new Vector2(-MathF.Sin(body.Yaw), -MathF.Cos(body.Yaw));
        if (!t.Anchored) { t.Anchor = body.Pos; t.Anchored = true; }
        else
        {
            var moved = body.Pos - t.Anchor;
            if (moved.LengthSquared() >= _cfg.DirectionM * _cfg.DirectionM)
            {
                var dir = Vector2.Dot(moved, fwd) >= 0 ? 1f : -1f;
                if (dir != t.Dir) { t.Sampler.Reset(); t.Dir = dir; } // it turned round: the other end leads, the path starts afresh
                t.Anchor = body.Pos;
            }
        }
        var lead = body.Pos + fwd * (t.Dir * CropCrushRules.LeadM(_cfg, body.HalfWidth, body.HalfLength));
        var field = FieldAt(lead);
        if (!t.Sampler.Step(field, lead, true, out var a, out var b)) return -1;
        if (GridOf(field) is not { } grid) return -1;
        var n = grid.Mark(a.Metres, b.Metres, CropCrushRules.HalfStrip(_cfg, body.HalfWidth), Total, fresh);
        if (n == 0) return -1;
        Total += n;
        return field;
    }
}
