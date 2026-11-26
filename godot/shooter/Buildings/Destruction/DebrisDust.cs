using Godot;
using Drycastle.Shooter.Effects;
using Drycastle.ShooterCore.Buildings.Destruction;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// The dust of house debris (WP-G): carved and crumbled cells and the dust group of a split go up as a puff of small grains in the cells' colour, from a fixed pool of
/// one-shot CPU emitters reused round-robin (<see cref="ParticleFx"/>). Cells are gathered into 2 m blocks, one puff a block, a few puffs a call at most. No allocation after
/// the pool is built (the block table is reused).
/// </summary>
public sealed class DebrisDust
{
    const int Pool = 24, Grains = 18, MaxPuffsPerCall = 6, BlockCells = 8;
    static readonly Color Grey = new(0.6f, 0.57f, 0.52f);

    readonly CpuParticles3D[] _emitters = new CpuParticles3D[Pool];
    readonly Dictionary<int, (Vector3 Sum, int Count, Vector3 Min, Vector3 Max, byte Palette)> _blocks = new();
    int _next;

    public DebrisDust(Node3D parent)
    {
        var mesh = new BoxMesh { Size = new Vector3(0.09f, 0.09f, 0.09f), Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f, MetallicSpecular = 0f } };
        var shrink = new Curve();
        shrink.AddPoint(new Vector2(0, 1));
        shrink.AddPoint(new Vector2(1, 0));
        for (var i = 0; i < Pool; i++)
        {
            var p = new CpuParticles3D
            {
                Name = $"DebrisDust{i}", Emitting = false, Visible = false, OneShot = true, Explosiveness = 0.85f, Amount = Grains, Lifetime = 1.4,
                Mesh = mesh, EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, Direction = Vector3.Up, Spread = 180f,
                InitialVelocityMin = 0.4f, InitialVelocityMax = 2.2f, Gravity = new Vector3(0, -5f, 0), DampingMin = 0.5f, DampingMax = 1.5f,
                ScaleAmountMin = 0.5f, ScaleAmountMax = 1.6f, ScaleAmountCurve = shrink, AngularVelocityMin = -180f, AngularVelocityMax = 180f,
                LocalCoords = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            parent.AddChild(p);
            _emitters[i] = p;
        }
    }

    /// <summary>One puff over the box at <paramref name="at"/> (world) of half size <paramref name="half"/>.</summary>
    public void Puff(Vector3 at, Vector3 half, Color colour)
    {
        var p = _emitters[_next];
        _next = (_next + 1) % Pool;
        p.Position = at;
        p.EmissionBoxExtents = half.Clamp(new Vector3(0.1f, 0.1f, 0.1f), new Vector3(1.2f, 1.2f, 1.2f));
        p.Color = colour.Lerp(Grey, 0.35f);
        ParticleFx.Fire(p);
    }

    /// <summary>
    /// Puffs over <paramref name="cells"/> of house <paramref name="g"/>, cells as they sit in the house (world = <paramref name="toWorld"/> of the grid point). Only the blocks
    /// within <paramref name="maxDistance"/> of <paramref name="eye"/> (none: all) puff.
    /// </summary>
    public void Cells(HouseGrid g, ReadOnlySpan<int> cells, Func<System.Numerics.Vector3, Vector3> toWorld, Vector3? eye, float maxDistance)
    {
        if (cells.Length == 0) return;
        _blocks.Clear();
        var pal = g.Cells.Span;
        var bx = (g.Nx + BlockCells - 1) / BlockCells;
        var by = (g.Ny + BlockCells - 1) / BlockCells;
        foreach (var i in cells)
        {
            var (x, y, z) = g.Coords(i);
            var key = x / BlockCells + bx * (y / BlockCells + by * (z / BlockCells));
            var c = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if (_blocks.TryGetValue(key, out var b)) _blocks[key] = (b.Sum + c, b.Count + 1, b.Min.Min(c), b.Max.Max(c), b.Palette);
            else _blocks[key] = (c, 1, c, c, pal[i]);
        }
        var puffs = 0;
        var palette = g.Palette.Span;
        foreach (var (_, b) in _blocks)
        {
            if (puffs >= MaxPuffsPerCall) break;
            var centre = toWorld(DebrisMesh.Nv(b.Sum / b.Count));
            if (eye is { } e && e.DistanceSquaredTo(centre) > maxDistance * maxDistance) continue;
            var half = (b.Max - b.Min + Vector3.One) * (0.5f * g.Cell);
            var entry = palette[b.Palette];
            Puff(centre, new Vector3(half.X, half.Z, half.Y), Color.Color8(entry.R, entry.G, entry.B));
            puffs++;
        }
    }
}
