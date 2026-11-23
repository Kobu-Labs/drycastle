using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #117: the loadout hotbar, the starter kit against the shipped data, and the XP event payloads.</summary>
public class LoadoutWiringTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    static int IndexOf(string id) => Shipped.Weapons.Weapons.FindIndex(w => w.Id == id);

    static Loadout Kit(string primary = "ak47", string secondary = "deagle") => new(0, primary, secondary, "fleet_foot", "uav", "", "");

    // ---- hotbar -------------------------------------------------------------------------------------------------

    [Fact]
    public void HotbarIsPrimarySecondaryAndKnifeInThatOrder()
    {
        var hotbar = LoadoutHotbar.Build(Shipped.Weapons, Kit("m4a1", "deagle"));
        Assert.Equal(new[] { IndexOf("m4a1"), IndexOf("deagle"), IndexOf("knife"), IndexOf("grenade") }, hotbar);
    }

    [Fact]
    public void HotbarFallsBackToTheStarterWeaponsForAMissingOrWrongSlotItem()
    {
        // a sniper in the secondary position is the wrong slot type; a missing primary falls back to an unlock-0 primary
        var hotbar = LoadoutHotbar.Build(Shipped.Weapons, new Loadout(0, "", "sniper", "", "", "", ""));
        Assert.Equal(4, hotbar.Length);
        Assert.Equal(Shipped.Weapons.Weapons[hotbar[0]].Slot, WeaponSlot.Primary);
        Assert.Equal(0, Shipped.Weapons.Weapons[hotbar[0]].UnlockLevel);
        Assert.Equal(WeaponSlot.Secondary, Shipped.Weapons.Weapons[hotbar[1]].Slot);
        Assert.Equal("knife", Shipped.Weapons.Weapons[hotbar[2]].Id);
        Assert.Equal("grenade", Shipped.Weapons.Weapons[hotbar[3]].Id);
    }

    [Fact]
    public void OnlyTheHotbarCanBeSelectedAndNextCyclesIt()
    {
        var hotbar = LoadoutHotbar.Build(Shipped.Weapons, Kit());
        var w = new PlayerWeapons(Shipped.Weapons, null, hotbar);
        w.Reset(0);
        Assert.Equal("ak47", w.Current.Id); // spawn with the primary
        Assert.Equal(4, w.Hotbar.Count);

        // the rpg is in weapons.json but not on the hotbar: selecting it does nothing
        w.Update(1, new WeaponInput(false, false, IndexOf("rpg") + 1, false));
        Assert.Equal("ak47", w.Current.Id);

        // the cycle goes primary -> secondary -> knife -> grenade -> primary, skipping every other weapon
        var ids = new List<string>();
        var now = 2.0;
        for (var i = 0; i < 4; i++)
        {
            w.Update(now, new WeaponInput(false, false, 0, true));
            ids.Add(w.Current.Id);
            now += 1;
            w.Update(now, new WeaponInput(false, false, 0, false)); // the next edge is per press
            now += 1;
        }
        Assert.Equal(new[] { "deagle", "knife", "grenade", "ak47" }, ids);
    }

    [Fact]
    public void KeyOnTheHotbarSelectsThatWeapon()
    {
        var w = new PlayerWeapons(Shipped.Weapons, null, LoadoutHotbar.Build(Shipped.Weapons, Kit()));
        w.Reset(0);
        w.Update(1, new WeaponInput(false, false, IndexOf("knife") + 1, false));
        Assert.Equal("knife", w.Current.Id);
    }

    [Fact]
    public void AmmoStillListsEveryWeaponSoTheSlotsLineUp()
    {
        var w = new PlayerWeapons(Shipped.Weapons, null, LoadoutHotbar.Build(Shipped.Weapons, Kit()));
        Assert.Equal(Shipped.Weapons.Weapons.Count, w.WeaponCount);
    }

    [Fact]
    public void HotbarFallsBackFromARetiredWeaponToTheFirstUnlockZeroKeptOneOfTheSlot()
    {
        var weapons = Shipped.Weapons.Weapons;
        var firstPrimary = weapons.First(w => w.Slot == WeaponSlot.Primary && w.UnlockLevel == 0 && !w.Retired);
        var firstSecondary = weapons.First(w => w.Slot == WeaponSlot.Secondary && w.UnlockLevel == 0 && !w.Retired);
        foreach (var r in weapons.Where(w => w.Retired))
        {
            var kit = r.Slot == WeaponSlot.Primary ? new Loadout(0, r.Id, "", "", "", "", "") : new Loadout(0, "", r.Id, "", "", "", "");
            var hotbar = LoadoutHotbar.Build(Shipped.Weapons, kit);
            Assert.DoesNotContain(IndexOf(r.Id), hotbar);
            Assert.Equal(r.Slot == WeaponSlot.Primary ? IndexOf(firstPrimary.Id) : IndexOf(firstSecondary.Id), hotbar[r.Slot == WeaponSlot.Primary ? 0 : 1]);
        }
    }

    // ---- the weapon cut (#477) --------------------------------------------------------------------------------------

    [Fact]
    public void EveryRetiredWeaponIsRefusedOnSave()
    {
        var levels = Shipped.Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);
        var kit = LoadoutRules.DefaultLoadout(0, Shipped.Weapons, Shipped.Perks);
        var retired = Shipped.Weapons.Weapons.Where(w => w.Retired).ToList();
        Assert.NotEmpty(retired);
        foreach (var r in retired)
        {
            var bad = r.Slot == WeaponSlot.Primary
                ? new Loadout(0, r.Id, kit.SecondaryWeapon, kit.Perk, kit.Streak1, "", "")
                : new Loadout(0, kit.PrimaryWeapon, r.Id, kit.Perk, kit.Streak1, "", "");
            var owned = new[] { new Unlock("weapon", r.Id) }; // owning the unlock does not bring it back
            Assert.Contains("no longer available", LoadoutRules.ValidateLoadout(bad, Shipped.Weapons, 99, owned, levels, Shipped.Killstreaks.UnlockLevels));
        }
    }

    [Fact]
    public void TheDefaultLoadoutIsAkAndDeagle()
    {
        var kit = LoadoutRules.DefaultLoadout(0, Shipped.Weapons, Shipped.Perks);
        Assert.Equal(("ak47", "deagle"), (kit.PrimaryWeapon, kit.SecondaryWeapon));
    }

    [Fact]
    public void NoKeptSniperKillsInOneBodyShotAndEveryHeadshotKills()
    {
        var snipers = Shipped.Weapons.Weapons.Where(w => w.Category == WeaponCategory.Sniper && !w.Retired && !w.IsReward).ToList();
        Assert.NotEmpty(snipers);
        foreach (var w in snipers)
        {
            Assert.True(w.Damage < 100, $"{w.Id} one-shots the body");
            Assert.True(w.Damage * w.HeadshotMultiplier >= 100, $"{w.Id} headshot does not kill");
        }
    }

    // ---- starter kit against the shipped data ----------------------------------------------------------------------

    [Fact]
    public void TheStarterKitIsValidAgainstTheShippedData()
    {
        var levels = Shipped.Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);
        var kit = LoadoutRules.DefaultLoadout(0, Shipped.Weapons, Shipped.Perks);
        Assert.Equal("fleet_foot", kit.Perk);
        Assert.Equal("uav", kit.Streak1);
        Assert.Null(LoadoutRules.ValidateLoadout(kit, Shipped.Weapons, 1, Array.Empty<Unlock>(), levels, Shipped.Killstreaks.UnlockLevels));
    }

    [Fact]
    public void ShippedStreakTableIsTheMapsKillstreakList()
    {
        // ids and kill thresholds from mods/shooter/v2/killstreaks.md
        var table = Shipped.Killstreaks.Streaks.Select(s => (s.Id, s.KillsRequired)).ToList();
        Assert.Equal(new[] { ("uav", 4), ("attack_missile", 5), ("turret", 6), ("care_package", 6), ("attack_helicopter", 10),
            ("helicopter_helper", 15), ("ac130", 18), ("moab", 24) }, table);
    }

    // ---- XP event payloads ------------------------------------------------------------------------------------------

    [Fact]
    public void LevelUpRoundTrips()
    {
        var info = new LevelUpInfo(12, 3020);
        Assert.Equal(info, ProgressionEvents.DecodeLevelUp(ProgressionEvents.EncodeLevelUp(info)));
        Assert.Null(ProgressionEvents.DecodeLevelUp(new byte[5]));
    }

    [Fact]
    public void XpSummaryRoundTripsWithItsLinesAndTotal()
    {
        var lines = new List<XpLine> { new(XpReason.Kill, 100), new(XpReason.Headshot, 25), new(XpReason.Roadkill, 130), new(XpReason.MatchTop3, 100) };
        var info = new XpSummaryInfo(4321, 9, lines);
        var back = ProgressionEvents.DecodeXpSummary(ProgressionEvents.EncodeXpSummary(info));
        Assert.NotNull(back);
        Assert.Equal(4321, back!.Xp);
        Assert.Equal(9, back.Level);
        Assert.Equal(lines, back.Lines);
        Assert.Equal(355, back.Total);

        var bytes = ProgressionEvents.EncodeXpSummary(info);
        Assert.Null(ProgressionEvents.DecodeXpSummary(bytes.AsSpan(0, bytes.Length - 1)));
        bytes[7] = 200; // not an XpReason
        Assert.Null(ProgressionEvents.DecodeXpSummary(bytes));
    }

    [Fact]
    public void ProgressionEventKindsStayInTheReliableEnvelope()
    {
        Assert.Equal(15, (int)EventKind.LevelUp);
        Assert.Equal(16, (int)EventKind.XpSummary);
    }
}
