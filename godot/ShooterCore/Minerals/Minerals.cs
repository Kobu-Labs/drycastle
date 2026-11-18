namespace Drycastle.ShooterCore;

/// <summary>One mineral of the Mineral Log (GLOSSARY "Mineral"). <see cref="Id"/> is the key of a fantasy mineral in <c>crystals.json</c>. <see cref="Fantasy"/>: one with its own effects, not a real-life stone.</summary>
public sealed record Mineral(string Id, string Name, bool Fantasy);

/// <summary>
/// The minerals a player can find (crystals.md): eight real-life stones and twenty fantasy ones. The log shows them in one grid, in <see cref="LogOrder"/>: a fixed
/// shuffle, the same for every player and every start, so the two kinds are mixed and a card is always in the same place.
/// </summary>
public static class Minerals
{
    public static readonly IReadOnlyList<Mineral> All = new Mineral[]
    {
        new("emerald", "Emerald", false), new("sapphire", "Sapphire", false), new("amethyst", "Amethyst", false), new("opal", "Opal", false),
        new("agate", "Agate", false), new("diamond", "Diamond", false), new("obsidian", "Obsidian", false), new("labradorite", "Labradorite", false),
        new("smetiskomite", "Smetiskomite", true), new("pheonixite", "Pheonixite", true), new("frostbite", "Frostbite", true), new("thundernite", "Thundernite", true),
        new("zombite", "Zombite", true), new("bloodstone", "Bloodstone", true), new("magmorite", "Magmorite", true), new("venomite", "Venomite", true),
        new("rosaline", "Rosaline", true), new("nazium", "Nazium", true), new("holystone", "Holystone", true), new("brothers_stone", "Brother's stone", true),
        new("futbalitrine", "Futbalitrine", true), new("senin", "Senin", true), new("povodnovite", "Povodnovite", true), new("dubovite", "Dubovite", true),
        new("hovnovite", "Hovnovite", true), new("micin", "Micin", true), new("marihuanite", "Marihuanite", true), new("kulturite", "Kulturite", true),
    };

    /// <summary>The seed of the log's order. Changing it moves every card.</summary>
    const uint OrderSeed = 0x6d696e65;

    /// <summary>The minerals in the order the log shows them.</summary>
    public static IReadOnlyList<Mineral> LogOrder { get; } = Shuffled(All, OrderSeed);

    public static Mineral? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>A Fisher-Yates shuffle driven by its own small generator (xorshift32), so the order does not depend on the runtime's Random.</summary>
    static IReadOnlyList<Mineral> Shuffled(IReadOnlyList<Mineral> list, uint seed)
    {
        var a = list.ToArray();
        var x = seed;
        for (var i = a.Length - 1; i > 0; i--)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            var j = (int)(x % (uint)(i + 1));
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }
}

/// <summary>A mineral's card as the log draws it: found or not, and which finder of it the player was (1: the first to find it; 0 while not found).</summary>
public readonly record struct MineralTileModel(Mineral Mineral, bool Found, int Rank)
{
    /// <summary>The cards of a player's log, in the log's order. <paramref name="ranks"/>: mineral id to the player's place among its finders; a mineral not in it is not found.</summary>
    public static IReadOnlyList<MineralTileModel> For(IReadOnlyDictionary<string, int>? ranks) =>
        Minerals.LogOrder.Select(m => ranks != null && ranks.TryGetValue(m.Id, out var r) && r > 0 ? new MineralTileModel(m, true, r) : new MineralTileModel(m, false, 0)).ToList();
}

/// <summary>Words of the Mineral Log, culture-free.</summary>
public static class MineralFormat
{
    /// <summary>"1st", "2nd", "3rd", "4th", "11th", "22nd": the place among a mineral's finders.</summary>
    public static string Ordinal(int n)
    {
        var tens = n % 100;
        var suffix = tens is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }
}
