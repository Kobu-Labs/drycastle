using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class MoabBannerModelTests
{
    /// <summary>What a frame of the banner shows (text as a token, the panel and the flash).</summary>
    readonly record struct Shown(string Text, bool Panel, bool Flash, float FlashAlpha);

    /// <summary>MoabBanner.cs as it was: three sentinel timers and a panel flag written from four places.</summary>
    sealed class Old
    {
        double _countdownEnds = -1, _bannerEnds = -1, _flashEnds = -1;
        int _shownSeconds = -1;
        public string Text = "";
        public bool Panel, Flash;
        public float FlashAlpha;

        public void Armed(double now, float seconds) { _countdownEnds = now + Math.Max(0, seconds); _bannerEnds = -1; _shownSeconds = -1; ShowCountdown(now); }
        public void Blast(double now, string owner, bool reduce)
        {
            _countdownEnds = -1; _bannerEnds = now + 3.0; _flashEnds = reduce ? -1 : now + 0.6;
            Flash = _flashEnds > 0; Text = "blast:" + owner; Panel = true;
        }
        public void Cancelled(double now) { _countdownEnds = -1; _bannerEnds = now + 3.0; Text = "cancelled"; Panel = true; }
        public void Process(double now)
        {
            if (_countdownEnds >= 0)
            {
                if (now >= _countdownEnds) _countdownEnds = -1;
                else ShowCountdown(now);
            }
            if (_bannerEnds >= 0 && now >= _bannerEnds && _countdownEnds < 0) { _bannerEnds = -1; Panel = false; }
            if (_flashEnds > 0)
            {
                var k = (float)Math.Clamp((_flashEnds - now) / 0.6, 0, 1);
                FlashAlpha = 0.85f * k;
                if (now >= _flashEnds) { _flashEnds = -1; Flash = false; }
            }
            if (_countdownEnds < 0 && _bannerEnds < 0) Panel = false;
        }
        void ShowCountdown(double now)
        {
            var left = (int)Math.Ceiling(Math.Max(0, _countdownEnds - now));
            if (left == _shownSeconds && Panel) return;
            _shownSeconds = left;
            Text = "inbound:" + left;
            Panel = true;
        }
    }

    /// <summary>The view glue on top of the model, as MoabBanner.cs now does it.</summary>
    sealed class New
    {
        readonly MoabBannerModel _m = new();
        Change<int> _shownSeconds;
        public string Text = "";
        public bool Panel, Flash;
        public float FlashAlpha;
        public MoabBannerPhase Phase => _m.Phase;

        public void Armed(double now, float seconds) { _m.Armed(now, seconds); _shownSeconds.Reset(); ShowCountdown(now); }
        public void Blast(double now, string owner, bool reduce)
        {
            _m.Blast(now, owner, reduce);
            Flash = _m.FlashActive; Text = "blast:" + owner; Panel = _m.PanelVisible;
        }
        public void Cancelled(double now) { _m.Cancelled(now); Text = "cancelled"; Panel = _m.PanelVisible; }
        public void Process(double now)
        {
            _m.Tick(now);
            if (_m.Phase == MoabBannerPhase.Countdown) ShowCountdown(now);
            if (_m.FlashActive) FlashAlpha = _m.FlashAlpha(now);
            else if (Flash) FlashAlpha = 0; // it ended: leave the colour clear for the next one (the old code did this by computing k = 0 on the last frame)
            Flash = _m.FlashActive;
            Panel = _m.PanelVisible;
        }
        void ShowCountdown(double now)
        {
            var left = _m.SecondsLeft(now);
            if (!_shownSeconds.Update(left) && Panel) return;
            Text = "inbound:" + left;
            Panel = _m.PanelVisible;
        }
    }

    [Fact]
    public void Model_with_its_glue_shows_what_the_three_timer_banner_showed()
    {
        var rng = new Random(2024);
        for (var run = 0; run < 300; run++)
        {
            var old = new Old();
            var now = new New();
            double t = 100 + rng.NextDouble() * 1000;
            var reduce = rng.Next(3) == 0;
            for (var step = 0; step < 400; step++)
            {
                t += rng.Next(4) == 0 ? 0 : 1.0 / 60 + rng.NextDouble() * 0.05;
                var processed = false;
                switch (rng.Next(14))
                {
                    case 0: { var s = (float)(rng.Next(7) == 0 ? 0 : rng.NextDouble() * 8); old.Armed(t, s); now.Armed(t, s); break; }
                    case 1: old.Blast(t, "bob", reduce); now.Blast(t, "bob", reduce); break;
                    case 2: old.Cancelled(t); now.Cancelled(t); break;
                    default: old.Process(t); now.Process(t); processed = true; break;
                }
                Assert.Equal(old.Panel, now.Panel);
                Assert.Equal(old.Flash, now.Flash);
                if (old.Panel) Assert.Equal(old.Text, now.Text);
                if (old.Flash && processed) Assert.Equal(old.FlashAlpha, now.FlashAlpha);
            }
        }
    }

    [Fact]
    public void Phases_follow_the_events()
    {
        var m = new MoabBannerModel();
        Assert.Equal(MoabBannerPhase.Idle, m.Phase);
        Assert.False(m.PanelVisible);
        m.Armed(10, 5);
        Assert.Equal(MoabBannerPhase.Countdown, m.Phase);
        Assert.Equal(5, m.SecondsLeft(10));
        Assert.Equal(5, m.SecondsLeft(10.5)); // rounded up
        Assert.Equal(1, m.SecondsLeft(14.5));
        m.Tick(14.99);
        Assert.Equal(MoabBannerPhase.Countdown, m.Phase);
        m.Tick(15);
        Assert.Equal(MoabBannerPhase.Idle, m.Phase); // the countdown ran out with no Blast: the panel goes
        m.Armed(20, 3);
        m.Blast(21, "bob", reduceMotion: false);
        Assert.Equal(MoabBannerPhase.Blast, m.Phase);
        Assert.Equal("bob", m.Owner);
        Assert.True(m.FlashActive);
        m.Tick(21.6);
        Assert.False(m.FlashActive);
        Assert.Equal(MoabBannerPhase.Blast, m.Phase);
        m.Tick(24);
        Assert.Equal(MoabBannerPhase.Idle, m.Phase);
        m.Cancelled(30);
        Assert.Equal(MoabBannerPhase.Cancelled, m.Phase);
        m.Armed(31, 2); // a new countdown replaces the banner
        Assert.Equal(MoabBannerPhase.Countdown, m.Phase);
        m.Blast(32, "al", reduceMotion: true);
        Assert.False(m.FlashActive);
    }

    [Fact]
    public void A_flash_outlives_a_rearmed_countdown()
    {
        var m = new MoabBannerModel();
        m.Blast(0, "x", false);
        m.Armed(0.1, 5);
        Assert.True(m.FlashActive);
        Assert.Equal(MoabBannerPhase.Countdown, m.Phase);
    }
}
