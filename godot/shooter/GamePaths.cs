using Godot;
using System.IO;

namespace Suchohrad.Shooter;

/// <summary>Where the asset library and the map folder live. Dev runs: env vars / flags, else the repo layout. Exported builds: the folders next to the executable, so unzipping the package is all a player does.</summary>
public static class GamePaths
{
    /// <summary>True in an exported build (no editor), false when run from source.</summary>
    public static bool Exported => !OS.HasFeature("editor");

    static string ExeDir => Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";

    /// <summary>Asset library: SUCHOHRAD_ASSETS, else <c>assets/</c> next to the executable (export) or the repo's assets/ (source).</summary>
    public static string Assets()
    {
        var env = OS.GetEnvironment("SUCHOHRAD_ASSETS");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.GetFullPath(Exported ? Path.Combine(ExeDir, "assets") : ProjectSettings.GlobalizePath("res://") + "../../assets");
    }

    /// <summary>Map folder: the explicit value (flag), else SUCHOHRAD_MAP, else <c>map/</c> next to the executable (export) or out/map (source).</summary>
    public static string Map(string? explicitDir)
    {
        if (!string.IsNullOrEmpty(explicitDir)) return explicitDir;
        var env = OS.GetEnvironment("SUCHOHRAD_MAP");
        if (!string.IsNullOrEmpty(env)) return env;
        return Exported ? Path.Combine(ExeDir, "map") : ProjectSettings.GlobalizePath("res://") + "../../out/map";
    }
}
