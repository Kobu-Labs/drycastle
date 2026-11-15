namespace Drycastle.ShooterCore;

/// <summary>When to start a blow clip: after <see cref="DelayS"/> seconds of Windup, from <see cref="FromS"/> seconds into the clip.</summary>
public readonly record struct BlowPlan(float DelayS, float FromS);

/// <summary>
/// What a Gang member looks like, from its look number alone (the 16-bit number the server rolls and every client reads from the entity's owner field), so every client draws the same member: pure, Godot-free
/// (docs/events/thieves/08). Each choice mixes the look with its own salt, so the model, the atlas and the cigarette are independent of one another.
/// </summary>
public static class GangLook
{
    const uint ModelSalt = 1, AtlasSalt = 2, SmokeSalt = 3, DeathSalt = 4, BlowSalt = 5;

    /// <summary>A well mixed 32-bit number for a look and a purpose.</summary>
    public static uint Mix(ushort look, uint salt)
    {
        var h = (look + 1u) * 2654435761u ^ (salt + 1u) * 0x9E3779B9u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
        return h;
    }

    /// <summary>The model of a member: of the kind's list.</summary>
    public static string Model(GangView view, GangMemberKind kind, ushort look)
    {
        var list = view.Models.Of(kind);
        return list[(int)(Mix(look, ModelSalt) % (uint)list.Count)];
    }

    /// <summary>Whether the model is a feminine body (its clips are the _femn ones).</summary>
    public static bool IsFemn(GangView view, string model) => view.Models.Femn.Contains(model);

    /// <summary>The atlas of a member: a plain uniform pick over all of them.</summary>
    public static string Atlas(GangView view, ushort look) => view.Atlases[(int)(Mix(look, AtlasSalt) % (uint)view.Atlases.Count)];

    /// <summary>Whether this member has a lit cigarette: one in <see cref="GangCigarette.OneIn"/>.</summary>
    public static bool Smokes(GangView view, ushort look) => Mix(look, SmokeSalt) % (uint)Math.Max(1, view.Dress.Cigarette.OneIn) == 0;

    /// <summary>The death clip of a member (a name from the list; "" when there is none).</summary>
    public static string Death(GangView view, ushort look) => view.Clips.Death.Count == 0 ? "" : view.Clips.Death[(int)(Mix(look, DeathSalt) % (uint)view.Clips.Death.Count)];

    /// <summary>The blow of a member's <paramref name="count"/>th swing, or null when its kind has none.</summary>
    public static GangBlow? Blow(GangView view, GangMemberKind kind, ushort look, int count)
    {
        var list = view.Blows.Of(kind);
        return list.Count == 0 ? null : list[(int)((Mix(look, BlowSalt) + (uint)count) % (uint)list.Count)];
    }

    /// <summary>The clip name for a body: <c>{g}</c> becomes masc or femn.</summary>
    public static string ClipName(string template, bool femn) => template.Replace("{g}", femn ? "femn" : "masc");

    /// <summary>
    /// The server's blow lands <paramref name="windupS"/> after Windup begins. The clip must be at its peak then: started that long before the peak, or, when the clip's wind-in
    /// (seek to peak) is shorter than the windup, held at its seek point and started late.
    /// </summary>
    public static BlowPlan Plan(GangBlow blow, float windupS)
    {
        var from = blow.PeakS - windupS;
        return from >= blow.SeekS ? new BlowPlan(0, from) : new BlowPlan(windupS - (blow.PeakS - blow.SeekS), blow.SeekS);
    }

    /// <summary>How fast a climb clip plays so the hands and feet keep up with a climb of <paramref name="climbSpeedMps"/> (the clip travels <see cref="GangClimbView.ClipSpeedMps"/> at 1).</summary>
    public static float ClimbPlayback(GangClimbView climb, float climbSpeedMps) => Math.Max(0.1f, climbSpeedMps / climb.ClipSpeedMps);
}
