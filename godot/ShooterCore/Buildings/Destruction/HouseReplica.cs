using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// The client's copy of one house (WP-F): its <see cref="HouseCells"/> following the server's 242 and 254 ops, the desync check (op seq and hash),
/// and the 243 catch-up (<see cref="HouseCellsCatchUp"/>). Pure: the Godot node (HouseWorldNode) draws what this says changed. The wire is <see cref="HouseCodec"/>'s.
/// The driver's own plough is guessed ahead of the server (<see cref="Guess"/>): the cells it cut are gone from <see cref="View"/> (what is drawn and collided
/// with) at once, keyed by the input that cut them, while <see cref="Cells"/> stays the server's word. A guess goes when the server's op has it, when the
/// prediction's replay no longer cuts it, or <see cref="GuessGraceTicks"/> after its input was acknowledged without the server cutting it.
/// </summary>
public sealed class HouseReplica
{
    /// <summary>A guess the server has not confirmed this many inputs after it acknowledged the input that made it is taken back (the server did not cut there).</summary>
    public const int GuessGraceTicks = 30;

    HouseCellsCatchUp _catchUp = new();
    readonly Dictionary<int, ushort> _guess = new();
    /// <summary>During a replay: the guesses of the inputs being replayed that the replay has not cut again yet.</summary>
    readonly HashSet<int> _pending = new();
    readonly List<int> _keys = new();
    HouseCells? _view;
    bool _viewStale = true;

    public HouseReplica(HouseGrid grid) { Cells = new HouseCells(grid); }

    public HouseCells Cells { get; }

    /// <summary>Whether any cell is guessed cut.</summary>
    public bool Guessing => _guess.Count > 0;

    /// <summary>What the client draws and collides with: <see cref="Cells"/> less the guessed cells.</summary>
    public HouseCells View
    {
        get
        {
            if (_guess.Count == 0) return Cells;
            if (_viewStale)
            {
                _view ??= new HouseCells(Grid);
                _view.CopyFrom(Cells);
                foreach (var i in _guess.Keys) _view.Remove(i);
                _viewStale = false;
            }
            return _view!;
        }
    }

    static bool Before(ushort a, ushort b) => unchecked((short)(a - b)) < 0;

    /// <summary>Whether cell <paramref name="i"/> is still there for the plough of input <paramref name="seq"/>: the server has not removed it, and no earlier input's guess has.</summary>
    public bool PresentFor(int i, ushort seq) =>
        Cells.Solid(i) && !(_guess.TryGetValue(i, out var g) && !_pending.Contains(i) && Before(g, seq));

    /// <summary>Input <paramref name="seq"/>'s plough cut cell <paramref name="i"/>.</summary>
    public void Guess(int i, ushort seq)
    {
        _guess[i] = seq;
        _pending.Remove(i);
        _viewStale = true;
    }

    /// <summary>
    /// The server has stepped input <paramref name="ack"/>: guesses the server's cells now have are dropped (nothing changes in the view), guesses
    /// <see cref="GuessGraceTicks"/> older than it that it does not have are taken back into <paramref name="back"/> (their cells show again).
    /// </summary>
    public void Acked(ushort ack, List<int> back)
    {
        if (_guess.Count == 0) return;
        var old = unchecked((ushort)(ack - GuessGraceTicks));
        _keys.Clear(); _keys.AddRange(_guess.Keys);
        foreach (var i in _keys)
        {
            if (!Cells.Solid(i)) { _guess.Remove(i); _pending.Remove(i); continue; }
            if (Before(_guess[i], old)) { _guess.Remove(i); _pending.Remove(i); back.Add(i); }
        }
        _viewStale = true;
    }

    /// <summary>The prediction replays the inputs after <paramref name="ack"/>: their guesses wait to be cut again.</summary>
    public void BeginReplay(ushort ack)
    {
        _pending.Clear();
        foreach (var (i, s) in _guess) if (Before(ack, s)) _pending.Add(i);
    }

    /// <summary>The replay is over: the guesses it did not cut again are taken back into <paramref name="back"/>.</summary>
    public void EndReplay(List<int> back)
    {
        if (_pending.Count == 0) return;
        foreach (var i in _pending) { _guess.Remove(i); if (Cells.Solid(i)) back.Add(i); }
        _pending.Clear();
        _viewStale = true;
    }
    public HouseGrid Grid => Cells.Grid;
    /// <summary>A desync was seen and a 243 asked for: 242s are ignored until it is in (the 243 holds them).</summary>
    public bool AwaitingResync { get; private set; }

    public enum CarveOutcome { Applied, Ignored, Desync }

    /// <summary>
    /// One 242. Ignored while a resync is pending or when it is not the next op; a gap or a different op seq or hash after the apply is a desync
    /// (<see cref="AwaitingResync"/> set; the caller logs and asks for 243). The result is null when nothing was applied.
    /// </summary>
    public (CarveOutcome Outcome, CarveResult? Result) OnCarve(HouseCarveMsg m, DestructionConfig cfg)
    {
        if (AwaitingResync) return (CarveOutcome.Ignored, null);
        var next = (ushort)(Cells.OpSeq + 1);
        if (m.OpSeq != next)
        {
            if (unchecked((short)(m.OpSeq - Cells.OpSeq)) <= 0) return (CarveOutcome.Ignored, null); // already in this state (a 243 held it)
            AwaitingResync = true;
            return (CarveOutcome.Desync, null);
        }
        var r = Cells.Apply(m.Op, cfg);
        _viewStale = true;
        if (Cells.OpSeq != m.OpSeq || r.Hash != m.Hash)
        {
            AwaitingResync = true;
            return (CarveOutcome.Desync, r);
        }
        return (CarveOutcome.Applied, r);
    }

    /// <summary>One 254, as <see cref="OnCarve"/>: the plough's cells and, when the op says so, the support pass.</summary>
    public (CarveOutcome Outcome, CarveResult? Result) OnCut(HouseCutMsg m, DestructionConfig cfg)
    {
        if (AwaitingResync) return (CarveOutcome.Ignored, null);
        var next = (ushort)(Cells.OpSeq + 1);
        if (m.OpSeq != next)
        {
            if (unchecked((short)(m.OpSeq - Cells.OpSeq)) <= 0) return (CarveOutcome.Ignored, null);
            AwaitingResync = true;
            return (CarveOutcome.Desync, null);
        }
        var r = Cells.Cut(m.Cells, m.Settle, cfg);
        _viewStale = true;
        if (Cells.OpSeq != m.OpSeq || r.Hash != m.Hash)
        {
            AwaitingResync = true;
            return (CarveOutcome.Desync, r);
        }
        return (CarveOutcome.Applied, r);
    }

    /// <summary>Marks this house as waiting for a 243 (the caller asked for it).</summary>
    public void MarkAwaiting() => AwaitingResync = true;

    /// <summary>
    /// One 243 message (<see cref="HouseCellsCatchUp.Add"/>): Done when the house now equals the server's (op seq and hash; a pending resync is over),
    /// Pending while more are to come, Broken when the set has a gap or does not fit the house (the caller asks for 249).
    /// </summary>
    public CatchUpStep OnCells(HouseCellsMsg m)
    {
        var s = _catchUp.Add(m, Cells);
        _viewStale = true;
        if (s == CatchUpStep.Done) AwaitingResync = false;
        return s;
    }

    /// <summary>Round reset (244, a new connection): every cell whole, op seq 0, nothing pending.</summary>
    public void Reset()
    {
        Cells.Restore();
        _guess.Clear(); _pending.Clear(); _viewStale = true;
        AwaitingResync = false;
        _catchUp = new HouseCellsCatchUp();
    }
}

/// <summary>
/// A house's grid in Godot's map frame (x east, y up, z south; map local x, y, z = Godot x, -z, y) and the mesher's quads as Godot triangles.
/// Shared by the client's cut-face mesh, its collider and the discard shader's matrix, so they line up with the server's collider.
/// </summary>
public static class HouseFrame
{
    /// <summary>Grid units (cells) to Godot map local metres.</summary>
    public static Vector3 GridToGodot(HouseGrid g, Vector3 grid)
    {
        var m = g.ToMap(grid);
        return new Vector3(m.X, m.Z, -m.Y);
    }

    /// <summary>Godot map local metres to grid units (<see cref="GridToGodot"/>'s inverse).</summary>
    public static Vector3 GodotToGrid(HouseGrid g, Vector3 p) => g.ToGrid(new Vector3(p.X, -p.Z, p.Y));

    /// <summary>The grid-to-Godot map as an affine map: the images of the three grid unit steps (one cell each) and of the grid's corner.</summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z, Vector3 Origin) Affine(HouseGrid g)
    {
        var o = GridToGodot(g, Vector3.Zero);
        return (GridToGodot(g, Vector3.UnitX) - o, GridToGodot(g, Vector3.UnitY) - o, GridToGodot(g, Vector3.UnitZ) - o, o);
    }

    /// <summary>
    /// The quads as triangles in Godot map local metres, six vertices a quad, wound clockwise seen from the quad's normal (Godot's front face),
    /// whatever the order of the quad's edges. Normals are the quad's, in Godot's frame. Appends; the caller clears.
    /// </summary>
    public static void Triangles(HouseGrid g, List<Quad> quads, List<Vector3> positions, List<Vector3>? normals = null)
    {
        var (ax, ay, az, o) = Affine(g);
        Vector3 P(Vector3 q) => o + ax * q.X + ay * q.Y + az * q.Z;
        foreach (var q in quads)
        {
            Vector3 a = P(q.Corner), b = P(q.Corner + q.U), c = P(q.Corner + q.U + q.V), d = P(q.Corner + q.V);
            var n = Vector3.Normalize(ax * q.Normal.X + ay * q.Normal.Y + az * q.Normal.Z);
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) > 0) (b, d) = (d, b); // counter-clockwise from the front: flip
            positions.Add(a); positions.Add(b); positions.Add(c);
            positions.Add(a); positions.Add(c); positions.Add(d);
            if (normals != null) for (var k = 0; k < 6; k++) normals.Add(n);
        }
    }

    /// <summary>How much darker a cut face is than the cell's colour: the inside of a wall reads as the broken edge.</summary>
    public const float CutShade = 0.8f;

    /// <summary>The cut face colour of palette entry <paramref name="palette"/> (sRGB 0..1).</summary>
    public static Vector3 CutColour(HouseGrid g, byte palette)
    {
        var e = g.Palette.Span[palette];
        return new Vector3(e.R, e.G, e.B) * (CutShade / 255f);
    }
}
