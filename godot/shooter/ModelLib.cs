using Godot;

namespace Suchohrad.Shooter;

/// <summary>Loads glTF/GLB models from the asset library (assets/, or SUCHOHRAD_ASSETS) once and hands out copies. Null when the file is missing or the run is headless, so callers keep their primitive fallback.</summary>
public static class ModelLib
{
    static readonly Dictionary<string, Node3D?> Cache = new();

    public static Node3D? Instance(string relPath)
    {
        if (!ShotFx.Enabled) return null;
        if (!Cache.TryGetValue(relPath, out var t))
        {
            t = Load(relPath);
            Cache[relPath] = t;
        }
        return t?.Duplicate() as Node3D;
    }

    /// <summary>Parses the model into the cache without copying it, so its first use in a match costs nothing (#170, T2-18).</summary>
    public static void Warm(string relPath)
    {
        if (!ShotFx.Enabled || Cache.ContainsKey(relPath)) return;
        Cache[relPath] = Load(relPath);
    }

    /// <summary>Absolute path of a file in the asset library (assets/, or SUCHOHRAD_ASSETS).</summary>
    public static string AssetPath(string relPath)
    {
        return Path.GetFullPath(Path.Combine(GamePaths.Assets(), relPath));
    }

    static Node3D? Load(string relPath)
    {
        var path = AssetPath(relPath);
        if (!File.Exists(path)) { GD.PrintErr($"model not found: {path}; using a primitive"); return null; }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok) { GD.PrintErr($"model failed to load: {path}"); return null; }
        return doc.GenerateScene(state) as Node3D;
    }
}
