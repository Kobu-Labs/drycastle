using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Player statuses (curse, blessing) and the burn the curse causes.</summary>
public class StatusTests
{
    static readonly BurnDef Burn = new() { Dps = 0.5f, DurationS = 10, TickIntervalS = 1 };

    /// <summary>Steps a burn set at 30 Hz for <paramref name="seconds"/>; returns the ticks.</summary>
    static List<BurnTick> Run(BurnSet set, float seconds)
    {
        var ticks = new List<BurnTick>();
        for (var i = 0; i < (int)MathF.Round(seconds * 30); i++) set.Step(1f / 30, ticks);
        return ticks;
    }

    [Fact]
    public void Grant_has_remove_and_mask()
    {
        var s = new PlayerStatuses();
        Assert.False(s.Any);
        Assert.True(s.Grant(StatusKind.Curse));
        Assert.False(s.Grant(StatusKind.Curse));
        Assert.True(s.Has(StatusKind.Curse));
        Assert.False(s.Has(StatusKind.Blessing));
        Assert.True(s.Grant(StatusKind.Blessing));
        Assert.Equal(0b11, s.Mask);
        Assert.True(s.Remove(StatusKind.Blessing));
        Assert.False(s.Remove(StatusKind.Blessing));
        Assert.Equal(0b01, s.Mask);
    }

    [Fact]
    public void Death_clears_every_status_and_marks_changed()
    {
        var s = new PlayerStatuses();
        s.Grant(StatusKind.Curse); s.Grant(StatusKind.Blessing);
        s.Changed = false;
        s.OnDeath();
        Assert.False(s.Any);
        Assert.True(s.Changed);
        s.Changed = false;
        s.OnDeath(); // nothing to clear: nothing to tell
        Assert.False(s.Changed);
    }

    [Fact]
    public void A_client_copy_follows_the_mask()
    {
        var a = new PlayerStatuses(); a.Grant(StatusKind.Curse);
        var b = new PlayerStatuses(); b.SetMask(a.Mask);
        Assert.True(b.Has(StatusKind.Curse));
    }

    [Fact]
    public void A_full_burn_deals_dps_times_duration_in_whole_ticks()
    {
        var set = new BurnSet();
        set.Ignite(7, 3, Burn);
        var ticks = Run(set, 12);
        Assert.Equal(5f, ticks.Sum(t => t.Damage), 0.01f);
        Assert.All(ticks, t => { Assert.Equal((ushort)7, t.Victim); Assert.Equal((ushort)3, t.Source); });
        Assert.Equal(10, ticks.Count);
        Assert.False(set.IsBurning(7));
    }

    [Fact]
    public void A_new_hit_refreshes_the_burn_and_does_not_stack()
    {
        var set = new BurnSet();
        set.Ignite(7, 3, Burn);
        var ticks = Run(set, 5);
        set.Ignite(7, 4, Burn); // another cursed shooter, 5 s in
        Assert.Equal(1, set.Count);
        ticks.AddRange(Run(set, 12));
        Assert.Equal(7.5f, ticks.Sum(t => t.Damage), 0.02f); // 5 s at 0.5 + 10 s at 0.5, never 1.0/s
        Assert.Equal((ushort)4, ticks[^1].Source); // the latest igniter is credited
    }

    [Fact]
    public void Extinguish_stops_the_damage()
    {
        var set = new BurnSet();
        set.Ignite(7, 3, Burn);
        Run(set, 2);
        set.Extinguish(7);
        Assert.Empty(Run(set, 12));
    }

    [Fact]
    public void Only_a_cursed_shooter_ignites_and_never_themselves()
    {
        var set = new BurnSet();
        var cursed = new PlayerStatuses(); cursed.Grant(StatusKind.Curse);
        Assert.False(CurseRules.OnHit(new PlayerStatuses(), 1, 2, set, Burn));
        Assert.False(CurseRules.OnHit(null, 1, 2, set, Burn));
        Assert.False(CurseRules.OnHit(cursed, 1, 1, set, Burn));
        Assert.True(CurseRules.OnHit(cursed, 1, 2, set, Burn));
        Assert.True(set.IsBurning(2));
    }

    [Fact]
    public void Grant_curses_the_credited_who_are_still_there()
    {
        var a = new PlayerStatuses(); var b = new PlayerStatuses();
        var granted = CurseRules.Grant(new ushort[] { 1, 2, 3 }, id => id switch { 1 => a, 2 => b, _ => null });
        Assert.Equal(new ushort[] { 1, 2 }, granted);
        Assert.True(a.Has(StatusKind.Curse));
        Assert.Empty(CurseRules.Grant(new ushort[] { 1 }, _ => a)); // already cursed
    }
}
