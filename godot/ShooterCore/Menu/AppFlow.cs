using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Menu;

/// <summary>Where the shooter application is, as one value (it used to be implied by <c>_client</c> null or not, <c>_mapLoaded</c>, <c>_worldLit</c> and the menu shell's <c>GameActive</c>).</summary>
public enum AppPhase
{
    /// <summary>Starting up: command line, sound table, role decided.</summary>
    Boot,
    /// <summary>Main menu or the connect form (client with a display).</summary>
    Menu,
    /// <summary>Loading data, map and assets, then waiting for the server's welcome. Esc cancels only from the connect wait on.</summary>
    Loading,
    /// <summary>A match is running (Pause is a menu screen over it, not a phase: the shell owns Esc and the mouse).</summary>
    Game,
    /// <summary>No menus: dedicated server, offline walk, smoke and bot roles run on the loaded map until they quit.</summary>
    Direct,
    /// <summary>Startup failed; the process is quitting with exit code 2 (a smoke run must not report OK).</summary>
    Failed,
}

/// <summary>How a join attempt ended: the one place that tells "the player cancelled" from "it failed" (it used to be an empty failure string).</summary>
public readonly record struct JoinResult(JoinOutcome Outcome, string Message = "")
{
    public static readonly JoinResult Welcomed = new(JoinOutcome.Welcomed);
    public static readonly JoinResult Cancelled = new(JoinOutcome.Cancelled);
    public static JoinResult Failed(string message) => new(JoinOutcome.Failed, message);

    /// <summary>The text the connect screen shows as the error, or null when there is nothing to report (a join that worked, or one the player cancelled).</summary>
    public string? ErrorForScreen => Outcome == JoinOutcome.Failed && Message.Length > 0 ? Message : null;
}

public enum JoinOutcome { Welcomed, Cancelled, Failed }

/// <summary>The shooter application's phases and legal moves between them.</summary>
public static class AppFlow
{
    public static readonly StateMachineSpec<AppPhase> Spec = StateMachineSpec<AppPhase>.Define(
            "App.Phase", "Boot, menus, loading, a running match; the one value the shooter's entry point and menu shell hang their behaviour on.", AppPhase.Boot)
        .Allow(AppPhase.Boot, AppPhase.Menu, AppPhase.Direct, AppPhase.Failed)
        .Allow(AppPhase.Direct, AppPhase.Failed)
        .Allow(AppPhase.Menu, AppPhase.Loading)
        .Allow(AppPhase.Loading, AppPhase.Game, AppPhase.Menu)
        .Allow(AppPhase.Game, AppPhase.Menu)
        .Note(AppPhase.Boot, "Menu: client with a display and no smoke or script flags. Direct: server, offline, smoke, bot roles. Failed: startup threw.")
        .Note(AppPhase.Menu, "Entered again after a cancelled or failed join (connect screen with the reason) and after leaving or losing a match (toast).")
        .Note(AppPhase.Loading, "Left for Game when the server welcomes us; back to Menu when cancelled, failed, or the connection dropped meanwhile.")
        .Note(AppPhase.Game, "Left only for Menu: Disconnect from Pause, or the server dropped us.")
        .Note(AppPhase.Direct, "No menus; ends with the process, or Failed when loading the data or map threw.")
        .Note(AppPhase.Failed, "Terminal: Quit(2) was requested.")
        .Build();
}
