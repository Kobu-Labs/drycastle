using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// The shaders of the tree cut (#287): near the focus the baked trees (props meshes, UV2 = base of the tree) give way to the pack trees, in the shader,
/// so no mesh is ever cut at runtime. The focus and radius are one global shader parameter, <c>tree_cut</c>, set by the streamer every frame (the light's
/// camera reads the same values in the shadow pass). The shaders are embedded resources of this assembly, like the water's; <c>tree_cut.gdshaderinc</c>
/// is spliced in at the <c>// @include tree_cut</c> line (a Shader made from code has no include path).
/// </summary>
static class TreeCutShaders
{
    const string Param = "tree_cut", UnderParam = "tree_cut_under";
    static readonly Vector4 Off = new(0, 0, -1, 0);
    static bool _added;

    /// <summary>Declares the global parameter (once per process); every shader that uses it must be made after this.</summary>
    static void EnsureGlobal()
    {
        if (_added) return;
        _added = true;
        // no project declares it in its settings (and listing the globals is an editor-only call that logs an error in the game)
        RenderingServer.GlobalShaderParameterAdd(Param, RenderingServer.GlobalShaderParameterType.Vec4, Off);
        RenderingServer.GlobalShaderParameterAdd(UnderParam, RenderingServer.GlobalShaderParameterType.Vec4, Off);
    }

    static string ReadEmbedded(string resource)
    {
        var asm = typeof(TreeCutShaders).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n == resource)
                   ?? throw new FileNotFoundException($"embedded shader {resource} is missing from {asm.GetName().Name}");
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    /// <summary>The shader <paramref name="resource"/> with the shared code in; <paramref name="cull"/> (cull_back, cull_front, cull_disabled) replaces CULL_MODE where the shader has it.</summary>
    public static Shader Load(string resource, string cull = "cull_back")
    {
        EnsureGlobal();
        var code = ReadEmbedded(resource).Replace("// @include tree_cut", ReadEmbedded("tree_cut.gdshaderinc")).Replace("CULL_MODE", cull);
        return new Shader { Code = code };
    }

    /// <summary>
    /// Sets the cut for this frame: the focus (Godot axes), the radius inside which pack trees stand, and the fade band within it; and the same for the undergrowth,
    /// which has no baked twin and so only needs its own, shorter radius.
    /// </summary>
    public static void Set(Vector3 focus, float radius, float band, float underRadius, float underBand)
    {
        if (!_added) return;
        RenderingServer.GlobalShaderParameterSet(Param, new Vector4(focus.X, focus.Z, radius, band));
        RenderingServer.GlobalShaderParameterSet(UnderParam, new Vector4(focus.X, focus.Z, underRadius, underBand));
    }

    /// <summary>No cut: every baked tree is drawn, every pack tree and all undergrowth dropped.</summary>
    public static void Disable()
    {
        if (!_added) return;
        RenderingServer.GlobalShaderParameterSet(Param, Off);
        RenderingServer.GlobalShaderParameterSet(UnderParam, Off);
    }
}
