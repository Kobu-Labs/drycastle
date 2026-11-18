namespace Drycastle.ShooterCore;

/// <summary>The weapon ids a hunt carries that are not in weapons.json.</summary>
public static class HuntWeapons
{
    /// <summary>A wild animal run over by a vehicle: no distance, no hit diagram, never a longest shot.</summary>
    public const string RunOver = "run_over";
    /// <summary>A mystery creature caught by winning its Event: no weapon of the player's.</summary>
    public const string Event = "event";
}

/// <summary>
/// The index species of the Hunter's Log (GLOSSARY "Index species"): what a record and a Life List tile are kept per. The one table of which
/// <c>animals.json</c> species fall under which index id (deer and doe are one Deer). A hunt row keeps both the index id and the exact species.
/// </summary>
public static class IndexSpecies
{
    /// <summary>The index ids, in Life List order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { "deer", "stag", "moose", "bear", "boar", "wolf", "fox", "raccoon", "skunk", "rabbit" };

    /// <summary>The index id of an animals.json species id; null for a species the log does not keep (pets, livestock, anything unlisted).</summary>
    public static string? Of(string animalId) => animalId switch
    {
        "deer" or "doe" => "deer",
        _ => All.Contains(animalId) ? animalId : null,
    };

    public static bool Is(string species) => All.Contains(species);
}

/// <summary>A creature the log keeps that is not an animal with a weight: caught by an Event (GLOSSARY "Mystery creature"). <see cref="Live"/>: something can catch it today.</summary>
public sealed record Mystery(string Id, string Name, bool Live);

/// <summary>
/// The Mystery registry. Bigfoot is caught when its Event is won; the Unicorn when a player kills it (a rare wild animal, <see cref="HuntRecord.ForUnicorn"/>); Scylla is registered so its Life List tile exists, and
/// needs only a catch hook later. A viewer who has not caught one is sent <see cref="HiddenId"/> instead of its id (<see cref="HuntRedaction"/>).
/// </summary>
public static class Mysteries
{
    public const string Bigfoot = "bigfoot", Unicorn = "unicorn", Scylla = "scylla";

    /// <summary>The filter key for "every mystery hunt I cannot see" (the one "Mystery hunts" option).</summary>
    public const string AllHidden = "?";

    const string HiddenPrefix = "mystery-";

    public static readonly IReadOnlyList<Mystery> All = new[]
    {
        new Mystery(Bigfoot, "Bigfoot", Live: true),
        new Mystery(Unicorn, "Unicorn", Live: true),
        new Mystery(Scylla, "Scylla", Live: false),
    };

    public static Mystery? Find(string id) => All.FirstOrDefault(m => m.Id == id);
    public static bool Is(string species) => Find(species) != null;

    /// <summary>What a viewer who has not caught <paramref name="id"/> is sent in its place: stable per creature (tiles stay apart) and says nothing of which one it is.</summary>
    public static string HiddenId(string id) => HiddenPrefix + (IndexOf(id) + 1);

    public static bool IsHiddenId(string species) => species.StartsWith(HiddenPrefix, StringComparison.Ordinal);

    static int IndexOf(string id)
    {
        for (var i = 0; i < All.Count; i++) if (All[i].Id == id) return i;
        return -1;
    }
}

/// <summary>Every species key of the log: the index species, then the mysteries. Life List and Records order.</summary>
public static class HuntSpecies
{
    public static IReadOnlyList<string> Keys { get; } = IndexSpecies.All.Concat(Mysteries.All.Select(m => m.Id)).ToArray();
    public static bool IsKnown(string species) => IndexSpecies.Is(species) || Mysteries.Is(species);
}
