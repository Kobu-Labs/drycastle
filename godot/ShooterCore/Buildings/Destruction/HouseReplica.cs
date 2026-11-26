using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// The client's copy of one house (WP-F): its <see cref="HouseCells"/> following the server's 242 ops, the desync check (op seq and hash),
/// and the 243 parts put back together into one run list. Pure: the Godot node (HouseWorldNode) draws what this says changed.
/// </summary>
public sealed class HouseReplica
{
    readonly List<byte> _runs = new();
    uint _runCount;
    long _runSum;
    ushort _partSeq;
    uint _partHash;
    bool _assembling;

    public HouseReplica(HouseGrid grid)
    {
        Cells = new HouseCells(grid);
        var cells = grid.Cells.Span;
        foreach (var c in cells) if (c != 0) SolidCount++;
    }

    public HouseCells Cells { get; }
    public HouseGrid Grid => Cells.Grid;
    /// <summary>Cells solid in the file: the sum of a full run list (243).</summary>
    public int SolidCount { get; }
    /// <summary>A desync was seen and a 243 asked for: 242s are ignored until it is in (the 243 holds them).</summary>
    public bool AwaitingResync { get; private set; }

    public enum CarveOutcome { Applied, Ignored, Desync }

    /// <summary>
    /// One 242. Ignored while a resync is pending or when it is not the next op; a gap or a different op seq or hash after the apply is a desync
    /// (<see cref="AwaitingResync"/> set; the caller logs and asks for 243). The result is null when nothing was applied.
    /// </summary>
    public (CarveOutcome Outcome, CarveResult? Result) OnCarve(HouseReplicaWire.Carve m, DestructionConfig cfg)
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

    public enum CellsOutcome { Partial, Dropped, Replaced, ReplacedHashDiffers }

    /// <summary>
    /// One 243 part. A part with first run 0 starts the list; a later part must continue it (same op seq and hash, its first run the count so far), else it is
    /// dropped and the list waits for a new start. The list is whole when its runs add up to <see cref="SolidCount"/>: the cells are replaced
    /// (<see cref="HouseCells.DecodeRemoved"/>) and the hash checked.
    /// </summary>
    public CellsOutcome OnCells(HouseReplicaWire.CellsPart m)
    {
        if (m.FirstRun == 0)
        {
            _runs.Clear(); _runCount = 0; _runSum = 0; _partSeq = m.OpSeq; _partHash = m.Hash; _assembling = true;
        }
        else if (!_assembling || m.FirstRun != _runCount || m.OpSeq != _partSeq || m.Hash != _partHash)
        {
            _assembling = false;
            return CellsOutcome.Dropped;
        }
        var d = m.Runs.Span;
        var p = 0;
        try
        {
            while (p < d.Length) { _runSum += Varint.Read(d, ref p); _runCount++; }
        }
        catch (InvalidDataException) { _assembling = false; return CellsOutcome.Dropped; }
        foreach (var b in d) _runs.Add(b);
        if (_runSum < SolidCount) return CellsOutcome.Partial;
        _assembling = false;
        if (_runSum > SolidCount) return CellsOutcome.Dropped;
        Cells.DecodeRemoved(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_runs), _partSeq);
        AwaitingResync = false;
        return Cells.Hash() == _partHash ? CellsOutcome.Replaced : CellsOutcome.ReplacedHashDiffers;
    }

    /// <summary>Round reset (244, a new connection): every cell whole, op seq 0, nothing pending.</summary>
    public void Reset()
    {
        Cells.Restore();
        AwaitingResync = false;
        _assembling = false;
        _runs.Clear();
    }

    /// <summary>The R8 layer <paramref name="z"/> of the removed-cell 3D texture (Nx x Ny, x fastest): 255 where a solid cell is removed, else 0.</summary>
    public void FillRemovedLayer(int z, byte[] into)
    {
        var g = Grid;
        int nx = g.Nx, n = nx * g.Ny, i0 = n * z;
        if (into.Length != n) throw new ArgumentException($"layer of {n} bytes expected");
        var cells = g.Cells.Span;
        for (var k = 0; k < n; k++)
        {
            var i = i0 + k;
            into[k] = cells[i] != 0 && !Cells.Solid(i) ? (byte)255 : (byte)0;
        }
    }
}

/// <summary>
/// The client's reading of events 242-244 and its 249 request (docs/house-destruction.md Contract 4; little-endian). WP-F's own until
/// <see cref="HouseCodec"/> has the same; it then can call that.
/// </summary>
public static class HouseReplicaWire
{
    public readonly record struct Carve(ushort House, ushort OpSeq, CarveOp Op, uint Hash);
    /// <summary>One 243 message: its runs are whole varints, <see cref="FirstRun"/> is the index of its first run in the house's whole list.</summary>
    public readonly record struct CellsPart(ushort House, ushort OpSeq, uint Hash, uint FirstRun, ReadOnlyMemory<byte> Runs);
    public readonly record struct Settings(bool RubbleStay, bool Reset);

    public const int CarveBytes = 16, CellsHeaderBytes = 12, SettingsBytes = 2;

    public static Carve? DecodeCarve(byte[] p)
    {
        if (p.Length < CarveBytes) return null;
        var s = p.AsSpan();
        return new Carve(U16(s, 0), U16(s, 2),
            new CarveOp(BinaryPrimitives.ReadInt16LittleEndian(s[4..]), BinaryPrimitives.ReadInt16LittleEndian(s[6..]), BinaryPrimitives.ReadInt16LittleEndian(s[8..]), U16(s, 10)),
            BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
    }

    public static CellsPart? DecodeCells(byte[] p)
    {
        if (p.Length < CellsHeaderBytes) return null;
        var s = p.AsSpan();
        return new CellsPart(U16(s, 0), U16(s, 2), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), p.AsMemory(CellsHeaderBytes));
    }

    public static Settings? DecodeSettings(byte[] p) => p.Length < SettingsBytes ? null : new Settings((p[0] & 1) != 0, p[1] == 1);

    public static byte[] EncodeResync(ushort house)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, house);
        return b;
    }

    static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
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
    /// A grid box (min and max corners, grid units) as a Godot box: its centre (map local), its size in metres along its own axes and its yaw about
    /// Godot +y. The box's axes are grid x, grid z (up) and minus grid y, so the size is (dx, dz, dy) cells times the cell size.
    /// </summary>
    public static (Vector3 Centre, Vector3 Size, float Yaw) Box(HouseGrid g, Vector3 min, Vector3 max)
    {
        var d = (max - min) * g.Cell;
        return (GridToGodot(g, (min + max) * 0.5f), new Vector3(d.X, d.Z, d.Y), g.Yaw);
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
