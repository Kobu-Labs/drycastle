using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class PerkTests
{
    static readonly string PerksPath = Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/perks.json");
    static readonly string WeaponsPath = Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json");
    static readonly PerksFile Perks = PerksFile.Load(PerksPath);
    static readonly WeaponsFile Weapons = WeaponsFile.Load(WeaponsPath);
    const double Dt = 1.0 / 30;

    static PerkModifiers Of(string id) => PerkModifiers.Of(Perks.Find(id)!);

    // ---- data file ------------------------------------------------------------------------------------------------

    [Fact]
    public void ShippedFileHasTheEightPerksWithUnlockLevels()
    {
        Assert.Equal(new[] { "fleet_foot", "quick_hands", "flak_jacket", "swift_swap", "tank_hunter", "hot_streak", "steady_lung", "ghost" },
            Perks.Perks.Select(p => p.Id));
        Assert.Equal("Fleet Foot", Perks.Find("fleet_foot")!.Name);
        Assert.Equal(0, Perks.Find("quick_hands")!.UnlockLevel);
        Assert.True(Perks.Perks.All(p => p.UnlockLevel >= 0));
    }

    [Fact]
    public void EachPerkSetsOnlyItsOwnModifier()
    {
        Assert.Equal(1.12f, Of("fleet_foot").RunSpeed, 4);
        Assert.Equal(1.33f, Of("quick_hands").ReloadSpeed, 4);
        Assert.Equal(0.8f, Of("flak_jacket").ExplosiveDamageTaken, 4);
        Assert.Equal(1.5f, Of("swift_swap").WeaponSwapSpeed, 4);
        Assert.Equal(1.25f, Of("tank_hunter").VehicleDamageDealt, 4);
        Assert.Equal(1f, Of("hot_streak").StreakCostReduction, 4);
        Assert.Equal(1.5f, Of("steady_lung").HoldBreathTime, 4);
        Assert.Equal(0f, Of("ghost").UavVisibility, 4);
        // the ids for effects that have no consumer yet are defined and neutral in the other perks
        Assert.Equal(1f, Of("fleet_foot").UavVisibility);
        Assert.Equal(0f, Of("fleet_foot").StreakCostReduction);
    }

    [Fact]
    public void NoPerkIsAllNeutral()
    {
        var m = PerkModifiers.Of(null);
        Assert.Equal(1f, m.RunSpeed);
        Assert.Equal(1f, m.ReloadSpeed);
        Assert.Equal(1f, m.ExplosiveDamageTaken);
        Assert.Equal(1f, m.WeaponSwapSpeed);
        Assert.Equal(1f, m.VehicleDamageDealt);
        Assert.Equal(0f, m.StreakCostReduction);
        Assert.Equal(1f, m.HoldBreathTime);
        Assert.Equal(1f, m.UavVisibility);
        Assert.Same(PerkModifiers.None, m);
    }

    [Fact]
    public void PerksDoNotStack()
    {
        // a loadout carries one perk: the modifiers of another perk never leak in
        Assert.Equal(1f, Of("quick_hands").RunSpeed);
        Assert.Equal(1f, Of("fleet_foot").ReloadSpeed);
        Assert.Equal(1.12f, Of("fleet_foot").RunSpeed, 4);
        Assert.Equal(1.12f, PerkModifiers.Of(Perks.Find("fleet_foot")).RunSpeed, 4);
    }

    [Fact]
    public void LookupByIdFindsOnlyKnownPerks()
    {
        Assert.Null(Perks.Find("nope"));
        Assert.NotNull(Perks.Find("ghost"));
    }

    [Theory]
    [InlineData("{\"version\":1,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{\"run_speeed\":1.1}}]}", "unknown modifier")]
    [InlineData("{\"version\":1,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{\"run_speed\":-1}}]}", "must be a finite number >= 0")]
    [InlineData("{\"version\":1,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{\"run_speed\":0}}]}", "must be > 0")]
    [InlineData("{\"version\":1,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"unlock_level\":-2,\"modifiers\":{}}]}", "unlock_level")]
    [InlineData("{\"version\":1,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{}},{\"id\":\"a\",\"name\":\"B\",\"modifiers\":{}}]}", "duplicate perk id")]
    [InlineData("{\"version\":1,\"perks\":[{\"name\":\"A\",\"modifiers\":{}}]}", "field \"id\" is required")]
    [InlineData("{\"version\":1,\"perks\":[]}", "no perks")]
    [InlineData("{\"version\":2,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{}}]}", "newer than supported")]
    [InlineData("{\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{}}]}", "version")]
    public void InvalidFilesAreRejected(string json, string message)
    {
        var e = Assert.Throws<DataFileException>(() => PerksFile.Parse(json));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void ZeroIsAllowedForUavVisibilityAndKillReduction()
    {
        var f = PerksFile.Parse("{\"version\":1,\"perks\":[{\"id\":\"a\",\"name\":\"A\",\"modifiers\":{\"uav_visibility\":0,\"streak_cost_reduction\":0}}]}");
        Assert.Equal(0f, PerkModifiers.Of(f.Find("a")).UavVisibility);
    }

    // ---- shared weapon step: reload, swap, run speed ---------------------------------------------------------------

    static PlayerWeapons Armed(PerkModifiers? perks, double t = 10)
    {
        var w = new PlayerWeapons(Weapons, perks);
        w.Reset(t);
        return w;
    }

    static FireOutcome? Tick(PlayerWeapons w, ref double t, bool fire = false, bool reload = false, int slot = 0)
    {
        t += Dt;
        return w.Update(t, new WeaponInput(fire, reload, slot, false));
    }

    [Fact]
    public void QuickHandsShortensTheReload()
    {
        var ak = Weapons.Find("ak47")!;
        double tp = 10, tq = 10;
        var plain = Armed(null, tp); var quick = Armed(Of("quick_hands"), tq);
        Tick(plain, ref tp, slot: 2); Tick(quick, ref tq, slot: 2); // slot 2 = the ak47 (primary)
        Assert.Equal("ak47", plain.Current.Id);
        // one round out of the full magazine, so a reload is allowed
        Tick(plain, ref tp, fire: true, slot: 2); Tick(quick, ref tq, fire: true, slot: 2);
        Tick(plain, ref tp, reload: true, slot: 2); Tick(quick, ref tq, reload: true, slot: 2);
        Assert.True(plain.Reloading && quick.Reloading);
        Assert.Equal(ak.ReloadTime, plain.ReloadRemaining(tp), 2 * Dt);
        Assert.Equal(ak.ReloadTime / 1.33f, quick.ReloadRemaining(tq), 2 * Dt);
    }

    [Fact]
    public void QuickHandsShortensEveryShellOfAShellReload()
    {
        var rem = Weapons.Find("remington870")!;
        var slot = Weapons.Weapons.ToList().FindIndex(x => x.Id == "remington870") + 1;
        double t = 10;
        var w = Armed(Of("quick_hands"), t);
        Tick(w, ref t, slot: slot);
        Assert.Equal("remington870", w.Current.Id);
        for (var i = 0; i < 400 && w.Mag(slot) > rem.MagazineSize - 2; i++) Tick(w, ref t, fire: i % 2 == 0, slot: slot);
        Assert.Equal(rem.MagazineSize - 2, w.Mag(slot));
        for (var i = 0; i < 400 && w.Reloading == false; i++) Tick(w, ref t, reload: true, slot: slot);
        Assert.True(w.Reloading);
        var start = w.Mag(slot);
        for (var i = 0; i < 1000 && w.Mag(slot) == start; i++) Tick(w, ref t, slot: slot); // the first shell goes in
        Assert.True(w.Reloading);
        Assert.Equal(rem.ReloadTime / 1.33f, w.ReloadRemaining(t), 2 * Dt); // the second shell is quick too
    }

    [Fact]
    public void FleetFootScalesTheSpeedOfEveryWeapon()
    {
        var plain = Armed(null); var fast = Armed(Of("fleet_foot"));
        for (var slot = 1; slot <= Weapons.Weapons.Count; slot++)
            Assert.Equal(plain.SpeedFor(slot) * 1.12f, fast.SpeedFor(slot), 4);
    }

    /// <summary>Ticks after the draw until the first shot. Presses alternate every tick (the pistol is semi-auto: a held button is one press).</summary>
    static int TicksToFirstShotAfterDrawing(PerkModifiers? perks, int drawSlot)
    {
        double t = 10;
        var w = Armed(perks, t);
        // start on the ak (slot 2) so the switch to the pistol (slot 1, the secondary) is a real draw
        Tick(w, ref t, slot: 2);
        Tick(w, ref t, slot: drawSlot);
        for (var n = 1; n <= 60; n++)
            if (Tick(w, ref t, fire: n % 2 == 1, slot: drawSlot) != null) return n;
        return -1;
    }

    [Fact]
    public void DrawingTheSecondaryTakesTimeAndSwiftSwapShortensIt()
    {
        var plain = TicksToFirstShotAfterDrawing(null, 1);
        var swift = TicksToFirstShotAfterDrawing(Of("swift_swap"), 1);
        // 0.35 s base is 11 ticks at 30 Hz, Swift Swap (1.5x) is 0.233 s, 7 ticks
        Assert.InRange(plain, 10, 12);
        Assert.InRange(swift, 6, 8);
        Assert.True(swift < plain);
    }

    [Fact]
    public void SwitchingToAPrimaryHasNoDrawDelay()
    {
        // from the pistol to the ak: the first trigger pull fires on the next tick
        double t = 10;
        var w = Armed(null, t);
        Tick(w, ref t, slot: 2);
        Assert.NotNull(Tick(w, ref t, fire: true, slot: 2));
    }

    [Fact]
    public void SwiftSwapDoesNotChangeThePrimaryDraw()
    {
        double t = 10;
        var w = Armed(Of("swift_swap"), t);
        Tick(w, ref t, slot: 2);
        Assert.NotNull(Tick(w, ref t, fire: true, slot: 2));
    }

    [Fact]
    public void SpeedWithoutPerksIsUnchanged()
    {
        var w = Armed(null);
        Assert.Equal(Weapons.Find("pistol")!.MoveSpeedMultiplier, w.SpeedFor(1));
    }
}
