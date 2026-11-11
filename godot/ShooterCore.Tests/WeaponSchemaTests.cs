using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>weapons.json schema v2: categories, category_defaults merge, recoil, shotgun/sniper fields, unlocks, validation.</summary>
public class WeaponSchemaTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>A minimal valid pistol-category hitscan gun with the given extra fields.</summary>
    static string Gun(string id = "a", string extra = "") =>
        "{\"id\":\"" + id + "\",\"category\":\"pistol\",\"slot\":\"secondary\",\"kind\":\"hitscan\",\"damage\":10" + (extra == "" ? "" : "," + extra) + "}";

    static string Doc(string weapons, string categoryDefaults = "{}", string start = "a", int version = 2) =>
        "{\"version\":" + version + ",\"starting_weapon\":\"" + start + "\",\"category_defaults\":" + categoryDefaults + ",\"weapons\":[" + weapons + "]}";

    static DataFileException Rejected(string json)
    {
        var e = Assert.Throws<DataFileException>(() => WeaponsFile.Parse(json));
        return e;
    }

    [Fact]
    public void ShippedFileIsV2AndV1WeaponsKeepTheirNumbers()
    {
        var d = GameData.Load(DataDir);
        Assert.Equal(2, d.Weapons.Version);

        var pistol = d.Weapons.Find("pistol")!;
        Assert.Equal(WeaponCategory.Pistol, pistol.Category);
        Assert.Equal(WeaponSlot.Secondary, pistol.Slot);
        Assert.Equal(0, pistol.UnlockLevel);
        Assert.Equal(1.0f, pistol.MoveSpeedMultiplier);
        Assert.Equal(0.4f, pistol.SpreadBase);
        Assert.Equal(0.3f, pistol.SpreadPerShot);
        Assert.Equal(4f, pistol.SpreadMax);
        Assert.Equal(0.4f, pistol.SpreadRecovery);

        // ak47 has no spread or move speed of its own: both come from the assault_rifle category block.
        var ak = d.Weapons.Find("ak47")!;
        Assert.Equal(WeaponCategory.AssaultRifle, ak.Category);
        Assert.Equal(WeaponSlot.Primary, ak.Slot);
        Assert.Equal(0.95f, ak.MoveSpeedMultiplier);
        Assert.Equal(1.0f, ak.SpreadBase);
        Assert.Equal(0.25f, ak.SpreadPerShot);
        Assert.Equal(0.6f, ak.SpreadRecovery);
        Assert.Equal(0.95f, ak.AdsMoveSpeedMultiplier);

        var sniper = d.Weapons.Find("sniper")!;
        Assert.Equal(WeaponCategory.Sniper, sniper.Category);
        Assert.Equal(WeaponSlot.Primary, sniper.Slot);
        Assert.Equal(0.8f, sniper.MoveSpeedMultiplier);
        Assert.Equal(0.02f, sniper.SpreadBase);
        Assert.Equal(20f, sniper.ZoomFov);
        Assert.Equal(1, sniper.ZoomLevels);

        var bazooka = d.Weapons.Find("bazooka")!;
        Assert.Equal(WeaponCategory.Launcher, bazooka.Category);
        Assert.Equal(WeaponSlot.Special, bazooka.Slot);
        Assert.Equal(0.85f, bazooka.MoveSpeedMultiplier);

        var knife = d.Weapons.Find("knife")!;
        Assert.Equal(WeaponCategory.Melee, knife.Category);
        Assert.Equal(WeaponSlot.Special, knife.Slot);

        // v1 weapons get no recoil, so their behaviour is unchanged until the category tickets add numbers.
        foreach (var w in d.Weapons.Weapons)
        {
            Assert.Equal(0, w.Recoil.VerticalPerShot);
            Assert.Empty(w.Recoil.Pattern);
            Assert.Equal(0, w.UnlockLevel);
        }
    }

    [Fact]
    public void CategoryDefaultsApplyAndWeaponValuesWin()
    {
        var f = WeaponsFile.Parse(Doc(
            Gun("a") + "," + Gun("b", "\"move_speed_multiplier\":0.7,\"spread_base\":0.1"),
            "{\"pistol\":{\"move_speed_multiplier\":0.9,\"spread_base\":0.5,\"spread_max\":3}}"));

        var a = f.Find("a")!;
        Assert.Equal(0.9f, a.MoveSpeedMultiplier);
        Assert.Equal(0.5f, a.SpreadBase);
        Assert.Equal(3f, a.SpreadMax);
        Assert.Equal(0f, a.SpreadPerShot); // not in the category block and not in the weapon: built-in default

        var b = f.Find("b")!;
        Assert.Equal(0.7f, b.MoveSpeedMultiplier);
        Assert.Equal(0.1f, b.SpreadBase);
        Assert.Equal(3f, b.SpreadMax); // still inherited
    }

    [Fact]
    public void AdsMoveSpeedFallsBackToMoveSpeedUnlessSet()
    {
        var f = WeaponsFile.Parse(Doc(
            Gun("a", "\"move_speed_multiplier\":0.8") + "," + Gun("b", "\"move_speed_multiplier\":0.8,\"ads_move_speed_multiplier\":0.5"),
            "{\"pistol\":{\"ads_move_speed_multiplier\":0.6}}"));
        Assert.Equal(0.6f, f.Find("a")!.AdsMoveSpeedMultiplier); // category ads value wins over the move speed
        Assert.Equal(0.5f, f.Find("b")!.AdsMoveSpeedMultiplier); // weapon ads value wins over both
        Assert.Equal(0.8f, f.Find("b")!.MoveSpeedMultiplier);

        var plain = WeaponsFile.Parse(Doc(Gun("c", "\"move_speed_multiplier\":0.8"), start: "c"));
        Assert.Equal(0.8f, plain.Find("c")!.AdsMoveSpeedMultiplier);
    }

    [Fact]
    public void RecoilMergesFieldByFieldAndPatternIsWholeOverride()
    {
        var f = WeaponsFile.Parse(Doc(
            Gun("a") + "," + Gun("b", "\"recoil\":{\"pattern\":[0.5]}"),
            "{\"pistol\":{\"recoil\":{\"vertical_per_shot\":2,\"recovery_rate\":5,\"pattern\":[1,-1]}}}"));
        var a = f.Find("a")!.Recoil;
        Assert.Equal(2f, a.VerticalPerShot);
        Assert.Equal(5f, a.RecoveryRate);
        Assert.Equal(0f, a.MaxClimb);
        Assert.Equal(new[] { 1f, -1f }, a.Pattern);

        var weaponRecoil = WeaponsFile.Parse(Doc(
            Gun("c", "\"recoil\":{\"vertical_per_shot\":3,\"max_climb\":12}"),
            "{\"pistol\":{\"recoil\":{\"vertical_per_shot\":2,\"recovery_rate\":5}}}", start: "c")).Find("c")!.Recoil;
        Assert.Equal(3f, weaponRecoil.VerticalPerShot);
        Assert.Equal(5f, weaponRecoil.RecoveryRate);
        Assert.Equal(12f, weaponRecoil.MaxClimb);
        Assert.Empty(weaponRecoil.Pattern);

        var b = f.Find("b")!.Recoil;
        Assert.Equal(new[] { 0.5f }, b.Pattern);
        Assert.Equal(2f, b.VerticalPerShot); // category value still applies to fields the weapon left out
    }

    [Fact]
    public void ScopeFovIsTheV2NameAndZoomFovStillWorks()
    {
        Assert.Equal(30f, WeaponsFile.Parse(Doc(Gun("a", "\"zoom_fov\":20,\"scope_fov\":30"))).Find("a")!.ZoomFov);
        Assert.Equal(20f, WeaponsFile.Parse(Doc(Gun("a", "\"zoom_fov\":20"))).Find("a")!.ZoomFov);
        Assert.Equal(25f, WeaponsFile.Parse(Doc(Gun("a", "\"scope_fov\":25"))).Find("a")!.ZoomFov);
        Assert.Equal(0f, WeaponsFile.Parse(Doc(Gun("a"))).Find("a")!.ZoomFov);
    }

    [Fact]
    public void ShotgunAndSniperFieldsParse()
    {
        var f = WeaponsFile.Parse(Doc(
            "{\"id\":\"shot\",\"category\":\"shotgun\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":12,\"pellets\":8,\"pellet_spread\":4}"
            + ",{\"id\":\"snipe\",\"category\":\"sniper\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":85,\"zoom_levels\":2,\"sway_amount\":0.3,"
            + "\"breath_hold_seconds\":2.5,\"breath_recover_seconds\":0.6,\"quickscope_penalty\":3}", start: "shot"));

        var shot = f.Find("shot")!;
        Assert.Equal(8, shot.Pellets);
        Assert.Equal(4f, shot.PelletSpread);
        Assert.Equal(12f, shot.DamagePerPellet); // not set: each pellet does the weapon's damage

        var snipe = f.Find("snipe")!;
        Assert.Equal(2, snipe.ZoomLevels);
        Assert.Equal(0.3f, snipe.SwayAmount);
        Assert.Equal(2.5f, snipe.BreathHoldSeconds);
        Assert.Equal(0.6f, snipe.BreathRecoverSeconds);
        Assert.Equal(3f, snipe.QuickscopePenalty);
        Assert.Equal(1, WeaponsFile.Parse(Doc(Gun())).Find("a")!.Pellets); // non-shotguns default to one projectile
    }

    [Fact]
    public void UnlockLevelDefaultsToZeroAndStartingWeaponMustBeUnlocked()
    {
        Assert.Equal(0, WeaponsFile.Parse(Doc(Gun())).Find("a")!.UnlockLevel);
        Assert.Equal(7, WeaponsFile.Parse(Doc(Gun("a", "\"unlock_level\":7") + "," + Gun("b", "\"unlock_level\":0"), start: "b")).Find("a")!.UnlockLevel);

        var e = Rejected(Doc(Gun("a", "\"unlock_level\":7"), start: "a"));
        Assert.Contains("weapons.json", e.Message);
        Assert.Contains("\"a\"", e.Message);
        Assert.Contains("unlock_level", e.Message);
    }

    [Fact]
    public void ValidationErrorsNameFileWeaponAndField()
    {
        // (weapon JSON, category_defaults JSON, what the message must name)
        var cases = new (string Weapon, string Defaults, string[] Names)[]
        {
            (Gun("a").Replace("\"pistol\"", "\"rocket\""), "{}", new[] { "\"a\"", "\"category\"" }),
            (Gun("a").Replace("\"secondary\"", "\"tertiary\""), "{}", new[] { "\"a\"", "\"slot\"" }),
            ("{\"id\":\"a\",\"slot\":\"secondary\",\"kind\":\"hitscan\",\"damage\":10}", "{}", new[] { "\"a\"", "\"category\"" }),
            (Gun("a").Replace("\"hitscan\"", "\"melee\""), "{}", new[] { "\"a\"", "\"kind\"" }),
            (Gun("a", "\"recoil\":{\"recovery_rate\":-1}"), "{}", new[] { "\"a\"", "\"recoil.recovery_rate\"" }),
            (Gun("a", "\"spread_max\":-2"), "{}", new[] { "\"a\"", "\"spread_max\"" }),
            (Gun("a", "\"fire_interval\":-0.1"), "{}", new[] { "\"a\"", "\"fire_interval\"" }),
            (Gun("a"), "{\"rocket\":{}}", new[] { "\"rocket\"", "category_defaults", "unknown category" }),
            (Gun("a"), "{\"pistol\":{\"move_speed_multiplier\":0}}", new[] { "category_defaults \"pistol\"", "\"move_speed_multiplier\"" }),
            (Gun("a", "\"move_speed_multiplier\":-1"), "{}", new[] { "\"a\"", "\"move_speed_multiplier\"" }),
            (Gun("a", "\"unlock_level\":-1"), "{}", new[] { "\"a\"", "\"unlock_level\"" }),
            ("{\"id\":\"a\",\"category\":\"shotgun\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":9}", "{}", new[] { "\"a\"", "\"pellets\"" }),
            ("{\"id\":\"a\",\"category\":\"shotgun\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":9,\"pellets\":0}", "{}", new[] { "\"a\"", "\"pellets\"" }),
            ("{\"category\":\"pistol\",\"slot\":\"secondary\",\"kind\":\"hitscan\",\"damage\":10}", "{}", new[] { "\"id\"" }),
            (Gun("a") + "," + Gun("a"), "{}", new[] { "duplicate", "\"a\"" }),
        };

        foreach (var (weapon, defaults, names) in cases)
        {
            var e = Rejected(Doc(weapon, defaults));
            Assert.StartsWith("weapons.json:", e.Message);
            foreach (var n in names) Assert.Contains(n, e.Message);
        }
    }

    [Fact]
    public void StartingWeaponMustExistAndNoWeaponsIsRejected()
    {
        var missing = Rejected(Doc(Gun("a"), start: "nope"));
        Assert.Contains("starting_weapon", missing.Message);
        var none = Rejected(Doc(""));
        Assert.Contains("no weapons", none.Message);
    }

    [Fact]
    public void VersionOneIsRejectedWithMigrationHintAndNewerVersionNamesSupported()
    {
        var old = Rejected(Doc(Gun(), version: 1));
        Assert.Contains("older", old.Message);
        Assert.Contains("migrate", old.Message);

        var newer = Rejected(Doc(Gun(), version: 3));
        Assert.Contains("newer", newer.Message);
        Assert.Contains("supported version 2", newer.Message);
    }

    [Fact]
    public void UnknownFieldsAreIgnoredInWeapons()
    {
        var f = WeaponsFile.Parse(Doc(Gun("a", "\"future_field\":[1,2,{\"x\":true}]")));
        Assert.Equal(10f, f.Find("a")!.Damage);
    }
}
