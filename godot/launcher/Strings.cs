namespace Drycastle.Launcher;

/// <summary>All launcher text by key (English only for now; keys keep the door open for translation).</summary>
public static class S
{
    static readonly Dictionary<string, string> En = new()
    {
        ["title"] = "DRYCASTLE",
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
        ["dl_pack"] = "Downloading private assets  {0} / {1}",
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
        ["set_pack"] = "Asset pack password",
        ["set_pack_placeholder"] = "From the host; empty = play without the private assets",
        ["set_pack_show"] = "Show the password",
        ["pack_installed"] = "Private assets: installed",
        ["pack_locked"] = "Private assets: locked. Enter the asset pack password in Settings. The game runs without them, with placeholder models and no voices.",
        ["pack_wrong"] = "The game is ready, but the asset pack password is wrong.",
        ["pack_failed"] = "The game is ready, but the private assets could not be downloaded: {0}",
        ["pack_hint"] = "Check the password in Settings, or try again. You can play without the private assets.",
        ["hint_main"] = "Enter: select    Tab / arrows: move focus",
        ["hint_settings"] = "Esc: back    Tab / arrows: move focus",
        ["hint_busy"] = "Esc: cancel the download",
        ["hint_installing"] = "Installing the update, this can't be cancelled. It only takes a moment.",
        ["protocol"] = "Network protocol {0}",
    };

    public static string T(string key, params object[] args) =>
        En.TryGetValue(key, out var s) ? (args.Length == 0 ? s : string.Format(s, args)) : key;
}
