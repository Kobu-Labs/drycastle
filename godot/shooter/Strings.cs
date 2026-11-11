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
        ("hud.health", "HEALTH"), ("hud.low", "LOW"), ("hud.air", "AIR"),
        ("hud.stance.stand", "Standing"), ("hud.stance.crouch", "Crouched"), ("hud.stance.prone", "Prone"),
        ("hud.reloading", "RELOADING"), ("hud.ammo.reserve", "/ {0}"), ("hud.ammo.none", "-"),
        ("hud.timer.waiting", "--:--"), ("hud.phase.waiting", "Waiting for players"), ("hud.phase.ending", "Round over"),
        ("hud.score.leading", "You lead: {0}"), ("hud.score.behind", "You {0}   Leader {1}: {2}"),
        ("hud.protection", "Spawn protection {0}"),
        ("hud.streak.title", "KILLSTREAK"), ("hud.streak.none", "Empty slot"), ("hud.streak.need", "{0} more kills"), ("hud.streak.ready", "READY  press {0}"),
        ("hud.streak.ready.dead", "READY"), ("hud.streak.active", "ACTIVE"), ("hud.streak.spent", "USED"),
        ("hud.uav.title", "UAV"), ("hud.uav.time", "{0}:{1:00} LEFT"),
        ("hud.respawn.title", "You were eliminated"), ("hud.respawn.by", "by {0}"), ("hud.respawn.self", "by accident"),
        ("hud.respawn.in", "Respawning in {0}"),
        ("hud.hint.captured", "{0}  Scoreboard     {1}-{2}  Weapons     Esc  Pause"),
        ("hud.hint.released", "Mouse released. Close the menu to resume"),
        ("hud.vehicle.speed", "{0} km/h"), ("hud.vehicle.enter", "{0}  Enter car"), ("hud.vehicle.exit", "{0}  Exit car     Space  Handbrake"),
        ("score.title", "Scoreboard"), ("score.ending", "Round over: {0} wins"), ("score.ending.tie", "Round over: {0} win"),
        ("score.col.player", "Player"), ("score.col.kills", "K"), ("score.col.deaths", "D"), ("score.col.ping", "Ping"),
        ("score.you", "{0} (you)"), ("score.hint.hold", "Hold Tab to show this"), ("score.hint.next", "A new round starts soon"),
        ("score.ping", "{0} ms"),
        ("weapon.pistol", "M1911"), ("weapon.ak47", "AK-47"), ("weapon.bazooka", "Bazooka"), ("weapon.knife", "Knife"), ("weapon.sniper", "Sniper"), ("weapon.glock17", "Glock 17"), ("weapon.deagle", "Desert Eagle"), ("weapon.m82", "Barrett M82"), ("weapon.awm", "AWM"), ("weapon.svd", "Dragunov SVD"),
        ("hud.breath.ready", "Breath: hold {0}"), ("hud.breath.held", "Breath held"), ("hud.breath.exhaled", "Breath out, steadying"), ("hud.breath.empty", "Breath empty, refilling"),
        ("weapon.fall", "Fall"), ("weapon.drown", "Drowned"), ("weapon.void", "Void"), ("weapon.debug", "Debug"), ("weapon.vehicle", "Car"), ("weapon.vehicle_gun", "Jeep MG"), ("weapon.moab", "MOAB"), ("moab.inbound", "MOAB inbound {0}"), ("moab.blast", "MOAB by {0}"), ("moab.cancelled", "MOAB cancelled"), ("weapon.turret", "Turret"), ("feed.roadkill", "Roadkill"), ("weapon.unknown", "?"),
        ("player.unknown", "Player {0}"),
        ("hud.corpse.hold", "Hold {0}  take {1} as primary     Tap {0}  choose slot"), ("hud.corpse.tap", "Tap {0}  take {1}"),
        ("hud.crate.hold", "Hold {0}  open care package"), ("hud.crate.opening", "Opening care package... {0} s"), ("toast.crate.perk", "Care package: {0} until you die."),
        ("corpse.title", "Take {0}"), ("corpse.option", "{0}  {1}"), ("corpse.primary", "Replace primary: {0}"), ("corpse.secondary", "Replace secondary: {0}"),
        ("corpse.cancel", "{0}  Cancel"),
        ("hud.turret.valid", "Turret: {0} or fire to place     {1} cancel"), ("hud.turret.invalid", "Turret: aim at flat ground within reach     {1} cancel"),
        ("gunship.title", "GUNSHIP"), ("gunship.zoom", "{0}x"), ("gunship.ammo", "AMMO {0} / {1}"), ("gunship.heat", "HEAT {0}%"),
        ("gunship.overheat", "OVERHEATED"), ("gunship.empty", "OUT OF AMMO"), ("gunship.hint", "{0}  Fire     {1}  Zoom     {2}  Next weapon"),
        ("app.title", "SUCHOHRAD"), ("app.subtitle", "Shooter"),
        ("menu.play", "Play"), ("menu.settings", "Settings"), ("menu.quit", "Quit"),
        ("menu.hint", "Up/Down or Tab to move   Enter to select   Esc to go back"),
        ("dialog.cancel", "Cancel"), ("dialog.back", "Back"),
        ("quit.title", "Quit the game?"), ("quit.menu.body", "The game closes and returns to the desktop. Settings are already saved."),
        ("quit.game.body", "The game closes. You leave the match and your score for this round is lost."), ("quit.confirm", "Quit to desktop"),
        ("pause.title", "Paused"), ("pause.resume", "Resume"), ("pause.disconnect", "Disconnect"),
        ("pause.hint", "Esc to resume"),
        ("disconnect.title", "Leave the match?"), ("disconnect.body", "You disconnect from the server and return to the main menu. Your score for this round is lost."),
        ("connect.title", "Play"), ("connect.address", "Server address"), ("connect.address.hint", "host or IP, for example 192.168.1.20"),
        ("connect.port", "Port"), ("connect.name", "Player name"), ("connect.go", "Connect"), ("connect.retry", "Try again"),
        ("connect.recent", "Recent servers"), ("connect.recent.empty", "No recent servers yet. Servers you join are listed here."), ("connect.recent.clear", "Clear recent servers"),
        ("connect.connecting", "Connecting to {0}..."),
        ("connect.err.mark", "Error:"),
        ("connect.err.address.empty", "Enter a server address."), ("connect.err.address.bad", "This address has characters a host name cannot have."),
        ("connect.err.port.bad", "The port is a number from 1 to 65535."),
        ("connect.err.name.empty", "Enter a player name."), ("connect.err.name.long", "The name is at most 24 characters."),
        ("connect.err.resolve", "Could not find a server called {0}."), ("connect.err.help.resolve", "Check the address for typos, or try the server's IP address."),
        ("connect.err.timeout", "The server at {0} did not answer within {1} seconds."), ("connect.err.help.timeout", "Check that the server is running and its port is open, and that the address and port are right. Then try again."),
        ("connect.err.refused", "The server at {0} refused the connection."), ("connect.err.help.refused", "It may be full or restarting. Try again in a moment."),
        ("connect.err.local", "The game could not open a network connection."), ("connect.err.help.local", "Check that no firewall blocks the game, then try again."),
        ("connect.err.help.generic", "Try again, or check the address with the server's host."),
        ("connect.err.lost", "The connection to {0} was lost: {1}."), ("connect.err.join", "The server at {0} did not let you join: {1}."),
        ("disconnect.done", "You left the match."), ("connect.err.help.lost", "Choose Play to reconnect."),
        ("loading.title", "Loading"), ("loading.target", "Joining {0}"), ("loading.pct", "{0}%"),
        ("loading.data", "Reading game data"), ("loading.map", "Loading the map"), ("loading.world", "Building the world"),
        ("loading.connect", "Joining the match"), ("loading.ready", "Ready"),
        ("tab.graphics", "Graphics"), ("tab.audio", "Audio"), ("tab.controls", "Controls"), ("tab.gameplay", "Gameplay"), ("tab.access", "Accessibility"),
        ("settings.apply", "Apply"), ("settings.revert", "Revert"), ("settings.saved", "Settings saved."), ("settings.reverted", "Reverted to the saved settings."),
        ("settings.hint", "PageUp / PageDown: switch tab"),
        ("settings.unsaved.title", "Unsaved changes"), ("settings.unsaved.body", "You changed settings that are not saved yet. Apply keeps them; Discard goes back to the saved settings."), ("settings.discard", "Discard"),
        ("state.on", "On"), ("state.off", "Off"),
        ("fmt.percent", "{0}%"), ("fmt.times", "{0:0.00}x"), ("fmt.degrees", "{0} deg"),
        ("graphics.fullscreen", "Fullscreen"), ("graphics.vsync", "Vertical sync"), ("graphics.msaa", "Anti-aliasing (MSAA)"), ("graphics.brightness", "Brightness"),
        ("display.title", "Keep these display settings?"), ("display.body", "The display changed. Reverting automatically in {0} s."), ("display.keep", "Keep"), ("display.revert", "Revert"),
        ("audio.master", "Master volume"), ("audio.music", "Music volume"), ("audio.sfx", "Effects volume"), ("audio.ui", "Interface volume"),
        ("audio.help", "The game has no sound assets yet; the buses and volumes are ready for them."),
        ("gameplay.sensitivity", "Mouse sensitivity"), ("gameplay.invert", "Invert vertical look"), ("gameplay.fov", "Field of view"), ("gameplay.crosshair", "Show crosshair"),
        ("access.scale", "Interface scale"), ("access.motion", "Reduce motion"), ("access.motion.help", "Turns off menu fades and toast animation."),
        ("access.colour", "Colour-blind safe colours"), ("access.colour.help", "Uses a blue and orange pair instead of green and red for health, hits and warnings."),
        ("controls.reset", "Reset to defaults"), ("controls.reset.done", "Controls reset to their defaults."), ("controls.press", "Press a key... (Esc cancels)"),
        ("controls.help", "Select a control, then press the new key or mouse button. A key another control uses swaps the two. Esc is reserved for Back and Pause."),
        ("controls.swapped", "{0} was used by {1}. They swapped: {1} is now {2}."),
        ("bind.mouse", "Mouse {0}"),
        ("bind.move_forward", "Move forward"), ("bind.move_back", "Move back"), ("bind.move_left", "Move left"), ("bind.move_right", "Move right"),
        ("bind.jump", "Jump"), ("bind.sprint", "Sprint"), ("bind.crouch", "Crouch"), ("bind.prone", "Prone"), ("bind.fire", "Fire"), ("bind.reload", "Reload"),
        ("bind.interact", "Interact"), ("bind.scoreboard", "Scoreboard (hold)"),
        ("bind.slot_1", "Weapon slot 1"), ("bind.slot_2", "Weapon slot 2"), ("bind.slot_3", "Weapon slot 3"), ("bind.slot_4", "Weapon slot 4"), ("bind.slot_5", "Weapon slot 5"), ("bind.zoom", "Scope zoom"), ("bind.headlights", "Vehicle headlights"), ("bind.killstreak", "Use killstreak"),
        ("weapon.m4a1", "M4A1"), ("weapon.aug", "AUG"), ("weapon.g36", "G36"), ("weapon.rpk", "RPK"), ("weapon.m249", "M249"), ("weapon.m60", "M60"), ("weapon.pkm", "PKM"),
        ("weapon.mp5", "MP5"), ("weapon.ump45", "UMP45"), ("weapon.p90", "P90"), ("weapon.remington870", "Remington 870"), ("weapon.spas12", "SPAS-12"), ("weapon.benelli_m4", "Benelli M4"), ("weapon.aa12", "AA-12"),
        ("streak.uav", "UAV"), ("streak.attack_missile", "Attack missile"), ("streak.turret", "Turret"), ("streak.care_package", "Care package"),
        ("streak.attack_helicopter", "Attack helicopter"), ("streak.helicopter_helper", "Helicopter helper"), ("streak.ac130", "AC-130"), ("streak.moab", "MOAB"),
        ("loadout.title", "Loadouts"), ("loadout.level", "Level {0}"), ("loadout.slot", "Slot {0}"), ("loadout.active", "Active"), ("loadout.none", "None"),
        ("loadout.primary", "Primary weapon"), ("loadout.secondary", "Secondary weapon"), ("loadout.perk", "Perk"), ("loadout.streak", "Killstreak {0}"),
        ("loadout.locked", "{0}   unlocks at level {1}"), ("loadout.empty", "Empty"),
        ("loadout.group.MachineGun", "Machine guns"), ("loadout.group.AssaultRifle", "Assault rifles"), ("loadout.group.Shotgun", "Shotguns"), ("loadout.group.Smg", "SMGs"),
        ("loadout.group.Sniper", "Sniper rifles"), ("loadout.group.Pistol", "Pistols"), ("loadout.group.Perk", "Perks"), ("loadout.group.Killstreak", "Killstreaks"),
        ("loadout.stat.damage", "Damage"), ("loadout.stat.rate", "Fire rate"), ("loadout.stat.magazine", "Magazine"), ("loadout.stat.mobility", "Mobility"),
        ("loadout.rate", "{0:0.0} shots/s"), ("loadout.compare", "Saved: {0}"),
        ("loadout.save", "Save"), ("loadout.discard", "Discard changes"), ("loadout.make_active", "Make active"),
        ("loadout.saved", "Saved on the server."), ("loadout.unsaved", "Unsaved changes."), ("loadout.invalid", "Cannot save: {0}"), ("loadout.saving", "Saving..."),
        ("loadout.offline", "Loadouts are kept on the server. Join a server to see and edit yours."), ("loadout.loading", "Loading your loadouts..."),
        ("loadout.discard.title", "Discard unsaved changes?"), ("loadout.discard.body", "Slot {0} has changes that are not saved yet. Discard drops them; Cancel keeps editing."),
        ("loadout.hint", "1-5  slot     Esc  back"),
        ("loadout.toast.saved", "Slot {0} saved."), ("loadout.toast.active", "Slot {0} is active from your next spawn."),
        ("loadout.edit", "Edit loadouts"), ("respawn.loadouts", "Your loadouts: press 1-5 to pick the one for your next spawn"),
        ("menu.loadouts", "Loadouts"), ("pause.loadouts", "Loadouts"), ("menu.customize", "Camouflage"), ("loadout.customize", "Camouflage"),
        ("camo.title", "Camouflage"), ("camo.offline", "Camouflage is kept on the server, one look per loadout. Join a server to customise yours."),
        ("camo.loading", "Loading your camouflage..."), ("camo.hint", "Esc: back    1-5: slot    Ctrl+Z: undo    Tab: move between controls"),
        ("camo.turn.hint", "Drag the model or press Left and Right to turn it."), ("camo.prone", "Prone pose"),
        ("camo.group.suit", "Suit colours"), ("camo.group.pattern", "Pattern"), ("camo.group.foliage", "Foliage"), ("camo.group.face", "Face paint"),
        ("camo.preset", "Preset"), ("camo.preset.apply", "Apply preset"), ("camo.toast.preset", "Preset {0} applied (not saved yet)"),
        ("camo.param.suit_base", "Base colour"), ("camo.param.suit_secondary", "Secondary colour"), ("camo.param.suit_pattern", "Pattern"),
        ("camo.param.pattern_scale", "Pattern scale"), ("camo.param.contrast", "Contrast"), ("camo.param.tuft_layers", "Tuft layers"),
        ("camo.param.vegetation", "Foliage look"), ("camo.param.tuft_size", "Tuft length"), ("camo.param.tuft_head", "Tufts on head"),
        ("camo.param.tuft_chest", "Tufts on chest"), ("camo.param.tuft_arms", "Tufts on arms"), ("camo.param.tuft_legs", "Tufts on legs"),
        ("camo.param.tuft_colour_variation", "Colour variation"), ("camo.param.tuft_sway", "Sway"), ("camo.param.face_pattern", "Paint pattern"),
        ("camo.param.face_colour_a", "Paint colour A"), ("camo.param.face_colour_b", "Paint colour B"), ("camo.param.face_colour_c", "Paint colour C"),
        ("camo.param.face_coverage", "Coverage"),
        ("camo.randomize", "Randomize"), ("camo.reset", "Reset to default"), ("camo.undo", "Undo"), ("camo.copy", "Copy code"), ("camo.paste", "Paste code"),
        ("camo.code", "Share code"), ("camo.revert", "Revert"), ("camo.apply", "Apply"), ("camo.saving", "Saving..."),
        ("camo.saved", "Saved on this loadout."), ("camo.unsaved", "Unsaved changes. Apply saves them to this loadout."),
        ("camo.toast.saved", "Slot {0} camouflage saved"), ("camo.toast.copied", "Share code copied to the clipboard"),
        ("camo.toast.pasted", "Share code applied (not saved yet)"), ("camo.paste.failed", "Could not apply the share code: {0}"),
        ("camo.discard.title", "Discard unsaved camouflage?"), ("camo.discard.body", "Unsaved camouflage edits to slot {0} will be lost."),
        ("camo.discard.confirm", "Discard"),
        ("progress.levelup", "Level {0} reached"),
        ("xp.title", "Match XP"), ("xp.total", "Total: {0} XP"), ("xp.level", "Level {0}"), ("xp.line", "{0}   +{1} XP"),
        ("xp.reason.Kill", "Kill"), ("xp.reason.Headshot", "Headshot"), ("xp.reason.Roadkill", "Roadkill"), ("xp.reason.Assist", "Assist"),
        ("xp.reason.StreakKill", "Killstreak kill"), ("xp.reason.MatchWin", "Match win"), ("xp.reason.MatchTop3", "Top three finish"),
        ("weapon.attack_missile", "Attack Missile"),
        ("pov.title", "ATTACK MISSILE"), ("pov.alt", "ALT {0} m"), ("pov.time", "TIME {0} s"), ("pov.hint", "Mouse: steer the missile. It turns at a limited rate. Your body stays where it is."),
        ("toast.info", "Info:"), ("toast.warn", "Notice:"), ("toast.error", "Problem:"),
    };

    static bool _done;

    public static void Register()
    {
        if (_done) return;
        _done = true;
        var tr = new Translation { Locale = "en" };
        foreach (var (k, v) in Table) tr.AddMessage(k, v);
        foreach (var (k, v) in Hud2Strings.Table) tr.AddMessage(k, v);
        TranslationServer.AddTranslation(tr);
        TranslationServer.SetLocale("en");
    }

    public static string T(string key) => TranslationServer.Translate(key);
    public static string F(string key, params object[] a) => string.Format(T(key), a);
}
