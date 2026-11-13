namespace Drycastle.ShooterCore;

/// <summary>Where an item is worn. The wire sends the slots in this order (see <see cref="Equipment"/>), so append new ones and never reorder.</summary>
public enum EquipSlot : byte { Headgear = 0 }

/// <summary>
/// One item definition (items.json): what a player can wear that is not a weapon, perk or killstreak. The effects are plain fields with a
/// neutral default, so a new event item adds a field here and a line in the data, and no other item changes.
/// </summary>
public sealed class ItemDef
{
    /// <summary>Lower-case id ("bigfoot_head"), what a chat command and the data name.</summary>
    public string Id { get; set; } = "";
    /// <summary>What the player reads (HUD, prompts, toasts).</summary>
    public string Name { get; set; } = "";
    public EquipSlot Slot { get; set; } = EquipSlot.Headgear;
    /// <summary>How many times the item can be taken from a corpse over its whole life, or null for any number. The attempt after the last makes it crumble.</summary>
    public int? PickupLimit { get; set; }
    /// <summary>A headshot on the wearer is a body hit (<see cref="HitTarget.HeadGuarded"/>).</summary>
    public bool HeadshotImmune { get; set; }
}

/// <summary>
/// items.json, schema version 1: the item table. Optional: a data directory without the file has no items. An item is named on the wire by
/// its position in the list (<see cref="IndexOf"/>), so the list only grows at the end.
/// </summary>
public sealed class ItemsFile : IVersioned
{
    /// <summary>The wire names an item in one byte, 0 meaning none.</summary>
    public const int MaxItems = 254;

    public int Version { get; set; }
    public List<ItemDef> Items { get; set; } = new();

    Dictionary<string, ItemDef>? _byId;

    public static ItemsFile Load(string path) => Validate(DataFile.Load<ItemsFile>(path));

    public static ItemsFile Parse(string json) => Validate(DataFile.Parse<ItemsFile>(json, "items.json"));

    /// <summary>The item with this id, or null.</summary>
    public ItemDef? Find(string? id) => DataIndex.Find(ref _byId, Items, i => i.Id, id);

    /// <summary>The item's position in the list (what the wire carries), or -1.</summary>
    public int IndexOf(string id) => Items.FindIndex(i => i.Id == id);

    /// <summary>The item at a list position, or null when there is none.</summary>
    public ItemDef? At(int index) => index >= 0 && index < Items.Count ? Items[index] : null;

    public static ItemsFile Validate(ItemsFile f)
    {
        const string F = "items.json";
        if (f.Items.Count > MaxItems) throw new DataFileException($"{F}: at most {MaxItems} items");
        var seen = new HashSet<string>();
        foreach (var i in f.Items)
        {
            if (i is null || i.Id == "" || i.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
                throw new DataFileException($"{F}: an item id is lower case letters, digits and underscores");
            if (!seen.Add(i.Id)) throw new DataFileException($"{F}: item id {i.Id} appears twice");
            if (i.Name == "") throw new DataFileException($"{F}: {i.Id}: name is empty");
            if (!Enum.IsDefined(i.Slot)) throw new DataFileException($"{F}: {i.Id}: unknown slot");
            if (i.PickupLimit is < 1) throw new DataFileException($"{F}: {i.Id}: pickup_limit is at least 1 (leave it out for no limit)");
        }
        return f;
    }
}
