using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Protocol v2 profile handshake (ticket #107): hello token, profile, loadout messages and the loadout rules.</summary>
public class ProfileHandshakeTests
{
    static Loadout Full(int slot = 2) => new(slot, "ak47", "pistol", "", "", "", "");

    static WeaponsFile Weapons() => WeaponsFile.Parse(
        "{\"version\":2,\"starting_weapon\":\"pistol\",\"category_defaults\":{},\"weapons\":[" +
        "{\"id\":\"pistol\",\"category\":\"pistol\",\"slot\":\"secondary\",\"kind\":\"hitscan\",\"damage\":10}," +
        "{\"id\":\"ak47\",\"category\":\"assault_rifle\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":10}," +
        "{\"id\":\"lmg\",\"category\":\"machine_gun\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":10,\"unlock_level\":5}]}");

    [Fact]
    public void HelloCarriesTheTokenAndAV1HelloDecodesWithoutOne()
    {
        var token = Guid.NewGuid().ToString();
        Assert.Equal(new HelloMsg(Wire.ProtocolVersion, "Anička", token), Wire.DecodeReliable(Wire.Encode(new HelloMsg(Wire.ProtocolVersion, "Anička", token))));

        // a v1 hello (type, version, name) has no token; it decodes so the server can refuse it on the version
        var v1 = new byte[] { Wire.THello, 1, 3, (byte)'b', (byte)'o', (byte)'b' };
        Assert.Equal(new HelloMsg(1, "bob", ""), Wire.DecodeReliable(v1));
    }

    [Fact]
    public void ProfileRoundTripsWithLoadoutsAndUnlocks()
    {
        var loadouts = Enumerable.Range(0, Loadout.SlotCount).Select(i => i == 1 ? new Loadout(1, "ak47", "pistol", "ghost", "uav", "", "") : Loadout.Empty(i)).ToList();
        var unlocks = new List<Unlock> { new("weapon", "ak47"), new("perk", "ghost") };
        var src = new ProfileMsg("bob", 1234, 6, 1, loadouts, unlocks);

        var got = Assert.IsType<ProfileMsg>(Wire.DecodeReliable(Wire.Encode(src)));
        Assert.Equal("bob", got.Name);
        Assert.Equal(1234, got.Xp);
        Assert.Equal(6, got.Level);
        Assert.Equal(1, got.ActiveSlot);
        Assert.Equal(loadouts, got.Loadouts);
        Assert.Equal(unlocks, got.Unlocks);
    }

    [Fact]
    public void ProfileWithNoUnlocksRoundTrips()
    {
        var got = Assert.IsType<ProfileMsg>(Wire.DecodeReliable(Wire.Encode(new ProfileMsg("", 0, 1, 0, new[] { Loadout.Empty(0) }, Array.Empty<Unlock>()))));
        Assert.Empty(got.Unlocks);
        Assert.Single(got.Loadouts);
    }

    [Fact]
    public void LoadoutMessagesAndErrorRoundTrip()
    {
        Assert.Equal(new SaveLoadoutMsg(Full()), Wire.DecodeReliable(Wire.Encode(new SaveLoadoutMsg(Full()))));
        Assert.Equal(new SelectSlotMsg(4), Wire.DecodeReliable(Wire.Encode(new SelectSlotMsg(4))));
        Assert.Equal(new ErrorMsg("unknown primary weapon \"x\""), Wire.DecodeReliable(Wire.Encode(new ErrorMsg("unknown primary weapon \"x\""))));
    }

    [Fact]
    public void TruncatedProfileAndLoadoutMessagesAreRejected()
    {
        var full = Wire.Encode(new ProfileMsg("bob", 10, 1, 0, new[] { Full(0) }, new[] { new Unlock("weapon", "ak47") }));
        for (var cut = 1; cut < full.Length; cut++)
            Assert.Null(Wire.DecodeReliable(full.AsSpan(0, cut)));

        var save = Wire.Encode(new SaveLoadoutMsg(Full()));
        Assert.Null(Wire.DecodeReliable(save.AsSpan(0, save.Length - 1)));
        Assert.Null(Wire.DecodeReliable(new byte[] { Wire.TSelectSlot }));
    }

    [Fact]
    public void ReliableTypesStayBelowTheInputChannelMarker()
    {
        // the server routes a packet to the reliable decoder by its type byte, so every reliable type must sit below TInput
        foreach (var b in new[] { Wire.THello, Wire.TWelcome, Wire.TJoin, Wire.TLeave, Wire.TEvent, Wire.TProfile, Wire.TSaveLoadout, Wire.TSelectSlot, Wire.TError })
            Assert.True(b < Wire.TInput);
    }

    [Fact]
    public void ValidLoadoutPasses()
    {
        Assert.Null(LoadoutRules.ValidateLoadout(Full(), Weapons(), 1, Array.Empty<Unlock>(), Empty(), Empty()));
    }

    [Fact]
    public void WeaponMustBeInItsSlot()
    {
        var swapped = new Loadout(0, "pistol", "ak47", "", "", "", "");
        Assert.Contains("unknown primary weapon", LoadoutRules.ValidateLoadout(swapped, Weapons(), 1, Array.Empty<Unlock>(), Empty(), Empty()));
    }

    [Fact]
    public void UnknownWeaponIsRejected()
    {
        var bad = new Loadout(0, "bazooka9000", "pistol", "", "", "", "");
        Assert.Contains("unknown primary weapon \"bazooka9000\"", LoadoutRules.ValidateLoadout(bad, Weapons(), 1, Array.Empty<Unlock>(), Empty(), Empty()));
    }

    [Fact]
    public void PrimaryAndSecondaryAreRequired()
    {
        Assert.Contains("primary weapon is required", LoadoutRules.ValidateLoadout(new Loadout(0, "", "pistol", "", "", "", ""), Weapons(), 1, Array.Empty<Unlock>(), Empty(), Empty()));
        Assert.Contains("secondary weapon is required", LoadoutRules.ValidateLoadout(new Loadout(0, "ak47", "", "", "", "", ""), Weapons(), 1, Array.Empty<Unlock>(), Empty(), Empty()));
    }

    [Fact]
    public void LockedWeaponNeedsTheLevelOrAnUnlock()
    {
        var w = Weapons();
        var lmg = new Loadout(0, "lmg", "pistol", "", "", "", "");
        Assert.Contains("lmg unlocks at level 5", LoadoutRules.ValidateLoadout(lmg, w, 4, Array.Empty<Unlock>(), Empty(), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(lmg, w, 5, Array.Empty<Unlock>(), Empty(), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(lmg, w, 1, new[] { new Unlock("weapon", "lmg") }, Empty(), Empty()));
        // an unlock of another kind does not count
        Assert.NotNull(LoadoutRules.ValidateLoadout(lmg, w, 1, new[] { new Unlock("perk", "lmg") }, Empty(), Empty()));
    }

    [Fact]
    public void PerkAndStreaksMustBeKnownOrEmpty()
    {
        var w = Weapons();
        var perk = new Loadout(0, "ak47", "pistol", "ghost", "", "", "");
        Assert.Contains("unknown perk \"ghost\"", LoadoutRules.ValidateLoadout(perk, w, 1, Array.Empty<Unlock>(), Empty(), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(perk, w, 1, Array.Empty<Unlock>(), new HashSet<string> { "ghost" }, Empty()));

        var streak = new Loadout(0, "ak47", "pistol", "", "", "uav", "");
        Assert.Contains("unknown killstreak 2 \"uav\"", LoadoutRules.ValidateLoadout(streak, w, 1, Array.Empty<Unlock>(), Empty(), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(streak, w, 1, Array.Empty<Unlock>(), Empty(), new HashSet<string> { "uav" }));
    }

    [Fact]
    public void SlotOutOfRangeIsRejected()
    {
        Assert.NotNull(LoadoutRules.ValidateSlot(-1));
        Assert.NotNull(LoadoutRules.ValidateSlot(Loadout.SlotCount));
        Assert.Null(LoadoutRules.ValidateSlot(Loadout.MaxSlot));
        Assert.NotNull(LoadoutRules.ValidateLoadout(new Loadout(5, "ak47", "pistol", "", "", "", ""), Weapons(), 1, Array.Empty<Unlock>(), Empty(), Empty()));
    }

    [Fact]
    public void DefaultLoadoutUsesTheFirstUnlockedWeapons()
    {
        var w = Weapons();
        var d = LoadoutRules.DefaultLoadout(3, w);
        Assert.Equal(3, d.Slot);
        Assert.Equal("ak47", d.PrimaryWeapon); // lmg is locked at level 5, so the first open primary is ak47
        Assert.Equal("pistol", d.SecondaryWeapon);
        Assert.Equal("", d.Perk);
        Assert.Null(LoadoutRules.ValidateLoadout(d, w, 1, Array.Empty<Unlock>(), Empty(), Empty()));
    }

    static IReadOnlySet<string> Empty() => new HashSet<string>();
}
