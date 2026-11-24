using Drycastle.LauncherCore;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Menu;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Menu;

/// <summary>The menu shell's screen stack and Esc routing, the connect screen's flow, the app phases and the camo save wait.</summary>
public class MenuFlowTests
{
    sealed class Screen { public override string ToString() => "screen"; }

    // ---- ScreenStack ----

    [Fact]
    public void Pop_closes_only_the_top_screen()
    {
        var stack = new ScreenStack<Screen>();
        Screen main = new(), dialog = new();
        stack.Push(main); stack.Push(dialog);
        Assert.True(stack.Pop(dialog));
        Assert.Same(main, stack.Top);
        Assert.Equal(1, stack.Count);
    }

    [Fact]
    public void A_second_close_of_the_same_screen_does_not_pop_the_one_underneath()
    {
        // the auto-timer of a dialog and Esc in one frame: both call Back(), the second must find the dialog gone
        var stack = new ScreenStack<Screen>();
        Screen settings = new(), dialog = new();
        stack.Push(settings); stack.Push(dialog);
        Assert.True(stack.Pop(dialog));
        Assert.False(stack.Pop(dialog));
        Assert.Same(settings, stack.Top);
    }

    [Fact]
    public void A_covered_screen_cannot_close_itself()
    {
        var stack = new ScreenStack<Screen>();
        Screen under = new(), over = new();
        stack.Push(under); stack.Push(over);
        Assert.False(stack.Pop(under));
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void A_screen_is_on_the_stack_once_and_clear_returns_them_all()
    {
        var stack = new ScreenStack<Screen>();
        Screen a = new(), b = new();
        Assert.True(stack.Push(a));
        Assert.False(stack.Push(a));
        stack.Push(b);
        var all = stack.Clear();
        Assert.Equal(new[] { a, b }, all);
        Assert.Null(stack.Top);
        Assert.False(stack.Pop(a));
    }

    // ---- Esc routing ----

    [Fact]
    public void Esc_with_a_screen_open_closes_the_screen_and_leaves_the_match_panels_alone()
    {
        var asked = 0;
        var action = MenuEsc.Decide(true, () => { asked++; return true; }, gameActive: true, hasPause: true);
        Assert.Equal(EscAction.ScreenBack, action);
        Assert.Equal(0, asked); // the open map behind a sandbox screen stays open
    }

    [Fact]
    public void Esc_without_a_screen_closes_the_match_panel_before_pause()
    {
        Assert.Equal(EscAction.MatchClosed, MenuEsc.Decide(false, () => true, true, true));
        Assert.Equal(EscAction.OpenPause, MenuEsc.Decide(false, () => false, true, true));
        Assert.Equal(EscAction.OpenPause, MenuEsc.Decide(false, null, true, true));
    }

    [Fact]
    public void Esc_in_the_menu_with_no_screen_does_nothing()
    {
        Assert.Equal(EscAction.None, MenuEsc.Decide(false, () => false, gameActive: false, hasPause: true));
        Assert.Equal(EscAction.None, MenuEsc.Decide(false, null, gameActive: true, hasPause: false));
    }

    // ---- ConnectFlow ----

    [Fact]
    public void Connect_flow_runs_form_connecting_error_and_back()
    {
        var f = new ConnectFlow();
        Assert.Equal(ConnectState.Form, f.State);
        Assert.True(f.Begin());
        Assert.False(f.Begin()); // already probing
        f.Fail();
        Assert.Equal(ConnectState.Error, f.State);
        Assert.True(f.Begin()); // Try again
        Assert.Equal(ConnectState.Connecting, f.State);
    }

    [Fact]
    public void Connect_probe_times_out_after_six_seconds_by_the_flow_clock()
    {
        var f = new ConnectFlow();
        f.Begin();
        var t = 0.0;
        while (!f.Tick(1 / 60.0)) { t += 1 / 60.0; Assert.True(t < 7); }
        Assert.InRange(f.Elapsed, 6.0, 6.0 + 1 / 30.0);
        Assert.Equal(6.0, ConnectFlow.ProbeTimeout);
        f.Fail();
        Assert.False(f.Tick(1)); // no probe, no clock
        Assert.Equal(0, f.Elapsed);
    }

    [Fact]
    public void Connect_retry_restarts_the_clock()
    {
        var f = new ConnectFlow();
        f.Begin(); f.Tick(5); f.Fail();
        f.Begin();
        Assert.Equal(0, f.Elapsed);
        Assert.False(f.Tick(1));
    }

    [Fact]
    public void Connect_probe_is_released_in_one_place_however_the_attempt_ends()
    {
        var f = new ConnectFlow();
        var released = 0;
        f.LeftConnecting += () => released++;
        f.Begin(); f.Fail();          // timeout, refused, unresolvable
        f.Begin(); f.Back();          // cancelled with Esc
        f.Begin(); f.ToForm();
        Assert.Equal(3, released);
        f.Fail();                     // a failed join shows its error with no probe running
        f.ToForm();
        Assert.Equal(3, released);
    }

    [Fact]
    public void Connect_back_is_one_level_up()
    {
        var f = new ConnectFlow();
        Assert.Equal(ConnectBack.LeaveScreen, f.Back());
        f.Begin();
        Assert.Equal(ConnectBack.CancelAttempt, f.Back());
        Assert.Equal(ConnectState.Form, f.State);
        f.Fail();
        Assert.Equal(ConnectBack.LeaveError, f.Back());
        Assert.Equal(ConnectState.Form, f.State);
    }

    // ---- AppFlow ----

    [Fact]
    public void App_phase_follows_boot_menu_loading_game_menu()
    {
        var p = new StateMachine<AppPhase>(AppFlow.Spec);
        Assert.True(p.Enter(AppPhase.Menu));
        Assert.True(p.Enter(AppPhase.Loading));
        Assert.True(p.Enter(AppPhase.Menu)); // cancelled or failed
        Assert.True(p.Enter(AppPhase.Loading));
        Assert.True(p.Enter(AppPhase.Game));
        Assert.True(p.Enter(AppPhase.Menu));
        Assert.Throws<InvalidOperationException>(() => p.Enter(AppPhase.Game)); // Menu -> Game skips loading
    }

    [Fact]
    public void App_startup_failure_is_its_own_terminal_phase()
    {
        Assert.Contains("Failed", AppFlow.Spec.Terminal);
        var p = new StateMachine<AppPhase>(AppFlow.Spec);
        p.Enter(AppPhase.Direct);
        Assert.True(p.Enter(AppPhase.Failed));
    }

    [Fact]
    public void A_cancelled_join_is_not_an_error_for_the_connect_screen()
    {
        Assert.Null(JoinResult.Cancelled.ErrorForScreen);
        Assert.Null(JoinResult.Welcomed.ErrorForScreen);
        Assert.Equal("why", JoinResult.Failed("why").ErrorForScreen);
        Assert.NotEqual(JoinResult.Cancelled, JoinResult.Failed(""));
    }

    // ---- CamoSaveWait ----

    static CamoSpec Look(byte body) => new() { Body = body };

    [Fact]
    public void A_profile_without_the_sent_look_is_not_the_save_answer()
    {
        // a loadout acknowledgement sends a profile too: it must not toast "saved" nor unlock the screen
        var w = new CamoSaveWait();
        w.Begin(2, Look(1), 10);
        Assert.False(w.Acked(2, Look(0)));
        Assert.False(w.Acked(1, Look(1)));
        Assert.False(w.Acked(2, null));
        Assert.True(w.Waiting);
        Assert.True(w.Acked(2, Look(1)));
        Assert.False(w.Waiting);
    }

    [Fact]
    public void A_save_the_server_never_answers_gives_up_once()
    {
        var w = new CamoSaveWait();
        w.Begin(0, Look(3), 100);
        Assert.False(w.GaveUp(100 + CamoSaveWait.GiveUpAfter - 0.1));
        Assert.True(w.Waiting);
        Assert.True(w.GaveUp(100 + CamoSaveWait.GiveUpAfter));
        Assert.False(w.Waiting);
        Assert.False(w.GaveUp(1000));
    }

    [Fact]
    public void An_ack_or_an_error_ends_the_wait_before_the_deadline()
    {
        var w = new CamoSaveWait();
        w.Begin(0, Look(3), 0);
        w.Cancel(); // server error
        Assert.False(w.Waiting);
        Assert.False(w.GaveUp(100));
        Assert.False(w.Acked(0, Look(3)));
    }

    // ---- LauncherFlow ----

    [Fact]
    public void A_stale_check_result_is_ignored_after_a_newer_check_started()
    {
        // Settings closed with another channel while the first check is in flight
        var f = new LauncherFlow();
        Assert.True(f.TryBeginCheck(out var first));
        Assert.True(f.TryBeginCheck(out var second));
        Assert.False(f.IsCurrent(first));
        Assert.True(f.IsCurrent(second));
    }

    [Fact]
    public void A_check_in_flight_is_stale_once_an_install_started_so_two_installers_cannot_run()
    {
        var f = new LauncherFlow();
        f.TryBeginCheck(out var old);
        f.TryBeginCheck(out var current);
        Assert.True(f.IsCurrent(current));
        Assert.True(f.TryBeginInstall());          // the current check found work and installs
        Assert.False(f.IsCurrent(old));            // the older answer does nothing
        Assert.False(f.TryBeginInstall());         // a second trigger is refused
        Assert.False(f.TryBeginCheck(out _));      // and no check may start while the installer owns the folder
        Assert.Equal(LauncherState.Working, f.State);
    }

    [Fact]
    public void Cancel_is_offered_while_downloading_and_withdrawn_once_installing()
    {
        var f = new LauncherFlow();
        f.TryBeginInstall();
        Assert.True(f.CanCancel);
        Assert.Equal("hint_busy", f.HintKey(false));
        f.MarkInstalling();
        Assert.False(f.CanCancel);
        Assert.True(f.Installing);
        Assert.Equal("hint_installing", f.HintKey(false));
        Assert.True(f.Finish(LauncherState.Ready));
        Assert.False(f.Installing);
        Assert.False(f.CanCancel);
    }

    [Fact]
    public void The_settings_hint_wins_over_the_state_hint()
    {
        var f = new LauncherFlow();
        f.TryBeginInstall();
        Assert.Equal("hint_settings", f.HintKey(true));
        Assert.Equal("hint_main", new LauncherFlow().HintKey(false));
    }

    [Fact]
    public void Launcher_moves_follow_the_declared_table()
    {
        Assert.True(LauncherFlow.Allows(LauncherState.Working, LauncherState.Ready));
        Assert.True(LauncherFlow.Allows(LauncherState.Working, LauncherState.Failed));
        Assert.False(LauncherFlow.Allows(LauncherState.Working, LauncherState.Checking));
        Assert.False(LauncherFlow.Allows(LauncherState.Ready, LauncherState.Working)); // Play, not install
        var f = new LauncherFlow();
        Assert.False(f.Finish(LauncherState.Working)); // only TryBeginInstall enters Working
        Assert.True(f.Finish(LauncherState.UpdateAvailable));
        Assert.True(f.TryBeginInstall());
        Assert.True(f.Finish(LauncherState.Failed));
        Assert.True(f.TryBeginCheck(out _)); // Try again
        Assert.False(f.Finish(LauncherState.Checking));
    }
}
