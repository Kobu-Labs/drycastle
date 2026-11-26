using Godot;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// The stay-mode rubble of one house (D8, WP-G): every piece that came to rest, frozen into one static mesh (world space, no collision, one draw). Resting bodies wait in
/// <see cref="Pending"/> and are merged together a moment later (<see cref="MergeAfterS"/>), so a collapse that settles over a second rebuilds the mesh a few times, not
/// once a piece.
/// </summary>
public sealed class DebrisRubble
{
    /// <summary>Seconds a resting body waits before it is merged (and freed).</summary>
    public const float MergeAfterS = 0.5f;

    public sealed record Piece(long Id, Vector3[] Vertices, Vector3[] Normals, Color[] Colours, int Cells, float Cell);

    public MeshInstance3D Node { get; }
    public readonly List<Piece> Pieces = new();
    public readonly List<DebrisPiece> Pending = new();
    public Countdown Merge;

    public DebrisRubble(Node3D parent, ushort house)
    {
        Node = new MeshInstance3D { Name = $"Rubble{house}" };
        parent.AddChild(Node);
    }

    /// <summary>Turns a resting body's triangles into world space (its pose now).</summary>
    public static Piece Bake(DebrisPiece p)
    {
        var a = p.Arrays!;
        var xf = p.Root.GlobalTransform;
        var n = a.Vertices.Length;
        var v = new Vector3[n];
        var nn = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            v[i] = xf * a.Vertices[i];
            nn[i] = xf.Basis * a.Normals[i];
        }
        return new Piece(p.Id, v, nn, a.Colours, p.Cells.Length, p.Grid.Cell);
    }

    /// <summary>Rebuilds the mesh from <see cref="Pieces"/>.</summary>
    public void Rebuild()
    {
        var total = 0;
        foreach (var p in Pieces) total += p.Vertices.Length;
        var v = new Vector3[total];
        var n = new Vector3[total];
        var c = new Color[total];
        var k = 0;
        foreach (var p in Pieces)
        {
            p.Vertices.CopyTo(v, k);
            p.Normals.CopyTo(n, k);
            p.Colours.CopyTo(c, k);
            k += p.Vertices.Length;
        }
        Node.Mesh = DebrisMesh.Mesh(v, n, c);
    }
}
