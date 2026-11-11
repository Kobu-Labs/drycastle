using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #117: the loadout hotbar, the starter kit against the shipped data, and the XP event payloads.</summary>
public class LoadoutWiringTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    static int IndexOf(string id) => Shipped.Weapons.Weapons.FindIndex(w => w.Id == id);

    static Loadout Kit(string primary = "ak47", string secondary = "pistol") => new(0, primary, secondary, "fleet_foot", "uav", "", "");

    // ---- hotbar -------------------------------------------------------------------------------------------------

    [Fact]
    public void HotbarIsPrimarySecondaryAndKnifeInThatOrder()
    {
        var hotbar = LoadoutHotbar.Build(Shipped.Weapons, Kit("m4a1", "glock17"));
        Assert.Equal(new[] { IndexOf("m4a1"), IndexOf("glock17"), IndexOf("knife") }, hotbar);
    }

    [Fact]
    public void HotbarFallsBackToTheStarterWeaponsForAMissingOrWrongSlotItem()
    {
        // a sniper in the secondary position is the wrong slot type; a missing primary falls back to an unlock-0 primary
        var hotbar = LoadoutHotbar.Build(Shipped.Weapons, new Loadout(0, "", "sniper", "", "", "", ""));
        Assert.Equal(3, hotbar.Length);
        Assert.Equal(Shipped.Weapons.Weapons[hotbar[0]].Slot, WeaponSlot.Primary);
        Assert.Equal(0, Shipped.Weapons.Weapons[hotbar[0]].UnlockLevel);
        Assert.Equal(WeaponSlot.Secondary, Shipped.Weapons.Weapons[hotbar[1]].Slot);
        Assert.Equal("knife", Shipped.Weapons.Weapons[hotbar[2]].Id);
    }

    [Fact]
    public void OnlyTheHotbarCanBeSelectedAndNextCyclesIt()
    {
        var hotbar = LoadoutHotbar.Build(Shipped.Weapons, Kit());
        var w = new PlayerWeapons(Shipped.Weapons, null, hotbar);
        w.Reset(0);
        Assert.Equal("ak47", w.Current.Id); // spawn with the primary
        Assert.Equal(3, w.Hotbar.Count);

        // the bazooka is in weapons.json but not on the hotbar: selecting it does nothing
        w.Update(1, new WeaponInput(false, false, IndexOf("bazooka") + 1, false));
        Assert.Equal("ak47", w.Current.Id);

        // the cycle goes primary -> secondary -> knife -> primary, skipping every other weapon
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
        Assert.Equal(new[] { "pistol", "knife", "ak47", "pistol" }, ids);
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

    // ---- starter kit against the shipped data ----------------------------------------------------------------------

    [Fact]
    public void TheStarterKitIsValidAgainstTheShippedData()
    {
        var levels = Shipped.Perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);
        var kit = LoadoutRules.DefaultLoadout(0, Shipped.Weapons, Shipped.Perks);
        Assert.Equal("fleet_foot", kit.Perk);
        Assert.Equal("uav", kit.Streak1);
        Assert.Null(LoadoutRules.ValidateLoadout(kit, Shipped.Weapons, 1, Array.Empty<Unlock>(), levels, KillstreakIds.Placeholder));
    }

    [Fact]
    public void ShippedStreakTableIsTheMapsKillstreakList()
    {
        Assert.Equal(new[] { "uav", "attack_missile", "turret", "care_package", "attack_helicopter", "helicopter_helper", "ac130", "moab" },
            KillstreakIds.Placeholder.Keys);
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
