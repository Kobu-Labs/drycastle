using Godot;

namespace Suchohrad.Shooter;

/// <summary>English strings of sandbox mode (#156). Registered with <see cref="Strings.Register"/>; screens use the keys.</summary>
public static class SandboxStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("bind.sandbox_panel", "Sandbox panel"),
        ("sandbox.tag", "[SANDBOX]"),
        ("sandbox.badge", "SANDBOX"),
        ("sandbox.held", "Held: {0}"), ("sandbox.held.none", "Nothing held"),
        ("sandbox.notice.off", "Sandbox mode is off on this server."),
        ("sandbox.panel.title", "Sandbox"),
        ("sandbox.panel.off", "Sandbox mode is off. Start the client with --sandbox against a server started with --allow-sandbox."),
        ("sandbox.panel.hint", "Pick a killstreak to hold it. It stays held until you pick another, so you can use it again and again. Esc closes."),
        ("sandbox.panel.killstreaks", "Killstreaks"), ("sandbox.panel.actions", "Actions"),
        ("sandbox.panel.held_mark", "HELD"), ("sandbox.panel.use", "Use now"),
        ("sandbox.panel.heal", "Heal and refill"), ("sandbox.panel.teleport", "Teleport to a spawn"),
        ("sandbox.panel.vehicles", "Spawn a vehicle in front"),
        ("sandbox.panel.respawn_on", "Target dummies respawn: on"), ("sandbox.panel.respawn_off", "Target dummies respawn: off"),
        ("sandbox.vehicle.car", "Car"), ("sandbox.vehicle.jeep", "Jeep"), ("sandbox.vehicle.quad", "Quad bike"),
        ("sandbox.vehicle.boat", "Boat"), ("sandbox.vehicle.amphibious", "Amphibious"), ("sandbox.vehicle.flak", "Flak truck"), ("sandbox.vehicle.bus", "Bus"),
        ("sandbox.vehicle.utility_helicopter", "Utility helicopter"),
        ("sandbox.connect", "Sandbox mode"),
        ("sandbox.connect.hint", "Asks a dev server for godmode, unlimited ammo and every killstreak. No XP, stats or score."),
        ("connect.advanced", "Advanced (dev builds and --dev-ui)"),
    };

    /// <summary>The display name of a killstreak id (its streak.* string, or the id when none).</summary>
    public static string StreakName(string id)
    {
        var k = "streak." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }
}

/// <summary>Whether the dev-only UI is shown: debug builds and the <c>--dev-ui</c> flag (the Advanced section of the connect screen).</summary>
public static class DevUi
{
    public static bool Enabled { get; set; }
}
