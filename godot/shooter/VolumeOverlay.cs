using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>Which bounding volumes F3 shows (#479): one type, or all. [ and ] step through them.</summary>
public enum VolumeMode { Hit = 0, Body = 1, Static = 2, All = 3 }

/// <summary>The three kinds of volume (GLOSSARY: Hit shape, Body shape; Static colliders): each has its own colour and text tag.</summary>
public enum VolumeKind { Hit = 0, Body = 1, Static = 2 }

/// <summary>
/// The F3 debug overlay (#479): wireframes of what a shot, a step or a bump is tested against, one colour and one text tag per kind (never colour alone).
/// Built when F3 turns it on and freed when it turns off; nothing exists while off. While on, <see cref="Begin"/> / the draw calls / <see cref="End"/> rebuild
/// one line mesh and a small pool of tags each frame. The caller (NetClient.Volumes.cs) says what to draw; this class only draws.
/// </summary>
public partial class VolumeOverlay : Node3D
{
    public static readonly Color[] Colours = { new(1f, 0.28f, 0.28f), new(0.3f, 0.85f, 1f), new(1f, 0.85f, 0.25f) };
    public static readonly string[] Tags = { "HIT", "BODY", "STATIC" };
    const int MaxTags = 40;

    public VolumeMode Mode { get; private set; } = VolumeMode.All;

    readonly ImmediateMesh _mesh = new();
    readonly List<Label3D> _labels = new();
    int _labelsUsed;
    bool _open;
    Vector3 _camAt;

    public override void _Ready()
    {
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true, Transparency = BaseMaterial3D.TransparencyEnum.Disabled };
        AddChild(new MeshInstance3D { Mesh = _mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f, TopLevel = true });
    }

    public void Step(int by) => Mode = (VolumeMode)(((int)Mode + by + 4) % 4);

    /// <summary>The text of the mode line: "Volumes: Hit shape | Body shape | Static | ALL" with the shown one in brackets.</summary>
    public string ModeText()
    {
        string[] names = { Strings.T("volumes.hit"), Strings.T("volumes.body"), Strings.T("volumes.static"), Strings.T("volumes.all") };
        var parts = names.Select((n, i) => i == (int)Mode ? $"[{n}]" : n);
        return Strings.F("volumes.line", string.Join("  ", parts));
    }

    public bool Wants(VolumeKind k) => Mode == VolumeMode.All || (int)Mode == (int)k;

    public void Begin(Vector3 camera)
    {
        _camAt = camera; _labelsUsed = 0;
        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        _open = true;
    }

    public void End()
    {
        if (!_open) return;
        _open = false;
        _mesh.SurfaceEnd();
        for (var i = _labelsUsed; i < _labels.Count; i++) _labels[i].Visible = false;
    }

    void Line(Color c, Vector3 a, Vector3 b)
    {
        _mesh.SurfaceSetColor(c); _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceSetColor(c); _mesh.SurfaceAddVertex(b);
    }

    void Tag(VolumeKind k, Vector3 at, string name)
    {
        if (_labelsUsed >= MaxTags || at.DistanceTo(_camAt) > 45f) return;
        if (_labelsUsed == _labels.Count)
        {
            var l = new Label3D { Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true, PixelSize = 0.0009f, FontSize = 36, OutlineSize = 10, Shaded = false, TopLevel = true };
            AddChild(l); _labels.Add(l);
        }
        var lab = _labels[_labelsUsed++];
        lab.Visible = true; lab.GlobalPosition = at;
        lab.Modulate = Colours[(int)k]; lab.Text = name == "" ? Tags[(int)k] : $"{Tags[(int)k]} {name}";
    }

    /// <summary>A box of full <paramref name="size"/> in the frame <paramref name="xf"/> (its origin the box centre).</summary>
    public void Box(VolumeKind k, Transform3D xf, Vector3 size, string name = "", bool tag = true)
    {
        if (!Wants(k)) return;
        var h = size / 2; var c = Colours[(int)k];
        var p = new Vector3[8];
        for (var i = 0; i < 8; i++) p[i] = xf * new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) }) Line(c, p[a], p[b]);
        if (tag) Tag(k, xf * new Vector3(0, h.Y, 0), name);
    }

    /// <summary>A ring of <paramref name="radius"/> about <paramref name="axis"/> (a unit vector) through <paramref name="centre"/>.</summary>
    void Ring(Color c, Vector3 centre, Vector3 axis, float radius)
    {
        var u = axis.Cross(MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized(); var v = axis.Cross(u);
        const int n = 20;
        var prev = centre + u * radius;
        for (var i = 1; i <= n; i++)
        {
            var a = i * Mathf.Tau / n;
            var p = centre + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            Line(c, prev, p); prev = p;
        }
    }

    public void Sphere(VolumeKind k, Vector3 centre, float radius, string name = "", bool tag = true)
    {
        if (!Wants(k)) return;
        var c = Colours[(int)k];
        Ring(c, centre, Vector3.Up, radius); Ring(c, centre, Vector3.Right, radius); Ring(c, centre, Vector3.Back, radius);
        if (tag) Tag(k, centre + Vector3.Up * radius, name);
    }

    /// <summary>A capsule: the segment <paramref name="a"/> to <paramref name="b"/> (the sphere centres) and the radius.</summary>
    public void Capsule(VolumeKind k, Vector3 a, Vector3 b, float radius, string name = "", bool tag = true)
    {
        if (!Wants(k)) return;
        var c = Colours[(int)k];
        var axis = (b - a); var len = axis.Length();
        axis = len > 1e-4f ? axis / len : Vector3.Up;
        Ring(c, a, axis, radius); Ring(c, b, axis, radius);
        var u = axis.Cross(MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized(); var v = axis.Cross(u);
        foreach (var d in new[] { u, -u, v, -v }) Line(c, a + d * radius, b + d * radius);
        // the end caps as two crossing half rings each
        foreach (var (end, sign) in new[] { (a, -1f), (b, 1f) })
            foreach (var d in new[] { u, v })
            {
                var prev = end + d * radius;
                for (var i = 1; i <= 8; i++)
                {
                    var t = i * Mathf.Pi / 16;
                    var p = end + d * (radius * MathF.Cos(t)) + axis * (sign * radius * MathF.Sin(t));
                    Line(c, prev, p); prev = p;
                }
                prev = end - d * radius;
                for (var i = 1; i <= 8; i++)
                {
                    var t = i * Mathf.Pi / 16;
                    var p = end - d * (radius * MathF.Cos(t)) + axis * (sign * radius * MathF.Sin(t));
                    Line(c, prev, p); prev = p;
                }
            }
        if (tag) Tag(k, (a.Y > b.Y ? a : b) + Vector3.Up * radius, name);
    }

    /// <summary>A flat circle (a rotor disc): <paramref name="centre"/>, a unit <paramref name="axis"/>, radius.</summary>
    public void Disc(VolumeKind k, Vector3 centre, Vector3 axis, float radius, string name = "")
    {
        if (!Wants(k)) return;
        Ring(Colours[(int)k], centre, axis, radius);
        Tag(k, centre, name);
    }

    /// <summary>A cylinder of <paramref name="height"/> standing on <paramref name="xf"/>'s origin's local Y (centre).</summary>
    public void Cylinder(VolumeKind k, Transform3D xf, float radius, float height, string name = "")
    {
        if (!Wants(k)) return;
        var c = Colours[(int)k]; var up = xf.Basis.Y.Normalized();
        var top = xf.Origin + up * (height / 2); var bot = xf.Origin - up * (height / 2);
        Ring(c, top, up, radius); Ring(c, bot, up, radius);
        var u = up.Cross(MathF.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized(); var v = up.Cross(u);
        foreach (var d in new[] { u, -u, v, -v }) Line(c, bot + d * radius, top + d * radius);
        Tag(k, top, name);
    }

    /// <summary>
    /// An animal's Hit shape (<see cref="AnimalShape"/>) as one prism per run of covered cells in a row, in the frame <paramref name="feet"/> / yaw / scale:
    /// x to its right, y up, z ahead (Godot -Z at yaw 0). Head cells are the same colour: an animal has no headshot damage.
    /// </summary>
    public void Outline(VolumeKind k, AnimalShape s, Vector3 feet, float yaw, float scale, string name)
    {
        if (!Wants(k)) return;
        var c = Colours[(int)k];
        var basis = new Basis(Vector3.Up, yaw);
        Vector3 At(float x, float y, float z) => feet + basis * new Vector3(x * scale, y * scale, -z * scale);
        for (var row = 0; row < s.Rows; row++)
        {
            var col = 0;
            while (col < s.Cols)
            {
                if (!s.Covered(col, row)) { col++; continue; }
                var start = col; var hw = 0f;
                while (col < s.Cols && s.Covered(col, row)) { hw = MathF.Max(hw, s.HalfWidth(col, row)); col++; }
                float z0 = s.Z0 + start * s.Cell, z1 = s.Z0 + col * s.Cell, y0 = row * s.Cell, y1 = (row + 1) * s.Cell;
                var p = new Vector3[8];
                for (var i = 0; i < 8; i++) p[i] = At((i & 1) == 0 ? -hw : hw, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
                foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) }) Line(c, p[a], p[b]);
            }
        }
        var bounds = s.Bounds;
        Tag(k, At(0, bounds.Top, (bounds.Z0 + bounds.Z1) / 2), name);
    }
}
