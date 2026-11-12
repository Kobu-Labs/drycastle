using System.Text.Json;

namespace Drycastle.LauncherCore;

/// <summary>Launcher preferences (settings.json in the install root). Never throws: a missing or broken file gives defaults.</summary>
public sealed class LauncherSettings
{
    public string Channel { get; set; } = Channels.Stable;
    public bool AutoUpdate { get; set; } = true;
    /// <summary>UI scale in percent (75-150).</summary>
    public int UiScale { get; set; } = 100;

    public static LauncherSettings Load(string root)
    {
        try
        {
            var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(Path.Combine(root, "settings.json"))) ?? new();
            s.UiScale = Math.Clamp(s.UiScale, 75, 150);
            s.Channel = Channels.Normalize(s.Channel);
            return s;
        }
        catch { return new(); }
    }

    public void Save(string root)
    {
        try { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(this)); }
        catch { /* preferences are best effort */ }
    }
}
