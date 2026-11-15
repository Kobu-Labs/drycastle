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
    [Fact]
    public void Burn_pays_whole_points_five_over_a_full_burn()
    {
        var set = new BurnSet(); var pay = new BurnPayout();
        set.Ignite(2, 1, CurseBurn.Default);
        var total = 0; var firstAt = -1f; var t = 0f;
        var ticks = new List<BurnTick>();
        for (var i = 0; i < 12 * 30; i++, t += 1f / 30)
        {
            ticks.Clear(); set.Step(1f / 30, ticks);
            foreach (var k in ticks) { var w = pay.Whole(k); if (w > 0 && firstAt < 0) firstAt = t; total += w; }
        }
        Assert.Equal(5, total);
        Assert.InRange(firstAt, 1.8f, 2.2f); // 1 hp every 2 s
        Assert.False(set.IsBurning(2));
    }

    [Fact]
    public void Blessing_negates_one_lethal_hit_without_a_kill_then_protects_briefly()
    {
        double now = 0;
        var m = new MatchSim(new MatchConfig(), () => now, new[] { new System.Numerics.Vector3(0, 0, 0), new System.Numerics.Vector3(100, 0, 0) }, new Random(1));
        m.AddPlayer(1, "a"); m.AddPlayer(2, "b");
        now += 3.5; m.Update(); // spawn protection over
        var statuses = new PlayerStatuses(); statuses.Grant(StatusKind.Blessing);
        m.SaveFromDeath = id => id == 2 && BlessingRules.TryConsume(statuses);
        m.ApplyDamage(2, 1, 0, 40);
        Assert.Equal(DamageResult.Saved, m.ApplyDamage(2, 1, 0, 200));
        Assert.Equal(MoveConsts.MaxHealth, m.Get(2)!.Health);
        Assert.Equal(PlayerLife.Alive, m.Get(2)!.Life);
        Assert.Equal(0, m.Get(1)!.Kills);
        Assert.Equal(0, m.Get(2)!.Deaths);
        Assert.False(statuses.Has(StatusKind.Blessing));
        Assert.Equal(DamageResult.Ignored, m.ApplyDamage(2, 1, 0, 200)); // invulnerable for a second
        now += BlessingRules.InvulnerableS + 0.1;
        Assert.Equal(DamageResult.Killed, m.ApplyDamage(2, 1, 0, 200)); // used up
        Assert.Equal(1, m.Get(1)!.Kills);
    }

    [Fact]
    public void Status_codec_round_trips()
    {
        Assert.True(StatusCodec.TryDecodeState(StatusCodec.EncodeState(7, 3, true), out var id, out var mask, out var burning));
        Assert.Equal(((ushort)7, (byte)3, true), (id, mask, burning));
        Assert.True(StatusCodec.TryDecodeSaved(StatusCodec.EncodeSaved(9), out var saved));
        Assert.Equal((ushort)9, saved);
        Assert.False(StatusCodec.TryDecodeState(new byte[3], out _, out _, out _));
    }
}
