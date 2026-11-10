using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// English strings behind translation keys (standard: English only for now, but screens use keys). Registered once
/// as a Translation; screens call <see cref="T"/> / <see cref="F"/> and never hold literals.
/// </summary>
public static class Strings
{
    static readonly (string Key, string Text)[] Table =
    {
        ("hud.health", "HEALTH"), ("hud.low", "LOW"),
        ("hud.reloading", "RELOADING"), ("hud.ammo.reserve", "/ {0}"), ("hud.ammo.none", "-"),
        ("hud.timer.waiting", "--:--"), ("hud.phase.waiting", "Waiting for players"), ("hud.phase.ending", "Round over"),
        ("hud.score.leading", "You lead: {0}"), ("hud.score.behind", "You {0}   Leader {1}: {2}"),
        ("hud.protection", "Spawn protection {0}"),
        ("hud.respawn.title", "You were eliminated"), ("hud.respawn.by", "by {0}"), ("hud.respawn.self", "by accident"),
        ("hud.respawn.in", "Respawning in {0}"),
        ("hud.hint.captured", "Tab  Scoreboard     1-4  Weapons     Esc  Release mouse"),
        ("hud.hint.released", "Click to capture the mouse and resume"),
        ("hud.vehicle.speed", "{0} km/h"),
        ("score.title", "Scoreboard"), ("score.ending", "Round over: {0} wins"), ("score.ending.tie", "Round over: {0} win"),
        ("score.col.player", "Player"), ("score.col.kills", "K"), ("score.col.deaths", "D"), ("score.col.ping", "Ping"),
        ("score.you", "{0} (you)"), ("score.hint.hold", "Hold Tab to show this"), ("score.hint.next", "A new round starts soon"),
        ("score.ping", "{0} ms"),
        ("weapon.pistol", "Pistol"), ("weapon.ak47", "AK-47"), ("weapon.bazooka", "Bazooka"), ("weapon.knife", "Knife"),
        ("weapon.fall", "Fall"), ("weapon.void", "Void"), ("weapon.debug", "Debug"), ("weapon.unknown", "?"),
        ("player.unknown", "Player {0}"),
    };

    static bool _done;

    public static void Register()
    {
        if (_done) return;
        _done = true;
        var tr = new Translation { Locale = "en" };
        foreach (var (k, v) in Table) tr.AddMessage(k, v);
        TranslationServer.AddTranslation(tr);
        TranslationServer.SetLocale("en");
    }

    public static string T(string key) => TranslationServer.Translate(key);
    public static string F(string key, params object[] a) => string.Format(T(key), a);
}
