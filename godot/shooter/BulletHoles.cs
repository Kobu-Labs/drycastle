using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// Bullet holes: a pool of 64 projective Decal nodes reused oldest-first (nothing is allocated per shot after init). A hole stays fully visible for 15 s, then fades over 5 s.
/// The texture is generated once. Skipped on headless runs (ShotFx.Enabled).
/// </summary>
public partial class BulletHoles : Node3D
{
    const int Count = 64;
    const float Hold = 15f, Fade = 5f, HoleSize = 0.12f;

    static BulletHoles? _inst;
    static ImageTexture? _tex;

    readonly Decal[] _decals = new Decal[Count];
    readonly double[] _age = new double[Count]; // < 0: unused
    int _next;

    static ImageTexture Texture()
    {
        if (_tex != null) return _tex;
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        var rng = new RandomNumberGenerator { Seed = 1234 };
        var ph = new float[3]; for (var i = 0; i < 3; i++) ph[i] = rng.Randf() * 6.28f;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                var a = Mathf.Atan2(dy, dx);
                var edge = 0.8f + 0.1f * Mathf.Sin(a * 5 + ph[0]) + 0.06f * Mathf.Sin(a * 9 + ph[1]) + 0.04f * Mathf.Sin(a * 17 + ph[2]); // ragged outline
                var core = 0.34f * edge;
                float lum, alpha;
                if (r < core) { lum = 0.03f; alpha = 0.96f; }                                         // dark centre
                else
                {
                    var t = Mathf.Clamp((r - core) / (edge - core), 0f, 1f);
                    lum = Mathf.Lerp(0.03f, 0.2f, Mathf.Min(1f, t * 1.6f));                           // scorch ring a touch lighter
                    alpha = (1f - t * t) * Mathf.Lerp(0.95f, 0.45f, t);                               // soft edge
                }
                if (r >= edge) alpha = 0;
                img.SetPixel(x, y, new Color(lum, lum * 0.95f, lum * 0.9f, alpha));
            }
        return _tex = ImageTexture.CreateFromImage(img);
    }

    static BulletHoles? Get(Node3D world)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return null;
        if (_inst != null && GodotObject.IsInstanceValid(_inst) && _inst.IsInsideTree() && _inst.GetParent() == world) return _inst;
        var h = new BulletHoles { Name = "BulletHoles", TopLevel = true };
        var tex = Texture();
        for (var i = 0; i < Count; i++)
        {
            var d = new Decal
            {
                Size = new Vector3(HoleSize, 0.2f, HoleSize), TextureAlbedo = tex, Visible = false,
                DistanceFadeEnabled = true, DistanceFadeBegin = 60f, DistanceFadeLength = 20f,
                CullMask = 1, UpperFade = 0.3f, LowerFade = 0.3f,
            };
            h._decals[i] = d; h._age[i] = -1; h.AddChild(d);
        }
        world.AddChild(h);
        return _inst = h;
    }

    /// <summary>Places a hole at <paramref name="at"/> on a surface with outward <paramref name="normal"/>.</summary>
    public static void Place(Node3D world, Vector3 at, Vector3 normal)
    {
        var h = Get(world);
        if (h == null || normal.LengthSquared() < 0.5f) return;
        normal = normal.Normalized();
        var d = h._decals[h._next]; h._age[h._next] = 0; h._next = (h._next + 1) % Count;
        // a Decal projects along its local -Y, so local +Y is the surface normal (the decal sits just above the surface, its box straddles it)
        var side = Mathf.Abs(normal.Y) > 0.99f ? Vector3.Right : Vector3.Up.Cross(normal).Normalized();
        var fwd = side.Cross(normal).Normalized();
        var basis = new Basis(side, normal, fwd).Rotated(normal, GD.Randf() * Mathf.Tau);
        var s = 0.85f + GD.Randf() * 0.35f;
        d.GlobalTransform = new Transform3D(basis.Scaled(new Vector3(s, 1, s)), at + normal * 0.02f);
        d.Modulate = Colors.White;
        d.Visible = true;
    }

    /// <summary>Raycasts the world (mask 1) from <paramref name="from"/> past <paramref name="end"/>; a hit within 0.15 m of <paramref name="end"/> is a world hit and gets a hole.</summary>
    public static void PlaceFromShot(Node3D world, Vector3 from, Vector3 end)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var dir = end - from;
        if (dir.LengthSquared() < 0.01f) return;
        var q = PhysicsRayQueryParameters3D.Create(from, end + dir.Normalized() * 0.1f, 1);
        var hit = world.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count == 0) return;
        var p = hit["position"].AsVector3();
        if (p.DistanceTo(end) > 0.15f) return;
        Place(world, p, hit["normal"].AsVector3());
    }

    public override void _Process(double delta)
    {
        for (var i = 0; i < Count; i++)
        {
            if (_age[i] < 0) continue;
            var a = _age[i] += delta;
            if (a > Hold + Fade) { _decals[i].Visible = false; _age[i] = -1; }
            else if (a > Hold) _decals[i].Modulate = new Color(1, 1, 1, 1f - (float)((a - Hold) / Fade));
        }
    }
}
