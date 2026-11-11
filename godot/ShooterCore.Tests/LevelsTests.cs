using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Shooter v2 levels and unlocks (#116): levels.json curve, XP awards, the unlock table and the roadkill cause.</summary>
public class LevelsTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string LevelsJson = File.ReadAllText(Path.Combine(DataDir, "levels.json"));
    static readonly GameData Shipped = GameData.Load(DataDir);

    static LevelsFile Parse(string json, IReadOnlySet<string>? streaks = null) =>
        LevelsFile.Parse(json, Shipped.Weapons, Shipped.Perks, streaks);

    static Profile ProfileAt(int level) => new(1, "t", "", 0, level, DateTime.UtcNow, DateTime.UtcNow);

    static string Curve(int count = 40) =>
        "\"xp_to_reach\": [" + string.Join(", ", Enumerable.Range(0, count).Select(i => i * 100)) + "]";

    const string Awards = "\"awards\": { \"kill\": 100, \"headshot\": 25, \"assist\": 40, \"streak_kill\": 20, \"roadkill\": 130, \"match_win\": 250, \"match_top3\": 100, \"assist_window_seconds\": 10 }";

    // the shipped file with extra unlock lines added at the top of the table
    static string WithUnlock(string entries) => LevelsJson.Replace("\"unlocks\": [", "\"unlocks\": [" + entries + ", ");

    static string Minimal(string curve, string unlocks, string awards = Awards) =>
        "{ \"version\": 1, " + curve + ", " + awards + ", \"unlocks\": [" + unlocks + "] }";

    // ---- shipped levels.json ----

    [Fact]
    public void ShippedCurveHasFortyLevelsStartingAtZero()
    {
        var c = Shipped.Levels.Curve;
        Assert.Equal(40, c.MaxLevel);
        Assert.Equal(1, c.LevelForXp(0));
        Assert.Equal(1, c.LevelForXp(149));
        Assert.Equal(2, c.LevelForXp(150));
        Assert.Equal(150, c.XpForLevel(2));
        Assert.Equal(40, c.LevelForXp(int.MaxValue));
    }

    [Fact]
    public void ShippedCurveRisesSteeplyEarly()
    {
        // a casual first match (a kill, a headshot and an assist) is already level 2
        Assert.True(Shipped.Levels.Curve.LevelForXp(100 + 25 + 40) >= 2);
        var gaps = Enumerable.Range(1, 39).Select(l => Shipped.Levels.Curve.XpForLevel(l + 1) - Shipped.Levels.Curve.XpForLevel(l)).ToList();
        Assert.Equal(gaps, gaps.OrderBy(g => g).ToList());
    }

    [Fact]
    public void ShippedUnlockTableMatchesTheRebalance()
    {
        var t = Shipped.Levels.Table;
        Assert.Equal(0, t.RequiredLevel(UnlockKind.Weapon, "ak47"));
        Assert.Equal(0, t.RequiredLevel(UnlockKind.Weapon, "pistol"));
        Assert.Equal(2, t.RequiredLevel(UnlockKind.Weapon, "m4a1"));
        Assert.Equal(5, t.RequiredLevel(UnlockKind.Weapon, "sniper"));
        Assert.Equal(12, t.RequiredLevel(UnlockKind.Weapon, "m60"));
        Assert.Equal(0, t.RequiredLevel(UnlockKind.Perk, "fleet_foot"));
        Assert.Equal(20, t.RequiredLevel(UnlockKind.Perk, "ghost"));
        Assert.Null(t.RequiredLevel(UnlockKind.Weapon, "knife"));     // special: not a loadout item
        Assert.Null(t.RequiredLevel(UnlockKind.Weapon, "bazooka"));
    }

    [Fact]
    public void EveryShippedWeaponAndPerkIsInTheTableAtItsOwnLevel()
    {
        foreach (var w in Shipped.Weapons.Weapons.Where(w => w.Slot != WeaponSlot.Special))
            Assert.Equal(w.UnlockLevel, Shipped.Levels.Table.RequiredLevel(UnlockKind.Weapon, w.Id));
        foreach (var p in Shipped.Perks.Perks)
            Assert.Equal(p.UnlockLevel, Shipped.Levels.Table.RequiredLevel(UnlockKind.Perk, p.Id));
    }

    [Fact]
    public void StartingKitIsUnlockedAtLevelOne()
    {
        var kit = ProfileAt(1);
        Assert.True(Shipped.Levels.IsUnlocked(kit, UnlockKind.Weapon, Shipped.Weapons.StartingWeapon));
        Assert.True(Shipped.Levels.IsUnlocked(kit, UnlockKind.Weapon, "pistol"));
        Assert.True(Shipped.Levels.IsUnlocked(kit, UnlockKind.Perk, "fleet_foot"));
        Assert.False(Shipped.Levels.IsUnlocked(kit, UnlockKind.Weapon, "m4a1"));
        Assert.False(Shipped.Levels.IsUnlocked(kit, UnlockKind.Perk, "flak_jacket"));
    }

    [Fact]
    public void IsUnlockedIsGatedByLevelAndUnknownIdsAreNever()
    {
        Assert.False(Shipped.Levels.IsUnlocked(ProfileAt(1), UnlockKind.Weapon, "m4a1"));
        Assert.True(Shipped.Levels.IsUnlocked(ProfileAt(2), UnlockKind.Weapon, "m4a1"));
        Assert.True(Shipped.Levels.IsUnlocked(ProfileAt(40), UnlockKind.Weapon, "m60"));
        Assert.False(Shipped.Levels.IsUnlocked(ProfileAt(40), UnlockKind.Weapon, "not_a_gun"));
        Assert.False(Shipped.Levels.IsUnlocked(ProfileAt(40), UnlockKind.Weapon, "knife"));
        Assert.False(Shipped.Levels.IsUnlocked(ProfileAt(40), UnlockKind.Streak, "uav"));
    }

    // ---- validation ----

    [Fact]
    public void CurveMustListFortyLevels()
    {
        var e = Assert.Throws<DataFileException>(() => Parse(Minimal(Curve(39), "")));
        Assert.Contains("exactly 40", e.Message);
    }

    [Fact]
    public void CurveMustStartAtZeroAndStrictlyIncrease()
    {
        var bad = string.Join(", ", Enumerable.Range(0, 40).Select(i => i == 5 ? 100 : i * 100));
        var e = Assert.Throws<DataFileException>(() => Parse(Minimal("\"xp_to_reach\": [" + bad + "]", "")));
        Assert.Contains("strictly increase", e.Message);
        var shifted = string.Join(", ", Enumerable.Range(0, 40).Select(i => i * 100 + 1));
        Assert.Contains("start at 0", Assert.Throws<DataFileException>(() => Parse(Minimal("\"xp_to_reach\": [" + shifted + "]", ""))).Message);
    }

    [Fact]
    public void UnknownWeaponIdFailsAtLoad()
    {
        var e = Assert.Throws<DataFileException>(() => Parse(WithUnlock("{ \"level\": 3, \"kind\": \"weapon\", \"id\": \"smg_future\" }")));
        Assert.Contains("unknown weapon id", e.Message);
        Assert.Contains("smg_future", e.Message);
    }

    [Fact]
    public void SpecialWeaponsAreNotLoadoutItems()
    {
        var e = Assert.Throws<DataFileException>(() => Parse(WithUnlock("{ \"level\": 0, \"kind\": \"weapon\", \"id\": \"knife\" }")));
        Assert.Contains("special", e.Message);
    }

    [Fact]
    public void UnknownPerkIdFailsAtLoad()
    {
        var e = Assert.Throws<DataFileException>(() => Parse(WithUnlock("{ \"level\": 3, \"kind\": \"perk\", \"id\": \"nope\" }")));
        Assert.Contains("unknown perk id", e.Message);
    }

    [Fact]
    public void LevelMustMatchTheItemsOwnUnlockLevel()
    {
        var shipped = File.ReadAllText(Path.Combine(DataDir, "levels.json"));
        var wrong = shipped.Replace("{ \"level\": 2, \"kind\": \"weapon\", \"id\": \"m4a1\" }", "{ \"level\": 3, \"kind\": \"weapon\", \"id\": \"m4a1\" }");
        Assert.NotEqual(shipped, wrong);
        var e = Assert.Throws<DataFileException>(() => Parse(wrong));
        Assert.Contains("make them match", e.Message);
    }

    [Fact]
    public void GatedItemMissingFromTheTableFailsAtLoad()
    {
        var shipped = File.ReadAllText(Path.Combine(DataDir, "levels.json"));
        var without = shipped.Replace("{ \"level\": 2, \"kind\": \"weapon\", \"id\": \"m4a1\" },", "");
        Assert.NotEqual(shipped, without);
        var e = Assert.Throws<DataFileException>(() => Parse(without));
        Assert.Contains("\"m4a1\" has unlock_level 2 but no entry", e.Message);
    }

    [Fact]
    public void StreakUnlocksNeedKillstreakIds()
    {
        var line = "{ \"level\": 4, \"kind\": \"streak\", \"id\": \"uav\" }";
        Assert.Contains("unknown killstreak id", Assert.Throws<DataFileException>(() => Parse(WithUnlock(line))).Message);
        var ok = Parse(WithUnlock(line), new HashSet<string> { "uav" });
        Assert.Equal(4, ok.Table.RequiredLevel(UnlockKind.Streak, "uav"));
    }

    [Fact]
    public void LoadoutSlotIdsAreFixed()
    {
        var e = Assert.Throws<DataFileException>(() => Parse(WithUnlock("{ \"level\": 4, \"kind\": \"loadout_slot\", \"id\": \"streak_slot_9\" }")));
        Assert.Contains("unknown loadout slot id", e.Message);
        var ok = Parse(WithUnlock("{ \"level\": 4, \"kind\": \"loadout_slot\", \"id\": \"streak_slot_2\" }"));
        Assert.Equal(4, ok.Table.RequiredLevel(UnlockKind.LoadoutSlot, "streak_slot_2"));
    }

    [Fact]
    public void DuplicateUnlockFailsAtLoad()
    {
        var line = "{ \"level\": 2, \"kind\": \"weapon\", \"id\": \"m4a1\" }";
        var e = Assert.Throws<DataFileException>(() => Parse(WithUnlock(line + ", " + line)));
        Assert.Contains("duplicate unlock", e.Message);
    }

    [Fact]
    public void UnknownKindFailsAtLoad()
    {
        var e = Assert.Throws<DataFileException>(() => Parse(WithUnlock("{ \"level\": 2, \"kind\": \"gadget\", \"id\": \"m4a1\" }")));
        Assert.Contains("field \"kind\"", e.Message);
    }

    [Fact]
    public void AwardsAreRequiredAndNonNegative()
    {
        var missing = Awards.Replace("\"roadkill\": 130, ", "");
        Assert.Contains("\"roadkill\" is required", Assert.Throws<DataFileException>(() => Parse(Minimal(Curve(), "", missing))).Message);
        var negative = Awards.Replace("\"kill\": 100", "\"kill\": -1");
        Assert.Contains("\"kill\"", Assert.Throws<DataFileException>(() => Parse(Minimal(Curve(), "", negative))).Message);
    }

    [Fact]
    public void RoadkillAwardIsSlightlyAboveAKill()
    {
        var a = Shipped.Levels.Awards;
        Assert.True(a.Roadkill > a.Kill);
        Assert.True(a.Roadkill < a.Kill * 2);
    }

    [Fact]
    public void JsonKeysAreSnakeCaseIncludingDigits()
    {
        // match_top3 is the only key with a digit; a naming-policy surprise would make the whole file fail to load
        Assert.True(Shipped.Levels.Awards.MatchTop3 > 0);
        Assert.Equal(10, Shipped.Levels.Awards.AssistWindowSeconds);
    }

    // ---- XP ----

    

    [Fact]
    public void KillPaysTheKillAwardAndBonuses()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        var g = mx.OnKill(new KillFacts(1, 2), 0);
        Assert.Equal(new[] { new XpGrant(1, 100, XpReason.Kill) }, g);

        var head = mx.OnKill(new KillFacts(1, 2, KillCause.Headshot), 0);
        Assert.Equal(new[] { new XpGrant(1, 100, XpReason.Kill), new XpGrant(1, 25, XpReason.Headshot) }, head);

        var streak = mx.OnKill(new KillFacts(1, 2, FromStreak: true), 0);
        Assert.Contains(new XpGrant(1, 20, XpReason.StreakKill), streak);
    }

    [Fact]
    public void RoadkillPaysTheRoadkillAwardInsteadOfTheKill()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        var g = mx.OnKill(new KillFacts(1, 2, KillCause.Roadkill), 0);
        Assert.Equal(new[] { new XpGrant(1, 130, XpReason.Roadkill) }, g);
    }

    [Fact]
    public void SuicideAndSelfDamagePayNothing()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        Assert.Empty(mx.OnKill(new KillFacts(3, 3), 0));
        mx.NoteDamage(3, 3, 0);
        Assert.Empty(mx.OnKill(new KillFacts(3, 3), 1));
    }

    [Fact]
    public void AssistsGoToRecentAttackersOtherThanTheKiller()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        mx.NoteDamage(5, 2, 0);   // too old by the kill (11 s > 10 s window)
        mx.NoteDamage(5, 3, 5);   // in the window
        mx.NoteDamage(5, 1, 9);   // the killer: no assist for them
        var g = mx.OnKill(new KillFacts(1, 5), 11);
        Assert.Equal(new[] { new XpGrant(1, 100, XpReason.Kill), new XpGrant(3, 40, XpReason.Assist) }, g);
    }

    [Fact]
    public void AssistBookIsClearedByTheKillAndByReset()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        mx.NoteDamage(5, 3, 0);
        mx.OnKill(new KillFacts(1, 5), 1);
        Assert.Single(mx.OnKill(new KillFacts(1, 6), 1));      // unrelated victim, no assists
        mx.NoteDamage(7, 3, 0);
        mx.Reset();
        Assert.Single(mx.OnKill(new KillFacts(1, 7), 1));
    }

    [Fact]
    public void MatchEndPaysWinnersAndTopThreeNonWinners()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        var g = mx.OnMatchEnd(new ushort[] { 1 }, new ushort[] { 1, 2, 3, 4 });
        Assert.Equal(new[] { new XpGrant(1, 250, XpReason.MatchWin), new XpGrant(2, 100, XpReason.MatchTop3), new XpGrant(3, 100, XpReason.MatchTop3) }, g);
    }

    [Fact]
    public void TiedWinnersShareTheWinAndTheThirdPlaceStillPlacesTop3()
    {
        var mx = new MatchXp(Shipped.Levels.Awards);
        var g = mx.OnMatchEnd(new ushort[] { 1, 2 }, new ushort[] { 1, 2, 3, 4 });
        Assert.Equal(2, g.Count(x => x.Reason == XpReason.MatchWin));
        Assert.Equal(new XpGrant(3, 100, XpReason.MatchTop3), Assert.Single(g, x => x.Reason == XpReason.MatchTop3));
    }

    // ---- the roadkill hook through the match ----

    [Fact]
    public void KillCauseReachesTheKillEvent()
    {
        double t = 0;
        var events = new List<MatchEvent>();
        var m = new MatchSim(new MatchConfig(), () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        m.Event += events.Add;
        m.AddPlayer(1, "a"); m.AddPlayer(2, "b");
        t += 3.01; m.Update();

        Assert.Equal(DamageResult.Killed, m.ApplyDamage(2, 1, SpecialWeapons.Vehicle, 999, cause: KillCause.Roadkill));
        var kill = events.OfType<PlayerKilled>().Single();
        Assert.Equal(KillCause.Roadkill, kill.Cause);
        Assert.Equal(1, m.Get(1)!.Kills);
    }
}
