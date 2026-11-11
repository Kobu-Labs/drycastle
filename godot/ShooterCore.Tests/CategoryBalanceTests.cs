using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// Shooter v2 balance pass (#133): no hitscan weapon is the unique fastest kill in its category at every sampled range.
/// Time to kill = (shots - 1) x fire interval against a 100 hp body, no headshot, using the shipped falloff.
/// Shotguns are left out (their pellet fan is a Monte Carlo, see ShotgunSmgTests); they are checked by hand in the
/// resolution comment of #133.
/// </summary>
public class CategoryBalanceTests
{
    static readonly WeaponsFile File = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));

    static readonly float[] Ranges = { 5f, 10f, 20f, 30f, 45f, 60f };

    static readonly (string Name, string[] Ids)[] Categories =
    {
        ("assault rifles", new[] { "ak47", "m4a1", "aug", "g36" }),
        ("machine guns", new[] { "rpk", "m249", "m60", "pkm" }),
        ("SMGs", new[] { "mp5", "ump45", "p90" }),
        ("snipers", new[] { "sniper", "m82", "awm", "svd" }),
        ("pistols", new[] { "pistol", "glock17", "deagle" }),
    };

    static float Ttk(WeaponDef w, float distance)
    {
        var shots = (int)Math.Ceiling(MoveConsts.MaxHealth / HitMath.HitscanDamage(w, distance, false));
        return (shots - 1) * w.FireInterval;
    }

    [Theory]
    [MemberData(nameof(CategoryNames))]
    public void NoWeaponIsTheUniqueFastestKillAtEveryRange(string name)
    {
        var (_, ids) = Categories.Single(c => c.Name == name);
        var weapons = ids.Select(id => File.Find(id)!).ToArray();
        // A weapon "dominates" the category when it is the unique fastest kill at every sampled range.
        foreach (var candidate in weapons)
        {
            var uniqueFastestEverywhere = Ranges.All(d =>
            {
                var mine = Ttk(candidate, d);
                return weapons.Where(o => o != candidate).All(o => Ttk(o, d) > mine + 1e-4f);
            });
            Assert.False(uniqueFastestEverywhere, $"{candidate.Id} is the unique fastest kill in {name} at every range");
        }
    }

    public static IEnumerable<object[]> CategoryNames() => Categories.Select(c => new object[] { c.Name });
}
