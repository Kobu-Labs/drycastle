using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>What an animal is doing, as far as its hit shape goes (the clip a client plays for it).</summary>
public enum AnimalPose : byte { Idle = 0, Eat = 1, Walk = 2, Run = 3 }

/// <summary>Where a bullet met a hit shape: how far along the ray, whether that cell is head, and the cell.</summary>
public readonly record struct ShapeHit(float Distance, bool Head, int Col, int Row);

/// <summary>
/// One hit shape (GLOSSARY "Hit shape"): the animal in one pose as a grid of cells on its side plane. A column is a step of <see cref="Cell"/>
/// metres along its length (column 0 starts <see cref="Z0"/> metres ahead of its position, so usually behind it), a row a step up from the ground.
/// A covered cell holds the animal's half width there in centimetres (bits 0-6, never 0) and whether it is head (bit 7); 0 is air.
/// </summary>
public sealed class AnimalShape
{
    public const byte HeadBit = 0x80;

    public float Cell { get; }
    public float Z0 { get; }
    public int Cols { get; }
    public int Rows { get; }
    readonly byte[] _cells; // row by row from the ground row, each from the rear column
    readonly float _halfWidth;

    /// <summary>
    /// The picture for the hit diagram, a PNG file's bytes over the grid's own bounds (rear on the left, ground at the bottom): the animal standing,
    /// smooth and solid. Only a look's standing (idle) shape has one; null otherwise, and when the file has none (the diagram draws the cells).
    /// </summary>
    public byte[]? Picture { get; init; }

    public AnimalShape(float cell, float z0, int cols, int rows, byte[] cells)
    {
        if (!(cell > 0) || cols < 1 || rows < 1 || cells.Length != cols * rows) throw new DataFileException("animal shape: cells must be cols x rows bytes and the cell positive");
        Cell = cell; Z0 = z0; Cols = cols; Rows = rows; _cells = cells;
        var widest = 0;
        foreach (var c in cells) widest = Math.Max(widest, c & 0x7F);
        _halfWidth = widest / 100f;
    }

    /// <summary>Metres from the rear of the grid to its front, and from the ground to its top.</summary>
    public float Length => Cols * Cell;
    public float Height => Rows * Cell;

    public bool Covered(int col, int row) => At(col, row) != 0;
    public bool IsHead(int col, int row) => (At(col, row) & HeadBit) != 0;
    /// <summary>The animal's half width at a cell, metres (0: air).</summary>
    public float HalfWidth(int col, int row) => (At(col, row) & 0x7F) / 100f;

    /// <summary>
    /// Where a hit on this shape belongs on another shape of the same animal (the standing one the hit diagram draws): a place on the head goes
    /// to the same place of the other's head, a place on the body to the same place of the other's body (each by its share of that part's
    /// bounds), and from there to the nearest cell of that part, so the mark is always on the animal. <paramref name="along"/> and
    /// <paramref name="height"/> are 0..1 of this grid; the result is 0..1 of <paramref name="onto"/>'s.
    /// </summary>
    public (float Along, float Height) MapOnto(AnimalShape onto, float along, float height, bool head)
    {
        if (ReferenceEquals(onto, this)) return (along, height);
        if (!onto.PartBounds(head, out var to)) { head = !head; if (!onto.PartBounds(head, out to)) return (along, height); }
        float u = 0.5f, v = 0.5f;
        if (PartBounds(head, out var from) || PartBounds(!head, out from))
        {
            u = Math.Clamp((along * Cols - from.C0) / (from.C1 - from.C0 + 1), 0f, 1f);
            v = Math.Clamp((height * Rows - from.R0) / (from.R1 - from.R0 + 1), 0f, 1f);
        }
        var col = to.C0 + u * (to.C1 - to.C0 + 1); var row = to.R0 + v * (to.R1 - to.R0 + 1);
        if (!onto.IsPart((int)col, (int)row, head))
        {
            // not on the animal there (the two poses differ): the nearest cell of the part
            var best = float.PositiveInfinity; float bc = col, br = row;
            for (var r = to.R0; r <= to.R1; r++)
                for (var c = to.C0; c <= to.C1; c++)
                {
                    if (!onto.IsPart(c, r, head)) continue;
                    var d = (c + 0.5f - col) * (c + 0.5f - col) + (r + 0.5f - row) * (r + 0.5f - row);
                    if (d < best) { best = d; bc = c + 0.5f; br = r + 0.5f; }
                }
            col = bc; row = br;
        }
        return (col / onto.Cols, row / onto.Rows);
    }

    bool IsPart(int col, int row, bool head) => Covered(col, row) && IsHead(col, row) == head;

    /// <summary>The bounds, in cells, of the head cells or of the other covered cells; false when there are none.</summary>
    bool PartBounds(bool head, out (int C0, int C1, int R0, int R1) b)
    {
        b = (int.MaxValue, -1, int.MaxValue, -1);
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                if (IsPart(c, r, head)) b = (Math.Min(b.C0, c), Math.Max(b.C1, c), Math.Min(b.R0, r), Math.Max(b.R1, r));
        return b.C1 >= 0;
    }

    byte At(int col, int row) => col >= 0 && col < Cols && row >= 0 && row < Rows ? _cells[row * Cols + col] : (byte)0;

    /// <summary>
    /// The first covered cell a ray crosses, or null. The ray is in the animal's frame: x to its right, y up from the ground, z ahead of its
    /// position; <paramref name="dir"/> is a unit vector. It is walked in half-cell steps through the grid's bounds, so a hit is found within
    /// half a cell (2.5 cm) of where the animal's outline is.
    /// </summary>
    public ShapeHit? Ray(Vector3 origin, Vector3 dir, float maxDistance)
    {
        // clip to the bounds of the grid: |x| within the widest cell, y from the ground to the top, z along the grid
        float near = 0, far = maxDistance;
        if (!Clip(origin.X, dir.X, -_halfWidth, _halfWidth, ref near, ref far)) return null;
        if (!Clip(origin.Y, dir.Y, 0, Height, ref near, ref far)) return null;
        if (!Clip(origin.Z, dir.Z, Z0, Z0 + Length, ref near, ref far)) return null;
        var step = Cell / 2;
        for (var t = near; t <= far; t += step)
        {
            var p = origin + dir * t;
            var col = (int)MathF.Floor((p.Z - Z0) / Cell);
            var row = (int)MathF.Floor(p.Y / Cell);
            var cell = At(col, row);
            if (cell != 0 && MathF.Abs(p.X) <= (cell & 0x7F) / 100f) return new ShapeHit(t, (cell & HeadBit) != 0, col, row);
        }
        return null;
    }

    static bool Clip(float o, float d, float min, float max, ref float near, ref float far)
    {
        if (MathF.Abs(d) < 1e-8f) return o >= min && o <= max;
        var a = (min - o) / d; var b = (max - o) / d;
        if (a > b) (a, b) = (b, a);
        near = MathF.Max(near, a); far = MathF.Min(far, b);
        return near <= far;
    }
}

/// <summary>The shapes of one species in one pose: one, or for a clip that moves the body slowly (eating) one every <see cref="Step"/> seconds of it.</summary>
public sealed class AnimalPoseShapes
{
    public required IReadOnlyList<AnimalShape> Frames { get; init; }
    /// <summary>Seconds between two frames and the clip's length (0 with a single frame).</summary>
    public float Step { get; init; }
    public float ClipSeconds { get; init; }

    /// <summary>The frame a client shows <paramref name="seconds"/> into the pose (the clip loops).</summary>
    public int FrameAt(float seconds)
    {
        if (Frames.Count <= 1 || Step <= 0 || ClipSeconds <= 0) return 0;
        var t = seconds % ClipSeconds;
        if (t < 0) t += ClipSeconds;
        return Math.Clamp((int)MathF.Round(t / Step), 0, Frames.Count - 1) % Frames.Count;
    }
}

/// <summary>
/// The hit shapes of the species (animal_shapes.json, baked by <c>scripts/make_animal_shapes.py</c> from the models and their clips), by species
/// id. The free packs' species are in the repository's data folder, the paid packs' in the private asset folder: <see cref="Merge"/> adds
/// them. A species with no shape is tested against its plain body box.
/// </summary>
public sealed class AnimalShapes
{
    readonly Dictionary<string, AnimalPoseShapes?[]> _bySpecies = new();

    public int Count => _bySpecies.Count;

    /// <summary>The shapes of a species in a pose: the pose's own, else the idle ones; null when the species has none.</summary>
    public AnimalPoseShapes? Of(string speciesId, AnimalPose pose) =>
        _bySpecies.TryGetValue(speciesId, out var poses) ? poses[(int)pose] ?? poses[(int)AnimalPose.Idle] : null;

    /// <summary>The shape a client shows for a species <paramref name="seconds"/> into a pose, with its frame number; null when the species has none.</summary>
    public (AnimalShape Shape, int Frame)? At(string speciesId, AnimalPose pose, float seconds)
    {
        if (Of(speciesId, pose) is not { } p) return null;
        var frame = p.FrameAt(seconds);
        return (p.Frames[frame], frame);
    }

    /// <summary>The shape of a species' pose and frame as a message names it (the hit diagram); null when it is not there.</summary>
    public AnimalShape? Frame(string speciesId, AnimalPose pose, int frame) =>
        Of(speciesId, pose) is { } p && frame >= 0 && frame < p.Frames.Count ? p.Frames[frame] : null;

    /// <summary>Adds the species of another file (the private packs'); a species already here keeps its shapes.</summary>
    public AnimalShapes Merge(AnimalShapes other)
    {
        foreach (var (id, poses) in other._bySpecies) _bySpecies.TryAdd(id, poses);
        return this;
    }

    public static AnimalShapes Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static AnimalShapes Parse(string json, string name = "animal_shapes.json")
    {
        var shapes = new AnimalShapes();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.GetInt32() != 1) throw new DataFileException($"{name}: version must be 1");
            var cell = root.GetProperty("cell").GetSingle();
            foreach (var species in root.GetProperty("species").EnumerateObject())
            {
                var poses = new AnimalPoseShapes?[Enum.GetValues<AnimalPose>().Length];
                foreach (var pose in species.Value.EnumerateObject())
                {
                    if (!Enum.TryParse<AnimalPose>(pose.Name, true, out var kind)) continue; // a pose a later version adds
                    var frames = new List<AnimalShape>();
                    foreach (var f in pose.Value.GetProperty("frames").EnumerateArray())
                        frames.Add(new AnimalShape(cell, f.GetProperty("z0").GetSingle(), f.GetProperty("cols").GetInt32(), f.GetProperty("rows").GetInt32(), f.GetProperty("cells").GetBytesFromBase64())
                            { Picture = f.TryGetProperty("picture", out var pic) ? pic.GetBytesFromBase64() : null });
                    if (frames.Count == 0) throw new DataFileException($"{name}: {species.Name}.{pose.Name}: no frames");
                    poses[(int)kind] = new AnimalPoseShapes
                    {
                        Frames = frames,
                        Step = pose.Value.TryGetProperty("step", out var s) ? s.GetSingle() : 0,
                        ClipSeconds = pose.Value.TryGetProperty("length", out var l) ? l.GetSingle() : 0,
                    };
                }
                if (poses[(int)AnimalPose.Idle] == null) throw new DataFileException($"{name}: {species.Name}: no idle shape");
                shapes._bySpecies[species.Name] = poses;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DataFileException($"{name}: {e.Message}");
        }
        return shapes;
    }
}
