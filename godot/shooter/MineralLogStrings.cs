using Drycastle.ShooterCore.Mining;

namespace Drycastle.Shooter;

/// <summary>The Mineral Log's strings (English; <see cref="Strings"/> registers them). A mineral's name is "mineral.&lt;id&gt;", from the registry.</summary>
public static class MineralLogStrings
{
    public static readonly (string Key, string Text)[] Table = new (string, string)[]
    {
        ("mineral.title", "Mineral Log"), ("pause.minerallog", "Mineral Log"), ("bind.mineral_log", "Mineral Log"),
        ("mineral.stat.found", "Found"), ("mineral.stat.found.of", "all minerals"), ("mineral.stat.first", "First finder"), ("mineral.stat.first.of", "found before anyone"),
        ("mineral.rank", "{0} found"), ("toast.mineral.found", "You are the {0} to find {1}!"), ("mineral.todo", "Not found yet"),
    }.Concat(Minerals.All.Select(m => ("mineral." + m.Id, m.Name))).ToArray();
}
