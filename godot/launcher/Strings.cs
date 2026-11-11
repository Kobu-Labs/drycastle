namespace Suchohrad.Launcher;

/// <summary>All launcher text by key (English only for now; keys keep the door open for translation).</summary>
public static class S
{
    static readonly Dictionary<string, string> En = new()
    {
        ["title"] = "SUCHOHRAD",
        ["checking"] = "Checking for updates...",
        ["uptodate"] = "Up to date",
        ["update_available"] = "Update available: {0}",
        ["installed"] = "Installed: {0}",
        ["not_installed"] = "Not installed yet",
        ["play"] = "Play",
        ["update"] = "Update",
        ["install"] = "Install",
        ["retry"] = "Try again",
        ["cancel"] = "Cancel",
        ["settings"] = "Settings",
        ["quit"] = "Quit",
        ["back"] = "Back",
        ["dl_game"] = "Downloading game  {0} / {1}",
        ["dl_map"] = "Downloading map  {0} / {1}",
        ["installing"] = "Installing...",
        ["cancelled"] = "Update cancelled. Your installed version is untouched.",
        ["offline_installed"] = "Can't reach the update server. You can still play the installed version.",
        ["offline_fresh"] = "Can't reach the update server, and nothing is installed yet. Check your internet connection, then try again.",
        ["no_release"] = "No release is published on the {0} channel yet.",
        ["failed"] = "Update failed: {0}",
        ["failed_hint"] = "Your installed version is untouched. Try again, or play it as is.",
        ["launch_failed"] = "Could not start the game: {0}",
        ["apply"] = "Apply",
        ["revert"] = "Revert",
        ["unsaved"] = "Unsaved changes",
        ["unsaved_discard"] = "Unsaved changes: press Back or Esc again to discard them",
        ["set_channel"] = "Update channel",
        ["channel_stable"] = "Stable (recommended)",
        ["channel_dev"] = "Dev (every change, may break)",
        ["set_auto"] = "Download updates automatically",
        ["set_scale"] = "UI scale",
        ["hint_main"] = "Enter: select    Tab / arrows: move focus",
        ["hint_settings"] = "Esc: back    Tab / arrows: move focus",
        ["hint_busy"] = "Esc: cancel the download",
        ["protocol"] = "Network protocol {0}",
    };

    public static string T(string key, params object[] args) =>
        En.TryGetValue(key, out var s) ? (args.Length == 0 ? s : string.Format(s, args)) : key;
}
