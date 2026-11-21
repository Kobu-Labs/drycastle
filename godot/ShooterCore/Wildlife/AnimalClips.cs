namespace Drycastle.ShooterCore;

/// <summary>The clips a client plays on an animal, by what they show (not by the model's own name for them).</summary>
public enum AnimalClip { Idle, IdleAlt, Eat, Walk, Run, Death, HitLeft, HitRight, SitStart, SitIdle, SitUp, Bark, Cruise, Fall, Perched }

/// <summary>
/// The one table that maps what an animal does to the names of clips in its model. The pet packs name them <c>Idle Walk Run Eat Sit_Start Sit_Idle Sit_Up Bark</c>;
/// the animal pack (deer, wolf, ...) <c>Idle Idle_2 Eating Walk Gallop Death Idle_HitReact_Left/Right</c>. Every clip lists the names it may have, best first; a model
/// uses the first it has. A clip a model has no name for falls back (see <see cref="Resolve"/>) or is not played.
/// </summary>
public static class AnimalClips
{
    static readonly string[][] Names =
    {
        /* Idle      */ new[] { "Idle" },
        /* IdleAlt   */ new[] { "Idle_2" },
        /* Eat       */ new[] { "Eat", "Eating" },
        /* Walk      */ new[] { "Walk" },
        /* Run       */ new[] { "Run", "Gallop" },
        /* Death     */ new[] { "Death" },
        /* HitLeft   */ new[] { "Idle_HitReact_Left" },
        /* HitRight  */ new[] { "Idle_HitReact_Right" },
        /* SitStart  */ new[] { "Sit_Start" },
        /* SitIdle   */ new[] { "Sit_Idle" },
        /* SitUp     */ new[] { "Sit_Up" },
        /* Bark      */ new[] { "Bark" },
        /* Cruise    */ new[] { "Cruise" },
        /* Fall      */ new[] { "Fall" },
        /* Perched   */ new[] { "Perched" },
    };

    public static IReadOnlyList<string> NamesOf(AnimalClip clip) => Names[(int)clip];

    /// <summary>Clips that play over and over (the rest play once and stop; glTF stores no loop flag, so the client sets it by this).</summary>
    public static bool Loops(AnimalClip clip) => clip is AnimalClip.Idle or AnimalClip.IdleAlt or AnimalClip.Eat or AnimalClip.Walk or AnimalClip.Run or AnimalClip.SitIdle or AnimalClip.Cruise or AnimalClip.Fall or AnimalClip.Perched; // a flyer's clips all loop (Fall tumbles until it lands, Perched breathes)

    /// <summary>The clips a species' model must have: for a flyer Idle, Cruise, Fall and Perched; else the four every animal plays, the death and hit reactions unless it dies from one hit, and the sit and bark clips of the species that sit or bark.</summary>
    public static IEnumerable<AnimalClip> Needed(AnimalDef def)
    {
        yield return AnimalClip.Idle;
        if (def.Flies)
        {
            yield return AnimalClip.Cruise;
            yield return AnimalClip.Fall;
            yield return AnimalClip.Perched;
            yield break;
        }
        yield return AnimalClip.Eat;
        yield return AnimalClip.Walk;
        yield return AnimalClip.Run;
        if (!def.OneHit)
        {
            yield return AnimalClip.Death;
            yield return AnimalClip.HitLeft;
            yield return AnimalClip.HitRight;
        }
        if (def.Sit != null)
        {
            yield return AnimalClip.SitStart;
            yield return AnimalClip.SitIdle;
            yield return AnimalClip.SitUp;
        }
        if (def.Bark != null) yield return AnimalClip.Bark;
    }

    /// <summary>
    /// The model's name for every <see cref="AnimalClip"/> (indexed by it), given which names it has; null where it has none. IdleAlt, SitIdle and Bark
    /// fall back to the idle clip (a dog without a bark just stands); the others stay null and are not played.
    /// </summary>
    public static string?[] Resolve(Func<string, bool> has)
    {
        var r = new string?[Names.Length];
        for (var i = 0; i < Names.Length; i++) r[i] = Names[i].FirstOrDefault(has);
        r[(int)AnimalClip.IdleAlt] ??= r[(int)AnimalClip.Idle];
        r[(int)AnimalClip.SitIdle] ??= r[(int)AnimalClip.Idle];
        r[(int)AnimalClip.Bark] ??= r[(int)AnimalClip.Idle];
        return r;
    }
}
