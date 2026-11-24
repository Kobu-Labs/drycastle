using System.Numerics;

namespace Drycastle.ShooterCore.Scenery;

/// <summary>One shooting star in flight: where its path starts and ends on the sky (unit directions), how far along it is (0..1) and how bright (already faded in and out).</summary>
public readonly record struct Meteor(Vector3 From, Vector3 To, float Progress, float Brightness);

/// <summary>
/// Shooting stars, client-only decoration (each player sees their own). Time is cut into <see cref="SlotSeconds"/> slots; each
/// slot hashes to nothing, one meteor, or (rarely) a bunch, and a bigger fireball comes about once per
/// <see cref="FireballSlotSeconds"/>. Every meteor starts and ends inside its slot, so only the current slot is asked.
/// Pure: the same time always gives the same sky. <see cref="NightGate"/> keeps them to a night that is already dark.
/// </summary>
public static class Meteors
{
    public const float SlotSeconds = 10f, FireballSlotSeconds = 300f;
    public const int MaxActive = 8;
    const float SingleChance = 0.35f, BunchChance = 0.04f, DrawSeconds = 1.1f;

    /// <summary>0 until the night factor is 0.85, 1 at 1.0: no meteors at dusk or dawn, only in the dark.</summary>
    public static float NightGate(float nightFactor) => DayCurve.Smoothstep(0.85f, 1f, nightFactor);

    /// <summary>The meteors in flight at <paramref name="seconds"/> (any steady clock), up to <see cref="MaxActive"/>.</summary>
    public static List<Meteor> Active(double seconds)
    {
        var list = new List<Meteor>();
        var slot = (long)Math.Floor(seconds / SlotSeconds);
        var local = (float)(seconds - slot * SlotSeconds);
        var u = Rand(slot, 0);
        if (u < BunchChance)
        {
            var n = 3 + (int)(Rand(slot, 1) * 3f); // 3..5
            for (var i = 0; i < n; i++)
                Add(list, slot, 10 + i, local, Rand(slot, 2) * 4f + i * (0.15f + 0.5f * Rand(slot, 20 + i)), DrawSeconds, 1f);
        }
        else if (u < BunchChance + SingleChance)
            Add(list, slot, 10, local, Rand(slot, 2) * (SlotSeconds - 2f), DrawSeconds * (0.7f + 0.6f * Rand(slot, 3)), 1f);

        // the fireball: one per long slot, longer and brighter
        var fslot = (long)Math.Floor(seconds / FireballSlotSeconds);
        var flocal = (float)(seconds - fslot * FireballSlotSeconds);
        var fstart = Rand(fslot, 100) * (FireballSlotSeconds - 5f);
        Add(list, fslot + 1_000_000, 50, flocal, fstart, 2.6f, 2.5f);
        return list;
    }

    static void Add(List<Meteor> list, long slot, int salt, float local, float start, float dur, float power)
    {
        if (local < start || local >= start + dur || list.Count >= MaxActive) return;
        var p = (local - start) / dur;
        var az = Rand(slot, salt * 7 + 1) * MathF.Tau;
        var el = (22f + 50f * Rand(slot, salt * 7 + 2)) * MathF.PI / 180f;
        var drop = (12f + 20f * Rand(slot, salt * 7 + 3)) * MathF.PI / 180f * (power > 1f ? 1.4f : 1f);
        var shift = (Rand(slot, salt * 7 + 4) - 0.5f) * 50f * MathF.PI / 180f;
        var el2 = MathF.Max(el - drop, 4f * MathF.PI / 180f);
        var env = MathF.Sin(p * MathF.PI);
        list.Add(new Meteor(Dir(az, el), Dir(az + shift, el2), p, power * env));
    }

    static Vector3 Dir(float az, float el) => new(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));

    /// <summary>The summed brightness of the meteors in flight (1 for a normal one at its peak): the ground lights up with it.</summary>
    public static float Glow(List<Meteor> meteors)
    {
        var g = 0f;
        foreach (var m in meteors) g += m.Brightness;
        return MathF.Min(g, 3f);
    }

    /// <summary>A 0..1 number from (slot, salt): an integer hash, so it is the same on every machine.</summary>
    static float Rand(long slot, int salt)
    {
        var x = unchecked((ulong)slot * 0x9E3779B97F4A7C15UL + (ulong)salt * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (x >> 40) / (float)(1UL << 24);
    }
}
