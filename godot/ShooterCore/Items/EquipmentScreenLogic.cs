namespace Drycastle.ShooterCore;

/// <summary>Where an item cell of the Equipment screen holds its item: a worn slot (index = <see cref="EquipSlot"/>) or a bag cell.</summary>
public enum CellHome { Worn, Bag }

/// <summary>One cell of the Equipment screen.</summary>
public readonly record struct CellRef(CellHome Home, int Index)
{
    public static CellRef Slot(EquipSlot slot) => new(CellHome.Worn, (int)slot);
    public static CellRef BagCell(int cell) => new(CellHome.Bag, cell);
}

public enum NavDir { Left, Right, Up, Down }

/// <summary>
/// Keyboard and gamepad focus on the Equipment screen (arrows move focus). The layout is a column of worn-slot cells on the left, top to bottom in slot
/// order, and the bag as a grid <see cref="BagColumns"/> wide on the right. Pure, so the screen and the tests agree; a move off an edge stays where it is.
/// </summary>
public static class EquipmentNav
{
    public const int BagColumns = 3;

    /// <summary>The cell a move from <paramref name="from"/> in <paramref name="dir"/> lands on, or null at an edge (focus stays).</summary>
    public static CellRef? Move(CellRef from, NavDir dir, int slots = -1, int bagCells = Bag.Cells, int bagColumns = BagColumns)
    {
        if (slots < 0) slots = Equipment.SlotCount;
        var rows = Math.Max(1, (bagCells + bagColumns - 1) / bagColumns);
        if (from.Home == CellHome.Worn)
        {
            if (from.Index < 0 || from.Index >= slots) return null;
            return dir switch
            {
                NavDir.Up => from.Index > 0 ? CellRef.Slot((EquipSlot)(from.Index - 1)) : null,
                NavDir.Down => from.Index < slots - 1 ? CellRef.Slot((EquipSlot)(from.Index + 1)) : null,
                NavDir.Right => bagCells > 0 ? CellRef.BagCell(Math.Min(from.Index, rows - 1) * bagColumns) : null, // the bag row level with the slot
                _ => null,
            };
        }
        var cell = from.Index;
        if (cell < 0 || cell >= bagCells) return null;
        var col = cell % bagColumns;
        var row = cell / bagColumns;
        switch (dir)
        {
            case NavDir.Left:
                if (col > 0) return CellRef.BagCell(cell - 1);
                return slots > 0 ? CellRef.Slot((EquipSlot)Math.Min(row, slots - 1)) : null;
            case NavDir.Right:
                return col < bagColumns - 1 && cell + 1 < bagCells ? CellRef.BagCell(cell + 1) : null;
            case NavDir.Up:
                return row > 0 ? CellRef.BagCell(cell - bagColumns) : null;
            default:
                return cell + bagColumns < bagCells ? CellRef.BagCell(cell + bagColumns) : null;
        }
    }

    /// <summary>The cell that has focus when the screen opens: the first cell holding an item (slots first, then the bag), else the first slot.</summary>
    public static CellRef First(Func<CellRef, bool> holdsItem, int slots = -1, int bagCells = Bag.Cells)
    {
        if (slots < 0) slots = Equipment.SlotCount;
        for (var s = 0; s < slots; s++) if (holdsItem(new CellRef(CellHome.Worn, s))) return new CellRef(CellHome.Worn, s);
        for (var c = 0; c < bagCells; c++) if (holdsItem(CellRef.BagCell(c))) return CellRef.BagCell(c);
        return new CellRef(CellHome.Worn, 0);
    }

    /// <summary>The next cell of a plain list (the corpse chooser's items) in a direction, or null at an end. Left and up go back, right and down go on.</summary>
    public static int? MoveInList(int index, NavDir dir, int count)
    {
        var next = dir is NavDir.Left or NavDir.Up ? index - 1 : index + 1;
        return next >= 0 && next < count ? next : null;
    }
}

/// <summary>What the player asked of the server with a click, a key or a drop on the Equipment screen.</summary>
public enum WishKind
{
    /// <summary>Nothing to do (an empty cell, or a drop that is not valid).</summary>
    None,
    /// <summary>Put the bag item in cell <see cref="ItemWish.Index"/> on (what was worn in its slot goes into that cell).</summary>
    Equip,
    /// <summary>Take off the worn item of slot <see cref="ItemWish.Index"/> into the bag.</summary>
    Unequip,
    DropWorn,
    DropBagged,
    /// <summary>Taking off needs an empty bag cell and there is none: the server would refuse it, so the screen answers itself (the cell shakes, the error sound).</summary>
    BagFull,
}

public readonly record struct ItemWish(WishKind Kind, int Index = 0)
{
    public static readonly ItemWish None = new(WishKind.None);
}

/// <summary>What a dragged item is dropped on.</summary>
public enum DropOn { Nothing, Cell, Avatar, Ground }

public readonly record struct DropTarget(DropOn On, CellRef Cell = default)
{
    public static DropTarget Onto(CellRef cell) => new(DropOn.Cell, cell);
    public static readonly DropTarget Avatar = new(DropOn.Avatar);
    public static readonly DropTarget Ground = new(DropOn.Ground);
    public static readonly DropTarget Nothing = new(DropOn.Nothing);
}

/// <summary>
/// The implicit controls of the Equipment screen as rules: a click or Enter on a cell, Delete on a cell and a drag from one cell to a target. Each
/// returns the wish to send (the server decides; it is never applied on the client first). <paramref name="slotOf"/> says which equipment slot the item
/// in a cell belongs to, or null for an empty cell.
/// </summary>
public static class EquipmentWishes
{
    /// <summary>Click or Enter: a worn item goes to the bag, a bagged item is put on (swapping what was worn into its cell).</summary>
    public static ItemWish Activate(CellRef cell, Func<CellRef, EquipSlot?> slotOf, bool bagFull)
    {
        if (slotOf(cell) == null) return ItemWish.None;
        if (cell.Home == CellHome.Bag) return new ItemWish(WishKind.Equip, cell.Index);
        return bagFull ? new ItemWish(WishKind.BagFull) : new ItemWish(WishKind.Unequip, cell.Index);
    }

    /// <summary>Delete: the item of the cell goes on the ground.</summary>
    public static ItemWish Drop(CellRef cell, Func<CellRef, EquipSlot?> slotOf) =>
        slotOf(cell) == null ? ItemWish.None
        : cell.Home == CellHome.Worn ? new ItemWish(WishKind.DropWorn, cell.Index) : new ItemWish(WishKind.DropBagged, cell.Index);

    /// <summary>
    /// A drag from <paramref name="from"/> released on <paramref name="to"/>. The avatar and the item's own slot take a bagged item; a worn item goes
    /// to any bag cell that is empty (the server puts it in the first empty one) or onto one that holds an item of the same slot (the two swap); outside
    /// the panel an item drops on the ground. The bag is not reordered (the server has no such move), and anything else is not a valid target.
    /// </summary>
    public static ItemWish Drag(CellRef from, DropTarget to, Func<CellRef, EquipSlot?> slotOf)
    {
        if (slotOf(from) is not { } slot) return ItemWish.None;
        switch (to.On)
        {
            case DropOn.Ground:
                return from.Home == CellHome.Worn ? new ItemWish(WishKind.DropWorn, from.Index) : new ItemWish(WishKind.DropBagged, from.Index);
            case DropOn.Avatar:
                return from.Home == CellHome.Bag ? new ItemWish(WishKind.Equip, from.Index) : ItemWish.None;
            case DropOn.Cell when to.Cell.Home == CellHome.Worn:
                return from.Home == CellHome.Bag && (int)slot == to.Cell.Index ? new ItemWish(WishKind.Equip, from.Index) : ItemWish.None;
            case DropOn.Cell when from.Home == CellHome.Worn:
                var there = slotOf(to.Cell);
                if (there == null) return new ItemWish(WishKind.Unequip, from.Index);
                return there == slot ? new ItemWish(WishKind.Equip, to.Cell.Index) : ItemWish.None;
            default:
                return ItemWish.None;
        }
    }

    /// <summary>True when releasing a drag from <paramref name="from"/> on <paramref name="to"/> would do something (the valid target highlights).</summary>
    public static bool CanDrop(CellRef from, DropTarget to, Func<CellRef, EquipSlot?> slotOf) => Drag(from, to, slotOf).Kind != WishKind.None;
}

/// <summary>Which device the player used last, for the hint row to show keys or buttons.</summary>
public enum InputDevice { KeyboardMouse, Gamepad }

/// <summary>The names the hint row shows for the controls of the Equipment screen on each device.</summary>
public static class EquipmentHints
{
    /// <summary>The gamepad buttons (Xbox naming) for opening the screen, activating a cell and dropping its item.</summary>
    public const string PadOpen = "Y", PadActivate = "A", PadDrop = "X";
    public const string KeyActivate = "Enter", KeyDrop = "Delete";

    public static (string Open, string Activate, string Drop) Names(InputDevice device, string openKey) =>
        device == InputDevice.Gamepad ? (PadOpen, PadActivate, PadDrop) : (openKey, KeyActivate, KeyDrop);
}
