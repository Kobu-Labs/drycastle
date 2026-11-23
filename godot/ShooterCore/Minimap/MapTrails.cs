using System.Numerics;

namespace Drycastle.ShooterCore.Minimap;

/// <summary>
/// The tracks vehicles leave in crop fields, as the two maps draw them (thin lines over the terrain): one polyline per stretch a vehicle crushed (the vehicle's centre line, a point every
/// <see cref="MinStepM"/>), plus sparse dots for cells only the server told (a late joiner's history, a vehicle this client never drew). Ground (x, z) metres, the game's axes. Local, cosmetic, cleared with
/// the crush masks at a round's start. Both maps read the same instance (<see cref="MapMarkers"/>), so they agree. Bounded: past <see cref="MaxPoints"/> the oldest stretch goes first.
/// </summary>
public sealed class MapTrails
{
    /// <summary>A vehicle's path gets a point this far (metres) from its last one: about the crush sample spacing, so a map never holds more detail than it can show.</summary>
    public const float MinStepM = 4f;
    /// <summary>Points over all polylines; the oldest polyline is dropped beyond it (8000 points are 32 km of track).</summary>
    public const int MaxPoints = 8000;
    /// <summary>Server-told cells are thinned to one dot per block of this side (metres), and at most <see cref="MaxDots"/> of them.</summary>
    public const float DotBlockM = 4f;
    public const int MaxDots = 6000;

    /// <summary>One unbroken stretch: its points and their bounding box (for culling).</summary>
    public sealed class Trail
    {
        readonly List<Vector2> _points = new();
        public IReadOnlyList<Vector2> Points => _points;
        public Vector2 Min { get; private set; }
        public Vector2 Max { get; private set; }

        internal void Add(Vector2 p)
        {
            if (_points.Count == 0) { Min = Max = p; }
            else { Min = Vector2.Min(Min, p); Max = Vector2.Max(Max, p); }
            _points.Add(p);
        }
    }

    readonly List<Trail> _trails = new();
    readonly Dictionary<ushort, Trail> _open = new();
    readonly List<Vector2> _dots = new();
    readonly HashSet<(int X, int Z)> _blocks = new();
    int _points;

    /// <summary>The stretches, oldest first (the list is this object's own; do not keep it). One with fewer than two points draws nothing.</summary>
    public IReadOnlyList<Trail> Trails => _trails;
    public IReadOnlyList<Vector2> Dots => _dots;
    public int PointCount => _points;
    public int Version { get; private set; }

    /// <summary>The vehicle crushed at <paramref name="at"/> (its centre): the open stretch gets a point when it is at least <see cref="MinStepM"/> from the last one, or a new stretch starts.</summary>
    public void Extend(ushort vehicle, Vector2 at)
    {
        if (!_open.TryGetValue(vehicle, out var t))
        {
            t = new Trail();
            _open[vehicle] = t;
            _trails.Add(t);
            t.Add(at); _points++; Version++;
            Trim();
            return;
        }
        var last = t.Points[^1];
        if (Vector2.DistanceSquared(last, at) < MinStepM * MinStepM) return;
        t.Add(at); _points++; Version++;
        Trim();
    }

    /// <summary>The vehicle stopped crushing (stopped, left the field, turned afloat or into the air): its next crush starts a new stretch.</summary>
    public void Break(ushort vehicle) => _open.Remove(vehicle);

    /// <summary>A cell the server told that this client did not stamp itself: one dot per <see cref="DotBlockM"/> block, up to <see cref="MaxDots"/>.</summary>
    public void AddCell(Vector2 centre)
    {
        if (_dots.Count >= MaxDots) return;
        if (!_blocks.Add(((int)MathF.Floor(centre.X / DotBlockM), (int)MathF.Floor(centre.Y / DotBlockM)))) return;
        _dots.Add(centre); Version++;
    }

    /// <summary>A new round: every stretch and dot goes.</summary>
    public void Clear()
    {
        _trails.Clear(); _open.Clear(); _dots.Clear(); _blocks.Clear(); _points = 0; Version++;
    }

    void Trim()
    {
        while (_points > MaxPoints && _trails.Count > 1)
        {
            var oldest = _trails[0];
            _trails.RemoveAt(0);
            _points -= oldest.Points.Count;
            foreach (var (id, open) in _open)
                if (ReferenceEquals(open, oldest)) { _open.Remove(id); break; }
        }
    }
}
