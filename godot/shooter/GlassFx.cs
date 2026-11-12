using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Client visuals of a glass pane (ADR 0007, docs/glass.md). A pane that is not whole gets an overlay quad just in front of it
/// (glass_crack.gdshader): cracks that reach further with each crack stage, and a dark opening with shards along the edge once it has
/// shattered. The moment it shatters, the pane's rectangle is cut into fragments (<see cref="GlassShards"/>) that fly out of it as one mesh
/// (glass_shards.gdshader), freed when they are gone. A whole pane has no node here at all. Callers skip headless runs (ShotFx.Enabled).
/// </summary>
public static class GlassFx
{
    public const string CrackShaderPath = "res://shaders/glass_crack.gdshader";
    public const string ShardShaderPath = "res://shaders/glass_shards.gdshader";
    /// <summary>How far in front of the pane the overlay sits, metres (clear of the pane's own surface, no z-fighting).</summary>
    const float Lift = 0.015f;

    static Shader? _crack, _shards;
    static int _active;

    static Vector3 G(NVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The pane's frame in its parent's space: x along the width, y up the pane, z out of it, the origin <paramref name="lift"/> metres in front of its centre.</summary>
    public static Transform3D Frame(GlassPaneRect r, float lift = 0) => new(new Basis(G(r.Right), G(r.Up), G(r.Normal)), G(r.Centre + r.Normal * lift));

    /// <summary>A number that differs between panes, for the crack pattern and the cut of the fragments.</summary>
    public static float Seed(GlassPaneKey key) => (key.Owner * 7919 + key.Index * 104729) % 9973 / 97f;

    /// <summary>The overlay of one pane under <paramref name="parent"/> (the vehicle's node, or a world node for a building's pane). <paramref name="holeDarkness"/> is how opaque the opening of the shattered pane is.</summary>
    public static MeshInstance3D MakeOverlay(Node3D parent, GlassPaneRect rect, GlassPaneKey key, float holeDarkness)
    {
        var mat = new ShaderMaterial { Shader = _crack ??= GD.Load<Shader>(CrackShaderPath) };
        mat.SetShaderParameter("pane_size", new Vector2(rect.Width, rect.Height));
        mat.SetShaderParameter("seed", Seed(key));
        mat.SetShaderParameter("hole_darkness", holeDarkness);
        var mi = new MeshInstance3D
        {
            Name = $"Glass_{key.Owner}_{key.Index}",
            Mesh = new QuadMesh { Size = new Vector2(rect.Width, rect.Height) },
            MaterialOverride = mat,
            Transform = Frame(rect, Lift),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(mi);
        return mi;
    }

    /// <summary>Shows a standing pane's crack stage (1..<paramref name="maxStage"/>) or, when <paramref name="shattered"/>, the opening.</summary>
    public static void SetOverlay(MeshInstance3D overlay, int crackStage, int maxStage, bool shattered)
    {
        if (overlay.MaterialOverride is not ShaderMaterial mat) return;
        mat.SetShaderParameter("stage", (float)crackStage);
        mat.SetShaderParameter("stages", (float)maxStage);
        mat.SetShaderParameter("shattered", shattered ? 1f : 0f);
    }

    /// <summary>
    /// The shatter animation: the fragments of the pane fly out of it under <paramref name="world"/>, from where the pane is now
    /// (<paramref name="paneParent"/> is the global transform of the node the rectangle is in). Skipped beyond the effect range of
    /// glass.json and when too many run already (the pane still changes to its shattered look).
    /// </summary>
    public static void Shatter(Node3D world, Transform3D paneParent, GlassPaneRect rect, GlassConfig cfg, GlassPaneKey key)
    {
        if (_active >= cfg.MaxShatterEffects) return;
        var frame = paneParent * Frame(rect, Lift);
        if (Sfx.ListenerDistance(frame.Origin) > cfg.ShatterEffectRange) return;

        var shards = GlassShards.Cut(rect.Width, rect.Height, cfg.Shards, (uint)(key.Owner * 7919 + key.Index * 104729));
        var verts = new Vector3[shards.Count * 3];
        var normals = new Vector3[shards.Count * 3];
        var centres = new Vector2[shards.Count * 3];
        for (var i = 0; i < shards.Count; i++)
        {
            var s = shards[i];
            var c = new Vector2(s.Centre.X, s.Centre.Y);
            verts[i * 3] = new Vector3(s.A.X, s.A.Y, 0); verts[i * 3 + 1] = new Vector3(s.B.X, s.B.Y, 0); verts[i * 3 + 2] = new Vector3(s.C.X, s.C.Y, 0);
            for (var k = 0; k < 3; k++) { normals[i * 3 + k] = Vector3.Back; centres[i * 3 + k] = c; }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV2] = centres;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        var mat = new ShaderMaterial { Shader = _shards ??= GD.Load<Shader>(ShardShaderPath) };
        mat.SetShaderParameter("pane_size", new Vector2(rect.Width, rect.Height));
        mat.SetShaderParameter("seed", Seed(key));
        mat.SetShaderParameter("speed", cfg.ShardSpeed);
        mat.SetShaderParameter("life", cfg.ShardLife);
        mat.SetShaderParameter("t", 0f);
        var mi = new MeshInstance3D
        {
            Name = "GlassShards",
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the shader moves the fragments out of the mesh's own bounds: keep it drawn while they fly
            ExtraCullMargin = cfg.ShardSpeed * cfg.ShardLife * 2f + 5f * cfg.ShardLife * cfg.ShardLife + 1f,
        };
        world.AddChild(mi);
        mi.GlobalTransform = frame;
        _active++;
        mi.TreeExiting += () => _active = Math.Max(0, _active - 1); // also when the client leaves mid-flight
        var tw = mi.CreateTween();
        tw.TweenMethod(Callable.From<float>(v => mat.SetShaderParameter("t", v)), 0f, cfg.ShardLife, cfg.ShardLife);
        tw.TweenCallback(Callable.From(mi.QueueFree));
    }
}
