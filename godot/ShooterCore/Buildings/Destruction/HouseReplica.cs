using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// The client's copy of one house (WP-F): its <see cref="HouseCells"/> following the server's 242 ops, the desync check (op seq and hash),
/// and the 243 catch-up (<see cref="HouseCellsCatchUp"/>). Pure: the Godot node (HouseWorldNode) draws what this says changed. The wire is <see cref="HouseCodec"/>'s.
/// </summary>
public sealed class HouseReplica
{
    HouseCellsCatchUp _catchUp = new();

    public HouseReplica(HouseGrid grid) { Cells = new HouseCells(grid); }

    public HouseCells Cells { get; }
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
        if (s == CatchUpStep.Done) AwaitingResync = false;
        return s;
    }

    /// <summary>Round reset (244, a new connection): every cell whole, op seq 0, nothing pending.</summary>
    public void Reset()
    {
        Cells.Restore();
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
