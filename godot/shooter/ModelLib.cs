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

    /// <summary>Absolute path of a file in the asset library (assets/, or SUCHOHRAD_ASSETS).</summary>
    public static string AssetPath(string relPath)
    {
        var env = OS.GetEnvironment("SUCHOHRAD_ASSETS");
        var dir = string.IsNullOrEmpty(env) ? ProjectSettings.GlobalizePath("res://") + "../../assets" : env;
        return Path.GetFullPath(Path.Combine(dir, relPath));
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
