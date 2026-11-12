using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Shotgun pellet fan (seeded directions, per-pellet falloff, summed damage), shell-by-shell reload, and the shipped SMG/shotgun entries.</summary>
public class ShotgunSmgTests
{
    static readonly WeaponsFile Shipped = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
    static WeaponDef Def(string id) => Shipped.Find(id)!;

    static HitTarget T(ushort id, float x, float y, float z) => new(id, new Vector3(x, y, z), Stance.Stand, 0);

    // ---- pellet directions ---------------------------------------------------------------------------------------

    [Fact]
    public void SameSeedGivesTheSamePelletsAndADifferentSeedGivesOthers()
    {
        var aim = new Vector3(0, 0, -1);
        var a = HitMath.PelletDirections(aim, 3, 8, 42);
        var b = HitMath.PelletDirections(aim, 3, 8, 42);
        var c = HitMath.PelletDirections(aim, 3, 8, 43);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(8, a.Length);
    }

    [Fact]
    public void PelletsStayInsideTheConeAndSpreadOut()
    {
        var aim = Vector3.Normalize(new Vector3(0.3f, 0.1f, -1));
        var limit = MathF.Cos(3f * MathF.PI / 180f) - 1e-4f;
        var distinct = new HashSet<string>();
        for (uint seed = 1; seed <= 200; seed++)
            foreach (var d in HitMath.PelletDirections(aim, 3, 8, seed))
            {
                Assert.InRange(Vector3.Dot(d, aim), limit, 1.0001f);
                distinct.Add($"{d.X:F3},{d.Y:F3}");
            }
        Assert.True(distinct.Count > 1000); // 1600 pellets, not all the same direction
    }

    [Fact]
    public void ZeroSpreadPointsEveryPelletAtTheAim()
    {
        var aim = new Vector3(0, 0, -1);
        foreach (var d in HitMath.PelletDirections(aim, 0, 5, 9))
            Assert.InRange(Vector3.Dot(d, aim), 0.99999f, 1.0001f);
    }

    // ---- per-pellet falloff ----------------------------------------------------------------------------------------

    [Fact]
    public void PelletDamageFollowsTheHitscanFalloffFromDamagePerPellet()
    {
        var r = Def("remington870"); // 13 per pellet, full to 8 m, 0.4 at 30 m
        Assert.Equal(13, HitMath.PelletDamage(r, 5, false));
        Assert.Equal(13, HitMath.PelletDamage(r, 8, false));
        Assert.Equal(9, HitMath.PelletDamage(r, 19, false));   // 13 * 0.7 = 9.1
        Assert.Equal(5, HitMath.PelletDamage(r, 30, false));   // 13 * 0.4 = 5.2
        Assert.Equal(5, HitMath.PelletDamage(r, 90, false));   // flat past the end
        Assert.Equal(20, HitMath.PelletDamage(r, 5, true));    // 13 * 1.5 = 19.5, rounds to even
    }

    [Fact]
    public void DamagePerPelletDefaultsToDamage()
    {
        var aa = Def("aa12");
        Assert.Equal(aa.Damage, aa.DamagePerPellet);
    }

    // ---- summed damage across pellets ------------------------------------------------------------------------------

    [Fact]
    public void PelletsThatHitTheSameTargetSumTheirDamage()
    {
        var r = Def("remington870");
        var target = T(2, 0, 0, -5);
        var dirs = new[] { new Vector3(0, 0, -1), new Vector3(0, 0, -1), new Vector3(0, 0, -1) };
        var walls = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var totals = HitMath.ResolvePellets(r, new Vector3(0, 1, 0), dirs, 60, walls, new[] { target }, 1);
        Assert.Single(totals);
        Assert.Equal(3 * 13, totals[2].Damage);
        Assert.False(totals[2].Head);
    }

    [Fact]
    public void MissingPelletsAndWalledPelletsDoNotCount()
    {
        var r = Def("remington870");
        var target = T(2, 0, 0, -5);
        var dirs = new[] { new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(0, 0, -1) };
        var walls = new[] { float.PositiveInfinity, float.PositiveInfinity, 3f }; // third pellet stops at a wall 3 m away
        var totals = HitMath.ResolvePellets(r, new Vector3(0, 1, 0), dirs, 60, walls, new[] { target }, 1);
        Assert.Equal(13, totals[2].Damage); // only the first pellet
    }

    [Fact]
    public void TwoTargetsGetTheirOwnTotals()
    {
        var r = Def("aa12");
        var near = T(2, 0, 0, -4);
        var far = T(3, 2.0f, 0, -10);
        // the side pellets pass 0.8 m wide of the near target (its radius is 0.56 m) and enter the far one
        var side = Vector3.Normalize(new Vector3(0.2f, 0, -1));
        var dirs = new[] { new Vector3(0, 0, -1), side, side };
        var walls = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var origin = new Vector3(0, 1, 0);
        var totals = HitMath.ResolvePellets(r, origin, dirs, 60, walls, new[] { near, far }, 1);
        var dFar = HitMath.Raycast(origin, side, 60, float.PositiveInfinity, new[] { far }, 1)!.Value.Distance;
        Assert.Equal(r.DamagePerPellet, totals[2].Damage, 0);
        Assert.Equal(2 * HitMath.PelletDamage(r, dFar, false), totals[3].Damage, 0); // the pellets enter the far capsule
    }

    // ---- schema flags and shipped entries ----------------------------------------------------------------------------

    [Fact]
    public void ShippedShotgunsAndSmgsHaveTheAgreedFlags()
    {
        var rem = Def("remington870");
        Assert.True(rem.PumpAction);
        Assert.True(rem.ShellReload);
        Assert.False(rem.FullAuto);
        Assert.Equal(8, rem.Pellets);
        Assert.Equal(WeaponCategory.Shotgun, rem.Category);
        Assert.False(Def("benelli_m4").PumpAction);
        Assert.True(Def("aa12").FullAuto);
        Assert.False(Def("aa12").PumpAction);
        foreach (var id in new[] { "remington870", "spas12", "benelli_m4", "aa12" })
            Assert.Equal(WeaponSlot.Primary, Def(id).Slot);
        foreach (var id in new[] { "mp5", "ump45", "p90" })
        {
            Assert.Equal(WeaponCategory.Smg, Def(id).Category);
            Assert.True(Def(id).FullAuto);
        }
    }

    static string Doc(string gun) => "{\"version\":2,\"starting_weapon\":\"a\",\"weapons\":[" + gun + "]}";

    static string Shotgun(string extra = "", string reloadTime = "0.5") =>
        "{\"id\":\"a\",\"category\":\"shotgun\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":10,\"pellets\":8," +
        "\"magazine_size\":4,\"reserve_ammo\":8,\"reload_time\":" + reloadTime + (extra == "" ? "" : "," + extra) + "}";

    [Fact]
    public void PumpActionCannotBeFullAuto()
    {
        var e = Assert.Throws<DataFileException>(() => WeaponsFile.Parse(Doc(Shotgun("\"pump_action\":true,\"full_auto\":true"))));
        Assert.Contains("pump_action", e.Message);
    }

    [Fact]
    public void ShellReloadNeedsAReloadTime()
    {
        var e = Assert.Throws<DataFileException>(() => WeaponsFile.Parse(Doc(Shotgun("\"shell_reload\":true", reloadTime: "0"))));
        Assert.Contains("shell_reload", e.Message);
    }

    // ---- shell-by-shell reload ----------------------------------------------------------------------------------------

    sealed class Rig
    {
        public double T = 10;
        public readonly PlayerWeapons W;
        public Rig(WeaponsFile file) { W = new PlayerWeapons(file); W.Reset(T); }

        /// <summary>One tick at time <paramref name="t"/> with the given fire and reload inputs (slot 0 = no change).</summary>
        public FireOutcome? At(double t, bool fire = false, bool reload = false)
        {
            T = t;
            return W.Update(t, new WeaponInput(fire, reload, 0, false));
        }

        /// <summary>Empties the magazine with four pulls, released between them (the shotgun is not full auto).</summary>
        public void EmptyMagazine()
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.NotNull(At(10 + i * 0.4, fire: true));
                At(10 + i * 0.4 + 0.2, fire: false);
            }
            Assert.Equal(0, W.Mag(1));
        }
    }

    /// <summary>A shotgun with a 4-shell magazine, 8 in reserve, 1 s per shell and a 0.2 s shot cooldown.</summary>
    static WeaponsFile ShellGun() => WeaponsFile.Parse(Doc(
        "{\"id\":\"a\",\"category\":\"shotgun\",\"slot\":\"primary\",\"kind\":\"hitscan\",\"damage\":10,\"pellets\":8," +
        "\"shell_reload\":true,\"fire_interval\":0.2,\"magazine_size\":4,\"reserve_ammo\":8,\"reload_time\":1.0}"));

    [Fact]
    public void ShellReloadLoadsOneShellPerReloadTime()
    {
        var r = new Rig(ShellGun());
        r.EmptyMagazine();
        r.At(12.0, reload: true);                     // reload key starts the reload
        Assert.True(r.W.Reloading);
        r.At(12.9);                                   // not yet a full second
        Assert.True(r.W.Reloading);
        Assert.Equal(0, r.W.Mag(1));
        r.At(13.05);                                  // first shell in, the reload goes on
        Assert.Equal(1, r.W.Mag(1));
        Assert.Equal(7, r.W.Reserve(1));
        Assert.True(r.W.Reloading);
        r.At(14.1);                                   // second shell
        Assert.Equal(2, r.W.Mag(1));
        Assert.Equal(6, r.W.Reserve(1));
    }

    [Fact]
    public void FiringInterruptsTheShellReloadOnceAShellIsLoaded()
    {
        var r = new Rig(ShellGun());
        r.EmptyMagazine();
        r.At(12.0, reload: true);
        r.At(13.05);                                  // one shell loaded, reload still running
        Assert.True(r.W.Reloading);
        Assert.Equal(1, r.W.Mag(1));
        var shot = r.At(13.5, fire: true);            // interrupt: fires the loaded shell
        Assert.NotNull(shot);
        Assert.False(r.W.Reloading);
        Assert.Equal(0, r.W.Mag(1));
    }

    [Fact]
    public void FiringWithNoShellLoadedDoesNotInterruptTheReload()
    {
        var r = new Rig(ShellGun());
        r.EmptyMagazine();
        r.At(12.0, reload: true);
        Assert.Null(r.At(12.5, fire: true));          // nothing loaded yet: a dry pull
        Assert.True(r.W.Reloading);
    }
}
