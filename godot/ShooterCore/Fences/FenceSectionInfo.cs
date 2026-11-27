using System.Numerics;

namespace Drycastle.ShooterCore.Fences;

/// <summary>A post of a thin section (fences.json "posts"): <see cref="Along"/> metres from A, a square of <see cref="Width"/>, from <see cref="Z0"/> to <see cref="Z1"/> (Godot y), drawn <see cref="Colour"/> (0xRRGGBB); it is hidden and falls with piece <see cref="Piece"/>.</summary>
public readonly record struct FencePost(float Along, float Width, float Z0, float Z1, uint Colour, int Piece);

/// <summary>
/// One section of fences.json (docs/fence-destruction.md Contract 1) in Godot axes, map-root local: A and B are the ground points (x, z = -north), ZA and ZB the ground
/// heights (y) under them. A thin section (<see cref="Thin"/>) is cut into <see cref="Pieces"/> strips along A to B; piece j starts <see cref="Piece0M"/> + j * <see cref="PieceM"/>
/// metres from A. A wall section has no pieces; its grid in house_voxels.bin is <see cref="Grid"/>.
/// </summary>
public sealed record FenceSectionInfo
{
    /// <summary>Gap the box of a section starts below the lower ground point (the pipeline's sink), metres.</summary>
    public const float SinkM = 0.1f;

    public ushort Index { get; init; }
    public FenceKind Kind { get; init; }
    /// <summary>fences.json type (picket, brick_red, ...); null for a plain section.</summary>
    public string? Type { get; init; }
    public Vector2 A { get; init; }
    public Vector2 B { get; init; }
    public float ZA { get; init; }
    public float ZB { get; init; }
    public float HeightM { get; init; }
    public float ThicknessM { get; init; }
    /// <summary>The tint it is drawn with, 0xRRGGBB.</summary>
    public uint Tint { get; init; }
    /// <summary>Thin only: 1..64; 0 for a wall.</summary>
    public int Pieces { get; init; }
    public float PieceM { get; init; }
    public float Piece0M { get; init; }
    /// <summary>Thin only: u at A, u per metre along, v at the bottom, v at the top.</summary>
    public Vector4 Uv { get; init; }
    /// <summary>Thin only: the collision node that holds it ("parcel_&lt;ID&gt;_props_collision" or "props_collision@ix,iy").</summary>
    public string? Collider { get; init; }
    /// <summary>Wall only: its grid id in house_voxels.bin ("fence_00042").</summary>
    public string? Grid { get; init; }
    /// <summary>Thin only: the posts that break with it (each with the piece it stands in), as fences.json "posts".</summary>
    public IReadOnlyList<FencePost> Posts { get; init; } = [];

    public bool Thin => Kind != FenceKind.Wall;
    public float Length => Vector2.Distance(A, B);
    /// <summary>Unit direction from A to B (x axis when A and B coincide).</summary>
    public Vector2 Dir { get { var d = B - A; var l = d.Length(); return l < 1e-6f ? Vector2.UnitX : d / l; } }
    public float Bottom => MathF.Min(ZA, ZB) - SinkM;
    public float Top => MathF.Max(ZA, ZB) + HeightM;

    /// <summary>Metres from A along <see cref="Dir"/> of <paramref name="p"/>'s projection (not clamped).</summary>
    public float AlongOf(Vector2 p) => Vector2.Dot(p - A, Dir);

    /// <summary>Piece <paramref name="j"/>'s along range, clipped to [0, Length].</summary>
    public (float From, float To) PieceSpan(int j)
    {
        var l = Length;
        return (Math.Clamp(Piece0M + j * PieceM, 0, l), Math.Clamp(Piece0M + (j + 1) * PieceM, 0, l));
    }

    /// <summary>The middle of piece <paramref name="j"/> at half its height (Godot axes).</summary>
    public Vector3 PieceCentre(int j)
    {
        var (f, t) = PieceSpan(j);
        var m = (f + t) * 0.5f;
        var p = A + Dir * m;
        return new Vector3(p.X, (Bottom + Top) * 0.5f, p.Y);
    }

    /// <summary>The piece at <paramref name="along"/> metres from A; null outside [0, Length] or for a wall.</summary>
    public int? PieceAt(float along)
    {
        if (Pieces <= 0 || PieceM <= 0 || along < 0 || along > Length) return null;
        return Math.Clamp((int)MathF.Floor((along - Piece0M) / PieceM), 0, Pieces - 1);
    }
}
