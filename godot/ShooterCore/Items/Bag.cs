namespace Drycastle.ShooterCore;

/// <summary>
/// What a player carries without wearing: <see cref="Cells"/> cells, each empty or one <see cref="ItemStack"/>. Match state next to
/// <see cref="Equipment"/> (ADR 0018): nothing here is stored, a death moves it onto the corpse, leaving the match loses it, a new round
/// keeps it. The cells only hold; the moves between a cell, a worn slot, a corpse and the ground are <see cref="ItemRules"/>.
/// </summary>
public sealed class Bag
{
    public const int Cells = 6;

    readonly ItemStack?[] _cells = new ItemStack?[Cells];

    /// <summary>Set whenever a cell changed; the host clears it after sending the bag to the player.</summary>
    public bool Changed { get; set; }

    /// <summary>The cells in order (null for an empty one): the wire's order.</summary>
    public IReadOnlyList<ItemStack?> Contents => _cells;

    public ItemStack? Get(int cell) => cell is >= 0 and < Cells ? _cells[cell] : null;

    /// <summary>How many cells hold something.</summary>
    public int Count => _cells.Count(c => c != null);

    public bool IsFull => Count == Cells;

    /// <summary>The first empty cell, or -1 when the bag is full.</summary>
    public int FirstEmpty() => Array.FindIndex(_cells, c => c == null);

    /// <summary>Puts <paramref name="stack"/> in the first empty cell and returns the cell, or -1 (nothing changes) when the bag is full.</summary>
    public int Add(ItemStack stack)
    {
        var cell = FirstEmpty();
        if (cell >= 0) Set(cell, stack);
        return cell;
    }

    /// <summary>Puts <paramref name="stack"/> (or nothing) in <paramref name="cell"/>, whatever was there. A cell out of range is ignored.</summary>
    public void Set(int cell, ItemStack? stack)
    {
        if (cell is < 0 or >= Cells) return;
        _cells[cell] = stack;
        Changed = true;
    }

    /// <summary>Empties <paramref name="cell"/> and returns what it held (null when it was empty).</summary>
    public ItemStack? Remove(int cell)
    {
        var was = Get(cell);
        if (was != null) Set(cell, null);
        return was;
    }

    /// <summary>Every item held, in cell order (the empty cells skipped).</summary>
    public IEnumerable<ItemStack> Held => _cells.Where(c => c != null).Select(c => c!.Value);
}
