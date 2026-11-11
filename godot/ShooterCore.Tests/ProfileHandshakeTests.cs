using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Protocol v2 profile handshake (ticket #107): hello token, profile, loadout messages and the loadout rules.</summary>
public class ProfileHandshakeTests
{
    static Loadout Full(int slot = 2) => new(slot, "ak47", "pistol", "", "", "", "");

    /// <summary>Streak id to unlock level, from the shipped killstreaks.json.</summary>
    static IReadOnlyDictionary<string, int> ShippedStreaks =>
        KillstreakFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "killstreaks.json")).UnlockLevels;

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
        Assert.Null(LoadoutRules.ValidateLoadout(perk, w, 1, Array.Empty<Unlock>(), new Dictionary<string, int> { ["ghost"] = 0 }, Empty()));

        var streak = new Loadout(0, "ak47", "pistol", "", "", "uav", "");
        Assert.Contains("unknown killstreak 2 \"uav\"", LoadoutRules.ValidateLoadout(streak, w, 1, Array.Empty<Unlock>(), Empty(), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(streak, w, 1, Array.Empty<Unlock>(), Empty(), new Dictionary<string, int> { ["uav"] = 0 }));
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
    public void DefaultLoadoutUsesTheFirstUnlockedItemsAndTheStarterKit()
    {
        var w = Weapons();
        var perks = PerksFile.Parse("{\"version\":1,\"perks\":[" +
            "{\"id\":\"fleet_foot\",\"name\":\"Fleet Foot\",\"unlock_level\":0,\"modifiers\":{\"run_speed\":1.12}}," +
            "{\"id\":\"ghost\",\"name\":\"Ghost\",\"unlock_level\":20,\"modifiers\":{\"uav_visibility\":0}}]}");
        var d = LoadoutRules.DefaultLoadout(3, w, perks);
        Assert.Equal(3, d.Slot);
        Assert.Equal("ak47", d.PrimaryWeapon); // lmg is locked at level 5, so the first open primary is ak47
        Assert.Equal("pistol", d.SecondaryWeapon);
        Assert.Equal("fleet_foot", d.Perk); // the first perk open at level 0
        Assert.Equal(KillstreakIds.StarterStreak, d.Streak1);
        Assert.Null(LoadoutRules.ValidateLoadout(d, w, 1, Array.Empty<Unlock>(), LevelsOf(perks), ShippedStreaks));
    }

    [Fact]
    public void LockedPerkNeedsTheLevelOrAnUnlock()
    {
        var w = Weapons();
        var perks = PerksFile.Parse("{\"version\":1,\"perks\":[{\"id\":\"ghost\",\"name\":\"Ghost\",\"unlock_level\":20,\"modifiers\":{\"uav_visibility\":0}}]}");
        var ghost = new Loadout(0, "ak47", "pistol", "ghost", "", "", "");
        Assert.Equal("ghost unlocks at level 20", LoadoutRules.ValidateLoadout(ghost, w, 19, Array.Empty<Unlock>(), LevelsOf(perks), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(ghost, w, 20, Array.Empty<Unlock>(), LevelsOf(perks), Empty()));
        Assert.Null(LoadoutRules.ValidateLoadout(ghost, w, 1, new[] { new Unlock("perk", "ghost") }, LevelsOf(perks), Empty()));
    }

    [Fact]
    public void KillstreakMayNotRepeatInALoadout()
    {
        var w = Weapons();
        var twice = new Loadout(0, "ak47", "pistol", "", "uav", "", "uav");
        Assert.Contains("\"uav\" is selected more than once", LoadoutRules.ValidateLoadout(twice, w, 1, Array.Empty<Unlock>(), Empty(), ShippedStreaks));
        var locked = new Dictionary<string, int> { ["uav"] = 4 };
        var streak = new Loadout(0, "ak47", "pistol", "", "uav", "", "");
        Assert.Equal("uav unlocks at level 4", LoadoutRules.ValidateLoadout(streak, w, 3, Array.Empty<Unlock>(), Empty(), locked));
    }

    static IReadOnlyDictionary<string, int> LevelsOf(PerksFile perks) => perks.Perks.ToDictionary(p => p.Id, p => p.UnlockLevel);

    static IReadOnlyDictionary<string, int> Empty() => new Dictionary<string, int>();
}

/// <summary>Server hardening (#197): player ids, name cleaning and the catch-up budget.</summary>
public class ServerHardeningTests
{
    [Fact]
    public void PlayerIdsStayBelowTheVehicleRangeAndSkipIdsInUse()
    {
        var ids = new PlayerIds();
        var used = new HashSet<ushort> { 2 };
        Assert.Equal(1, ids.Allocate(used.Contains));
        Assert.Equal(3, ids.Allocate(used.Contains)); // 2 is taken
        for (var i = 0; i < 100_000; i++)
        {
            var id = ids.Allocate(_ => false);
            Assert.InRange(id, PlayerIds.First, PlayerIds.Last);
        }
        Assert.True(PlayerIds.Last < VehicleRecord.FirstId);
    }

    [Fact]
    public void PlayerIdsReturnZeroWhenEveryIdIsInUse() => Assert.Equal(0, new PlayerIds().Allocate(_ => true));

    [Theory]
    [InlineData("  Bob  ", "Bob")]
    [InlineData("[SANDBOX] Eve", "SANDBOX Eve")]
    [InlineData("a\u0007b\nc", "abc")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void PlayerNamesAreCleaned(string? raw, string expected) => Assert.Equal(expected, LoadoutRules.SanitizePlayerName(raw));

    [Fact]
    public void PlayerNamesAreCutToTheLimit() =>
        Assert.Equal(LoadoutRules.MaxNameChars, LoadoutRules.SanitizePlayerName(new string('x', 200)).Length);

    [Fact]
    public void CatchUpAllowsABurstButNotSustainedDoubleSpeed()
    {
        var b = new CatchUpBudget();
        var burst = 0;
        for (var t = 0; t < 7; t++) burst += b.Steps(8) - 1; // a stalled client's backlog drains at two inputs per tick
        Assert.Equal(7, burst);
        var extra = 0;
        for (var t = 0; t < 400; t++) extra += b.Steps(8) - 1; // a client that keeps the queue full
        Assert.InRange(extra, 90, 110); // about RefillPerTick (0.25) of the ticks
        Assert.Equal(1, new CatchUpBudget().Steps(3)); // a short queue never double-steps
    }
}
