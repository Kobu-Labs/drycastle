using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class InteractTests
{
    [Fact]
    public void Owner_matches_the_old_hand_written_arbitration_for_every_combination()
    {
        for (var i = 0; i < 32; i++)
        {
            bool crate = (i & 1) != 0, ground = (i & 2) != 0, nearest = (i & 4) != 0, corpse = (i & 8) != 0, chooser = (i & 16) != 0;
            // NetClient.GroundItems.cs: GroundItemOwnsKey
            var groundOwns = ground && nearest && !crate && !chooser;
            // NetClient.Corpses.cs TickCorpses: near = crate or ground owns the key ? null : _near
            var corpseNear = crate || groundOwns ? false : corpse;
            var owner = InteractArbiter.Owner(crate, ground, nearest, corpse, chooser);
            Assert.Equal(groundOwns, owner == InteractOwner.GroundItem);
            Assert.Equal(corpseNear, owner == InteractOwner.Corpse);
            Assert.Equal(crate, owner == InteractOwner.Crate);
            Assert.Equal(!crate && !groundOwns && !corpse, owner == InteractOwner.None);
        }
    }

    [Fact]
    public void Corpse_prompt_stays_quiet_while_a_crate_owns_the_key()
    {
        // regression: CratePrompt steps aside during the hold (the ring shows it), and the corpse prompt then showed through. Old CorpsePrompt: corpse && !chooser && !groundOwns.
        Assert.False(InteractArbiter.CorpseShowsPrompt(crateInReach: true, groundItemInReach: false, groundItemNearest: false, corpseInReach: true, chooserOpen: false));
        for (var i = 0; i < 32; i++)
        {
            bool crate = (i & 1) != 0, ground = (i & 2) != 0, nearest = (i & 4) != 0, corpse = (i & 8) != 0, chooser = (i & 16) != 0;
            var groundOwns = ground && nearest && !crate && !chooser;
            var old = corpse && !chooser && !groundOwns;
            Assert.Equal(old && !crate, InteractArbiter.CorpseShowsPrompt(crate, ground, nearest, corpse, chooser));
        }
    }

    [Fact]
    public void Interact_key_edges_equal_use_and_not_was()
    {
        var rng = new Random(5);
        var k = new InteractKey();
        var was = false;
        for (var i = 0; i < 2000; i++)
        {
            var use = rng.Next(3) != 0 ? was : !was; // runs of held and released
            var (pressed, released) = k.Step(use);
            Assert.Equal(use && !was, pressed);
            Assert.Equal(!use && was, released);
            was = use;
            Assert.Equal(use, k.Held);
        }
    }

    /// <summary>NetClient.CarePackages.cs TickCarePackages before the extraction.</summary>
    sealed class OldCrate
    {
        public double Held; public bool Sent; public int Sends;
        public void Tick(bool use, bool near, double dt, double hold)
        {
            if (!use) { Held = 0; Sent = false; }
            else if (!near) { Held = 0; }
            else if (!Sent)
            {
                Held += dt;
                if (Held >= hold) { Sends++; Sent = true; }
            }
        }
    }

    [Fact]
    public void Crate_hold_equals_the_old_inline_code()
    {
        var rng = new Random(77);
        for (var run = 0; run < 100; run++)
        {
            var old = new OldCrate();
            var hold = new CrateHold();
            var sends = 0;
            bool use = false, near = false;
            for (var i = 0; i < 600; i++)
            {
                if (rng.Next(12) == 0) use = !use;
                if (rng.Next(15) == 0) near = !near;
                if (rng.Next(200) == 0) { old.Held = 0; old.Sent = false; hold.Reset(); } // the crate despawned
                var dt = 1.0 / 30;
                old.Tick(use, near, dt, 2.0);
                if (hold.Tick(use, near, dt, 2.0)) sends++;
                Assert.Equal(old.Held, hold.Held);
                Assert.Equal(old.Sent, hold.Sent);
                Assert.Equal(old.Sends, sends);
            }
        }
    }

    [Fact]
    public void Crate_opens_once_per_hold_and_not_after_leaving_reach_mid_hold()
    {
        var h = new CrateHold();
        var sent = 0;
        for (var i = 0; i < 90; i++) if (h.Tick(true, true, 1.0 / 30, 2.0)) sent++;
        Assert.Equal(1, sent);
        Assert.True(h.Sent);
        Assert.False(h.Tick(true, false, 1.0 / 30, 2.0)); // reach lost: the time restarts, the sent flag holds
        Assert.Equal(0, h.Held);
        Assert.True(h.Sent);
        Assert.False(h.Tick(true, true, 1.0, 2.0));
        Assert.False(h.Tick(false, true, 1.0 / 30, 2.0)); // released: ready for the next hold
        Assert.False(h.Sent);
    }
}
