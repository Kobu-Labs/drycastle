using Xunit;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.ShooterCore.Tests.Hud;

public class OverlayTests
{
    // ---- mouse ownership ----

    [Fact]
    public void Free_mouse_is_the_or_of_its_owners_for_every_combination()
    {
        for (var i = 0; i < 32; i++)
        {
            var o = new MouseOwners((i & 1) != 0, (i & 2) != 0, (i & 4) != 0, (i & 8) != 0, (i & 16) != 0);
            // Shooter.cs:182 as it was
            var old = o.Dead || o.ChatOpen || o.EquipmentOpen || o.TacticalMapOpen || o.ChooserFreesMouse;
            Assert.Equal(old, o.WantsFreeMouse);
            Assert.Equal(i != 0, o.WantsFreeMouse);
        }
    }

    [Fact]
    public void Map_close_does_not_capture_the_mouse_under_the_respawn_screen_or_the_chat_box()
    {
        // regression: the sandbox map's cursor writer captured on close whenever no menu was up, one frame before the shell took the mouse back
        Assert.False(new MouseOwners(Dead: true, false, false, false, false).CapturesWhenMapCloses(menuOpen: false));
        Assert.False(new MouseOwners(false, ChatOpen: true, false, false, false).CapturesWhenMapCloses(menuOpen: false));
        Assert.False(new MouseOwners(false, false, false, false, ChooserFreesMouse: true).CapturesWhenMapCloses(menuOpen: false));
        Assert.True(new MouseOwners(false, false, false, false, false).CapturesWhenMapCloses(menuOpen: false));
        Assert.False(new MouseOwners(false, false, false, false, false).CapturesWhenMapCloses(menuOpen: true)); // the old rule, kept
    }

    // ---- overlay arbiter ----

    public static IEnumerable<object[]> Contexts()
    {
        for (var i = 0; i < 64; i++) yield return new object[] { i };
    }

    static OverlayContext Ctx(int i) => new((i & 1) != 0, (i & 2) != 0, (i & 4) != 0, (i & 8) != 0, (i & 16) != 0, (i & 32) != 0);

    [Theory, MemberData(nameof(Contexts))]
    public void Screen_taken_is_the_old_MapBlocked(int i)
    {
        var c = Ctx(i);
        Assert.Equal(c.Dead || c.Ending || c.ControlMode, OverlayArbiter.ScreenTaken(c)); // Hud.TacticalMap.cs MapBlocked
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Equipment_key_and_poll_equal_the_old_code(int i)
    {
        var c = Ctx(i);
        var blocked = c.Dead || c.Ending || c.ControlMode;
        foreach (var open in new[] { false, true })
        {
            // ScreensClient.cs: menu or chooser: false. Hud.ToggleEquipment: chat: false; open: close; blocked: false; else open
            var old = c.MenuOpen || c.ChooserOpen ? OverlayKeyVerdict.Ignore : c.ChatOpen ? OverlayKeyVerdict.Ignore : open ? OverlayKeyVerdict.Close : blocked ? OverlayKeyVerdict.Ignore : OverlayKeyVerdict.Open;
            Assert.Equal(old, OverlayArbiter.Key(Overlay.Equipment, open, c));
        }
        // the two polls: Hud.UpdateEquipment (blocked) and NetClient.UpdateHud (menu or chooser)
        Assert.Equal(blocked || c.MenuOpen || c.ChooserOpen, OverlayArbiter.ShouldClose(Overlay.Equipment, c));
        Assert.Equal(c.MenuOpen || c.ChooserOpen, OverlayArbiter.YieldsToMenu(Overlay.Equipment, c));
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Map_key_equals_the_old_code_and_it_now_yields_to_a_menu(int i)
    {
        var c = Ctx(i);
        var blocked = c.Dead || c.Ending || c.ControlMode;
        foreach (var open in new[] { false, true })
        {
            // ScreensClient.cs: menu: false (the chooser was not checked). Hud.ToggleTacticalMap: chat: false; open: close; blocked: false; else open
            var old = c.MenuOpen ? OverlayKeyVerdict.Ignore : c.ChatOpen ? OverlayKeyVerdict.Ignore : open ? OverlayKeyVerdict.Close : blocked ? OverlayKeyVerdict.Ignore : OverlayKeyVerdict.Open;
            Assert.Equal(old, OverlayArbiter.Key(Overlay.TacticalMap, open, c));
        }
        Assert.Equal(blocked || c.MenuOpen, OverlayArbiter.ShouldClose(Overlay.TacticalMap, c)); // the menu term is the fix: the old poll was only `blocked`
    }

    [Fact]
    public void An_open_map_closes_when_a_menu_opens_behind_it()
    {
        var c = new OverlayContext(ChatOpen: false, Dead: false, Ending: false, ControlMode: false, MenuOpen: true, ChooserOpen: false);
        Assert.True(OverlayArbiter.ShouldClose(Overlay.TacticalMap, c));
        Assert.True(OverlayArbiter.ShouldClose(Overlay.Equipment, c));
        // the corpse chooser still does not close the map (the map key is not gated by it either)
        Assert.False(OverlayArbiter.ShouldClose(Overlay.TacticalMap, c with { MenuOpen = false, ChooserOpen = true }));
        Assert.True(OverlayArbiter.ShouldClose(Overlay.Equipment, c with { MenuOpen = false, ChooserOpen = true }));
    }

    [Fact]
    public void Chat_opens_unless_open_or_a_menu_is_up()
    {
        Assert.True(OverlayArbiter.CanOpenChat(false, false));
        Assert.False(OverlayArbiter.CanOpenChat(true, false));
        Assert.False(OverlayArbiter.CanOpenChat(false, true));
        Assert.False(OverlayArbiter.CanOpenChat(true, true));
    }

    // ---- panel fade ----

    /// <summary>The fade as the two panels wrote it by hand (EquipmentScreen / TacticalMap, with Godot's IsEqualApprox).</summary>
    sealed class OldPanel
    {
        public bool Open, Visible;
        public float Alpha;
        static bool Approx(float a, float b)
        {
            if (a == b) return true;
            var t = 1e-5f * MathF.Abs(a);
            if (t < 1e-5f) t = 1e-5f;
            return MathF.Abs(a - b) < t;
        }
        public void DoOpen(bool reduce) { Open = true; Visible = true; if (reduce) Alpha = 1f; }
        public void DoClose(bool reduce) { Open = false; if (reduce) Finish(); }
        void Finish() { Alpha = 0f; Visible = false; }
        public void Process(float dt, float fade)
        {
            var target = Open ? 1f : 0f;
            if (!Approx(Alpha, target)) Alpha = Ramp.MoveToward(Alpha, target, dt / fade);
            if (!Open && Alpha <= 0f) Finish();
        }
    }

    [Fact]
    public void Panel_fade_matches_the_hand_written_fade_and_only_takes_declared_transitions()
    {
        var rng = new Random(1234);
        for (var run = 0; run < 200; run++)
        {
            var old = new OldPanel();
            var fade = new PanelFade();
            var phase = fade.Phase;
            Assert.Equal(PanelPhase.Closed, phase);
            var reduce = rng.Next(4) == 0;
            for (var step = 0; step < 300; step++)
            {
                var r = rng.Next(10);
                if (r == 0) { old.DoOpen(reduce); fade.Show(reduce); }
                else if (r == 1) { old.DoClose(reduce); fade.Hide(reduce); }
                else if (r == 2 && rng.Next(8) == 0) reduce = !reduce; // the setting flipped while a fade was going
                else
                {
                    var dt = rng.Next(5) == 0 ? 1f / 240f : 1f / 60f + (float)rng.NextDouble() * 0.02f;
                    if (old.Visible) old.Process(dt, 0.15f);
                    if (fade.Shown) fade.Tick(dt, 0.15f);
                }
                Assert.Equal(old.Open, fade.Wanted);
                Assert.Equal(old.Visible, fade.Shown);
                Assert.Equal(old.Alpha, fade.Alpha);
                var next = fade.Phase;
                Assert.True(PanelFade.Spec.Allows(phase, next), $"{phase} -> {next}");
                phase = next;
            }
        }
    }

    [Fact]
    public void Panel_fade_reports_the_alpha_to_write_and_the_end_of_the_fade()
    {
        var f = new PanelFade();
        f.Show(false);
        Assert.Equal(PanelPhase.Opening, f.Phase);
        var (changed, finished) = f.Tick(0.075f, 0.15f);
        Assert.True(changed); Assert.False(finished);
        Assert.Equal(0.5f, f.Alpha);
        f.Tick(0.1f, 0.15f);
        Assert.Equal(PanelPhase.Open, f.Phase);
        Assert.False(f.Tick(0.1f, 0.15f).AlphaChanged); // at the target: nothing to write
        Assert.False(f.Hide(false));
        Assert.Equal(PanelPhase.Closing, f.Phase);
        f.Tick(0.1f, 0.15f);
        var end = f.Tick(0.1f, 0.15f);
        Assert.True(end.Finished);
        Assert.Equal(PanelPhase.Closed, f.Phase);
        f.Show(true);
        Assert.Equal(PanelPhase.Open, f.Phase);
        Assert.True(f.Hide(true));
        Assert.Equal(PanelPhase.Closed, f.Phase);
    }

    // ---- pending answer (the screens' "wait for the server, dim when slow, give up") ----

    [Fact]
    public void Pending_answer_dims_after_012_and_gives_up_after_06_like_the_screens_did()
    {
        var p = new PendingAnswer();
        p.Begin(8.0); // binary-exact times: the thresholds are compared with >=
        Assert.False(p.Dimmed(8.0625, 0.12));
        Assert.True(p.Dimmed(8.125, 0.12));
        Assert.False(p.GiveUp(8.5, 0.6));
        Assert.True(p.Active);
        Assert.True(p.GiveUp(8.625, 0.6));
        Assert.False(p.Active);
        Assert.False(p.Dimmed(9, 0.12));
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Hunters_log_is_ruled_like_the_equipment_screen(int i)
    {
        var c = Ctx(i);
        Assert.Equal(OverlayArbiter.YieldsToMenu(Overlay.Equipment, c), OverlayArbiter.YieldsToMenu(Overlay.HuntersLog, c));
        Assert.Equal(OverlayArbiter.Key(Overlay.Equipment, false, c), OverlayArbiter.Key(Overlay.HuntersLog, false, c));
        Assert.Equal(OverlayArbiter.Key(Overlay.Equipment, true, c), OverlayArbiter.Key(Overlay.HuntersLog, true, c));
    }

    [Fact]
    public void An_open_hunters_log_frees_the_mouse()
    {
        Assert.True(new MouseOwners(false, false, false, false, false, HuntersLogOpen: true).WantsFreeMouse);
        Assert.False(new MouseOwners(false, false, false, false, false).WantsFreeMouse);
    }
}
