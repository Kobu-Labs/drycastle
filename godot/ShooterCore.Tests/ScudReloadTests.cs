using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Loading a rocket from a rocket cart onto an empty Scud truck: the launcher's Loading state and its interrupts, the reach rules, the interact key's tap and hold, the chain of loaded carts, scud.json and the wire.</summary>
public class ScudReloadTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly ScudFile Shipped = ScudFile.Load(Path.Combine(DataDir, "scud.json"));
    static ScudLauncherData T => Shipped.Launcher;
    static ScudReloadData R => Shipped.Reload;

    const ushort Op = 7, Cart = 0x4020;
    static readonly Vector3 Truck = new(100, 5, 100);
    static readonly Vector3 CartAt = new(110, 5, 100);
    static readonly Vector3 Far = new(100, 5, 600);
    static ScudCrew Solo => new(new ushort[] { 0, Op }, 1);

    static ScudLauncher Launcher() => new(T, R);

    static ScudTick Step(ScudLauncher l, float seconds, ScudCartSense? cart, Vector3? truck = null)
    {
        ScudTick last = default;
        for (var t = 0f; t < seconds; t += 0.1f)
        {
            var o = l.Tick(0.1f, Solo, truck ?? Truck, cart);
            if (o.TakeCart != 0) last = o;
        }
        return last;
    }

    /// <summary>A truck fired and lowered: Empty.</summary>
    static ScudLauncher Empty()
    {
        var l = Launcher();
        Assert.Equal(ScudAnswer.Ok, l.Open(Solo, Op));
        Assert.Equal(ScudAnswer.Ok, l.PlaceLock(Solo, Op, Far, Truck, true));
        Assert.Equal(ScudAnswer.Ok, l.Confirm(Solo, Op, 0));
        Step(l, (float)(T.ErectSeconds + T.IgnitionSeconds + T.SpentHoldSeconds + T.LowerSeconds) + 0.5f, null);
        Assert.Equal(ScudLauncherState.Empty, l.State);
        return l;
    }

    static readonly ScudCartSense Fine = new(true, CartAt);

    // ---- the launcher ----

    [Fact]
    public void A_loading_takes_the_rocket_off_the_cart_at_the_half_way_point_and_the_rest_puts_it_on_the_rack()
    {
        var l = Empty();
        Assert.True(l.BeginLoad(Cart, Truck, CartAt));
        Assert.Equal(ScudLauncherState.Loading, l.State);
        Assert.Equal(Cart, l.Cart);
        Assert.True(l.Immobilised);
        Assert.False(ScudRules.RocketOnRack(l.State, l.Left, T)); // still on the cart
        Assert.Equal(ScudAnswer.WrongState, l.Open(Solo, Op));
        // before the transfer point nothing is taken
        var order = Step(l, (float)(T.ReloadSeconds * R.TransferShare) - 0.5f, Fine);
        Assert.Equal(0, order.TakeCart);
        Assert.Equal(ScudLauncherState.Loading, l.State);
        // at it: the order to take the rocket (once), and the second half begins with the rocket on the rack
        order = Step(l, 1f, Fine);
        Assert.Equal(Cart, order.TakeCart);
        Assert.Equal(ScudLauncherState.Reloading, l.State);
        Assert.Equal(0, l.Cart);
        Assert.True(ScudRules.RocketOnRack(l.State, l.Left, T));
        Assert.Equal(T.ReloadSeconds * (1 - R.TransferShare), l.Left + l.Elapsed, 1);
        Assert.True(l.Immobilised);
        Assert.Equal(0, Step(l, 1f, Fine).TakeCart); // never a second order
        Step(l, (float)T.ReloadSeconds, Fine);
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.False(l.Immobilised);
        Assert.Equal(ScudAnswer.Ok, l.Open(Solo, Op)); // ready to fire again
    }

    [Fact]
    public void The_whole_loading_takes_the_reload_time_like_the_admins_reload()
    {
        var l = Empty();
        l.BeginLoad(Cart, Truck, CartAt);
        var seconds = 0f;
        while (l.State is ScudLauncherState.Loading or ScudLauncherState.Reloading && seconds < 60) { l.Tick(0.05f, Solo, Truck, Fine); seconds += 0.05f; }
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.Equal(T.ReloadSeconds, seconds, 0);
    }

    [Fact]
    public void A_loading_starts_only_from_an_empty_rack_and_with_a_cart()
    {
        var stowed = Launcher();
        Assert.False(stowed.BeginLoad(Cart, Truck, CartAt));
        var e = Empty();
        Assert.False(e.BeginLoad(0, Truck, CartAt));
        Assert.True(e.BeginLoad(Cart, Truck, CartAt));
        Assert.False(e.BeginLoad(Cart, Truck, CartAt)); // already loading
        Assert.False(e.BeginReload()); // and the admin's reload cannot start on top of it
        Assert.False(e.ReloadNow());
        Assert.Equal(ScudLauncherState.Loading, e.State);
    }

    public static IEnumerable<object[]> Interrupts() => new[]
    {
        new object[] { "the cart was lost", false, 0f, 0f, ScudLoadCancel.CartLost },
        new object[] { "the cart moved away", true, 2.5f, 0f, ScudLoadCancel.Moved },
        new object[] { "the truck was shoved", true, 0f, 2.5f, ScudLoadCancel.Moved },
    };

    [Theory]
    [MemberData(nameof(Interrupts))]
    public void An_interrupt_before_the_transfer_point_cancels_and_the_cart_keeps_its_rocket(string why, bool cartOk, float cartMoved, float truckMoved, ScudLoadCancel expected)
    {
        _ = why;
        var l = Empty();
        var dropped = new List<ScudLoadCancel>();
        l.LoadDropped += dropped.Add;
        l.BeginLoad(Cart, Truck, CartAt);
        Assert.Equal(0, Step(l, 2f, Fine).TakeCart);
        var order = Step(l, 0.3f, new ScudCartSense(cartOk, CartAt + new Vector3(cartMoved, 0, 0)), Truck + new Vector3(truckMoved, 0, 0));
        Assert.Equal(0, order.TakeCart); // no order: the cart is untouched
        Assert.Equal(ScudLauncherState.Empty, l.State);
        Assert.Equal(0, l.Cart);
        Assert.False(l.Immobilised);
        Assert.Equal(new[] { expected }, dropped);
        // and it can be tried again
        Assert.True(l.BeginLoad(Cart, Truck, CartAt));
    }

    [Fact]
    public void A_launcher_told_of_no_cart_drops_the_loading()
    {
        var l = Empty();
        l.BeginLoad(Cart, Truck, CartAt);
        Step(l, 0.2f, null);
        Assert.Equal(ScudLauncherState.Empty, l.State);
    }

    [Fact]
    public void A_small_shove_is_not_leaving_and_after_the_transfer_nothing_cancels_it()
    {
        var l = Empty();
        l.BeginLoad(Cart, Truck, CartAt);
        Step(l, 1f, new ScudCartSense(true, CartAt + new Vector3(0.4f, 0, 0)), Truck + new Vector3(0.3f, 0, 0));
        Assert.Equal(ScudLauncherState.Loading, l.State);
        Step(l, (float)(T.ReloadSeconds * R.TransferShare), Fine);
        Assert.Equal(ScudLauncherState.Reloading, l.State);
        // the cart destroyed, towed away or shot now changes nothing: the rocket is on the rack
        Step(l, 1f, new ScudCartSense(false, CartAt + new Vector3(50, 0, 0)));
        Assert.Equal(ScudLauncherState.Reloading, l.State);
    }

    [Fact]
    public void The_admins_reload_still_shows_the_rocket_in_its_second_half()
    {
        var l = Empty();
        Assert.True(l.BeginReload());
        Assert.False(ScudRules.RocketOnRack(l.State, l.Left, T));
        Step(l, (float)T.ReloadSeconds * 0.6f, null);
        Assert.True(ScudRules.RocketOnRack(l.State, l.Left, T));
        Step(l, (float)T.ReloadSeconds, null);
        Assert.Equal(ScudLauncherState.Stowed, l.State);
    }

    // ---- the reach rules ----

    static BodyBox Box(float x, float z, float yaw = 0f, float halfW = 1.57f, float halfL = 5.95f) => new(x, z, halfW, halfL, yaw);

    [Fact]
    public void The_gap_between_boxes_is_zero_when_they_touch_and_the_distance_between_their_sides_otherwise()
    {
        var truck = Box(0, 0);
        Assert.Equal(0f, ScudReloadRules.Gap(truck, Box(0, 0)), 3);
        Assert.Equal(0f, ScudReloadRules.Gap(truck, Box(3.14f, 0, 0, 1.57f, 5.95f)), 2); // side by side, touching
        Assert.Equal(2f, ScudReloadRules.Gap(truck, Box(3.14f + 2f, 0)), 2); // 2 m between the sides
        Assert.Equal(4f, ScudReloadRules.Gap(truck, Box(0, 11.9f + 4f)), 2); // end to end, 4 m apart
        // a 12.6 m cart alongside, offset along the truck: still the 2 m between the sides
        Assert.Equal(2f, ScudReloadRules.Gap(truck, Box(2.46f / 2 + 1.57f + 2f, 5f, 0, 1.23f, 6.3f)), 2);
        // symmetric and turned
        var a = Box(0, 0, 0.4f); var b = Box(9, 3, -0.7f, 1.23f, 6.3f);
        Assert.Equal(ScudReloadRules.Gap(a, b), ScudReloadRules.Gap(b, a), 4);
        Assert.True(ScudReloadRules.Gap(a, b) > 0);
        // crossing boxes (a plus sign) overlap
        Assert.Equal(0f, ScudReloadRules.Gap(Box(0, 0, 0), Box(0, 0, MathF.PI / 2)), 3);
    }

    [Fact]
    public void The_nearest_loaded_live_cart_within_reach_is_the_one_and_nothing_else_counts()
    {
        var truck = Box(0, 0);
        var near = new ScudCartCandidate(1, Box(5.5f, 0, 0, 1.23f, 6.3f), true, true);    // gap 2.7 m
        var nearer = new ScudCartCandidate(2, Box(4.6f, 3, 0, 1.23f, 6.3f), true, true);   // gap 1.8 m
        var empty = new ScudCartCandidate(3, Box(3.9f, 0, 0, 1.23f, 6.3f), false, true);
        var dead = new ScudCartCandidate(4, Box(3.9f, 0, 0, 1.23f, 6.3f), true, false);
        var far = new ScudCartCandidate(5, Box(30, 0, 0, 1.23f, 6.3f), true, true);
        Assert.Null(ScudReloadRules.NearestCart(truck, new[] { empty, dead, far }, (float)R.CartReachM));
        Assert.Equal((ushort)1, ScudReloadRules.NearestCart(truck, new[] { empty, near, dead, far }, (float)R.CartReachM)!.Value.Id);
        Assert.Equal((ushort)2, ScudReloadRules.NearestCart(truck, new[] { near, nearer, far }, (float)R.CartReachM)!.Value.Id);
        Assert.Null(ScudReloadRules.NearestCart(truck, new[] { near }, 2f));
        // hitched or not does not matter: a cart is a box
        Assert.Null(ScudReloadRules.NearestCart(truck, Array.Empty<ScudCartCandidate>(), 5f));
    }

    [Fact]
    public void Anyone_aboard_may_start_it_and_a_player_on_foot_only_beside_the_truck_and_only_when_it_is_empty()
    {
        var truck = Box(0, 0);
        Assert.True(ScudReloadRules.MayStart(ScudLauncherState.Empty, true, truck, 500, 500, R)); // aboard: wherever the box says
        Assert.True(ScudReloadRules.MayStart(ScudLauncherState.Empty, false, truck, 3f, 0f, R)); // 1.4 m from the side
        Assert.False(ScudReloadRules.MayStart(ScudLauncherState.Empty, false, truck, 8f, 0f, R)); // 6.4 m from it
        foreach (var s in Enum.GetValues<ScudLauncherState>().Where(s => s != ScudLauncherState.Empty))
            Assert.False(ScudReloadRules.MayStart(s, true, truck, 0, 0, R), s.ToString());
    }

    // ---- the key ----

    [Fact]
    public void A_tap_gets_in_or_out_and_a_hold_starts_the_loading_once()
    {
        var k = new ScudReloadKey();
        const float dt = 1f / 30f; var hold = (float)R.HoldSeconds;
        // a tap: acted on at the release
        Assert.Equal(ScudKeyAction.None, k.Tick(true, dt, hold));
        Assert.Equal(ScudKeyState.Holding, k.State);
        Assert.Equal(ScudKeyAction.None, k.Tick(true, dt, hold));
        Assert.Equal(ScudKeyAction.Tap, k.Tick(false, dt, hold));
        Assert.Equal(ScudKeyState.Idle, k.State);
        // a hold: once, then nothing while it stays down, and the release is no tap
        var actions = new List<ScudKeyAction>();
        for (var t = 0f; t < hold + 1f; t += dt) actions.Add(k.Tick(true, dt, hold));
        Assert.Single(actions.Where(a => a == ScudKeyAction.Hold));
        Assert.DoesNotContain(ScudKeyAction.Tap, actions);
        Assert.Equal(ScudKeyAction.None, k.Tick(false, dt, hold));
        Assert.Equal(ScudKeyState.Idle, k.State);
    }

    [Fact]
    public void A_key_that_is_down_when_the_offer_appears_must_be_let_go_first_and_with_no_offer_it_is_the_plain_press()
    {
        var k = new ScudReloadKey();
        const float dt = 1f / 30f; var hold = (float)R.HoldSeconds;
        k.Disarm(true); // E was held while walking up
        for (var i = 0; i < 90; i++) Assert.Equal(ScudKeyAction.None, k.Tick(true, dt, hold));
        Assert.Equal(ScudKeyAction.None, k.Tick(false, dt, hold)); // let go: not a tap either, the press was spent
        Assert.Equal(ScudKeyState.Idle, k.State);
        // the offer goes away mid-hold: back to the plain key, a new press afterwards starts over
        Assert.Equal(ScudKeyAction.None, k.Tick(true, dt, hold));
        k.Disarm(true);
        Assert.Equal(ScudKeyState.Spent, k.State);
        k.Disarm(false);
        Assert.Equal(ScudKeyState.Idle, k.State);
    }

    // ---- the chain of loaded carts ----

    [Fact]
    public void A_doomed_vehicle_keeps_its_first_date_so_a_chain_of_carts_goes_up_once_each()
    {
        var life = new VehicleLifecycle(150);
        Assert.True(life.Damage(1000, 10.0, 0.8f));
        Assert.False(life.Damage(1000, 10.5, 0.1f)); // another blast's chain reaches it: no second date
        Assert.False(life.DetonationDue(10.79));
        Assert.True(life.DetonationDue(10.81));
        life.Destroy(double.MaxValue);
        Assert.False(life.Damage(1000, 11, 0.8f)); // gone: nothing to doom again
        Assert.False(life.Doomed);
    }

    [Fact]
    public void A_cart_in_the_blast_goes_up_after_the_wave_reaches_it_and_the_chain_delay()
    {
        var b = Shipped.Blast;
        Assert.True(b.ChainDelaySeconds > 0);
        Assert.Equal(ScudBlastRules.ArrivalSeconds(b, 100f) + b.ChainDelaySeconds, ScudBlastRules.ChainDelay(b, 100f), 4);
        Assert.True(ScudBlastRules.ChainDelay(b, 140f) > ScudBlastRules.ChainDelay(b, 20f)); // the farther one later
        Assert.True(ScudBlastRules.ChainDelay(b, 0f) >= (float)b.ChainDelaySeconds - 1e-4f);
    }

    // ---- scud.json ----

    [Fact]
    public void The_shipped_reload_numbers_are_sane()
    {
        Assert.InRange(R.HoldSeconds, 0.3, 3);
        Assert.InRange(R.CartReachM, 2, 12);
        Assert.InRange(R.PlayerReachM, 1, 10);
        Assert.InRange(R.TransferShare, 0.1, 0.9);
        Assert.Equal(5, R.CartReachM);
        Assert.Equal(0.8, Shipped.Blast.ChainDelaySeconds);
    }

    [Theory]
    [InlineData("""{"version":1,"reload":{"hold_seconds":0}}""", "reload.hold_seconds")]
    [InlineData("""{"version":1,"reload":{"cart_reach_m":-1}}""", "reload.cart_reach_m")]
    [InlineData("""{"version":1,"reload":{"player_reach_m":0}}""", "reload.player_reach_m")]
    [InlineData("""{"version":1,"reload":{"moved_cancel_m":0}}""", "reload.moved_cancel_m")]
    [InlineData("""{"version":1,"reload":{"transfer_share":1}}""", "reload.transfer_share")]
    [InlineData("""{"version":1,"reload":{"transfer_share":0}}""", "reload.transfer_share")]
    [InlineData("""{"version":1,"blast":{"chain_delay_seconds":-1}}""", "blast.chain_delay_seconds")]
    public void A_bad_reload_field_is_refused_with_the_file_and_the_field_named(string json, string named)
    {
        var e = Assert.Throws<DataFileException>(() => ScudFile.Parse(json));
        Assert.Contains("scud.json", e.Message);
        Assert.Contains(named, e.Message);
    }

    // ---- the wire ----

    [Fact]
    public void The_loading_state_and_its_notices_cross_the_wire_and_one_past_them_is_refused()
    {
        var m = new ScudStateMsg(0x4001, ScudLauncherState.Loading, 1.5f, 3.5f, 0f, 0, null, 0, 0);
        var back = ScudCodec.DecodeState(ScudCodec.Encode(m))!;
        Assert.Equal(ScudLauncherState.Loading, back.State);
        Assert.Equal(3.5f, back.Left);
        var bad = ScudCodec.Encode(m); bad[2] = (byte)ScudLauncherState.Loading + 1;
        Assert.Null(ScudCodec.DecodeState(bad));
        Assert.Equal(ScudNoticeCode.LoadStarted, ScudCodec.DecodeNotice(ScudCodec.Encode(ScudNoticeCode.LoadStarted)));
        Assert.Equal(ScudNoticeCode.LoadCancelled, ScudCodec.DecodeNotice(ScudCodec.Encode(ScudNoticeCode.LoadCancelled)));
        Assert.Null(ScudCodec.DecodeNotice(new byte[] { (byte)ScudNoticeCode.EventFound + 1 }));
        Assert.Equal(ScudNoticeCode.LoadCancelled, ScudCodec.NoticeOf(ScudLoadCancel.Moved));
        Assert.True(Wire.ProtocolVersion >= 69);
    }
}
