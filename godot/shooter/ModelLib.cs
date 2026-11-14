using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Loads glTF/GLB models from the asset library (assets/, or DRYCASTLE_ASSETS) once and hands out copies. A path is looked up in the asset library first, then in
/// the private packs (assets-private/, see <see cref="GamePaths.PrivateAssets"/>: the paid Synty packs, never in the repo or the package), so a model may live in
/// either under the same relative path. Null when the file is in neither or the run is headless, so callers keep their primitive fallback (the game runs without the packs).
/// </summary>
public static class ModelLib
{
    static readonly Dictionary<string, Node3D?> Cache = new();

    /// <summary>The folder of the private packs a model path may start with (assets-private/synty/...): when the whole pack is missing that is said once, not per model.</summary>
    const string PrivateFolder = "synty/";
    static bool _noPackLogged;

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

    /// <summary>Absolute path of a file in the asset library (assets/, or DRYCASTLE_ASSETS).</summary>
    public static string AssetPath(string relPath)
    {
        return Path.GetFullPath(Path.Combine(GamePaths.Assets(), relPath));
    }

    /// <summary>Absolute path of a file in the private packs (assets-private/, or DRYCASTLE_ASSETS_PRIVATE).</summary>
    public static string PrivatePath(string relPath)
    {
        return Path.GetFullPath(Path.Combine(GamePaths.PrivateAssets(), relPath));
    }

    /// <summary>The file a relative model path names: in the asset library, else in the private packs, else null.</summary>
    public static string? Find(string relPath)
    {
        var path = AssetPath(relPath);
        if (File.Exists(path)) return path;
        path = PrivatePath(relPath);
        return File.Exists(path) ? path : null;
    }

    static Node3D? Load(string relPath)
    {
        if (Find(relPath) is not { } path)
        {
            if (relPath.StartsWith(PrivateFolder, StringComparison.Ordinal) && !Directory.Exists(PrivatePath(PrivateFolder)))
            {
                if (!_noPackLogged) GD.Print($"private asset packs not installed ({PrivatePath(PrivateFolder)}); their models are drawn as primitives");
                _noPackLogged = true;
            }
            else GD.PrintErr($"model not found: {AssetPath(relPath)} (nor in the private packs); using a primitive");
            return null;
        }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok) { GD.PrintErr($"model failed to load: {path}"); return null; }
        return doc.GenerateScene(state) as Node3D;
    }
}
