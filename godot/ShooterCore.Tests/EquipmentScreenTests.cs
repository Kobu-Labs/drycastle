using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Issue #268: focus on the Equipment screen (arrows) and the rules of its implicit controls (click, Enter, Delete, drag and drop).</summary>
public class EquipmentScreenTests
{
    static readonly CellRef Head = CellRef.Slot(EquipSlot.Headgear);
    static CellRef Bag(int cell) => CellRef.BagCell(cell);

    /// <summary>A kit: the headgear slot and a bag, each cell empty (null) or holding an item of a slot.</summary>
    static Func<CellRef, EquipSlot?> Kit(EquipSlot? worn, params EquipSlot?[] bag) =>
        c => c.Home == CellHome.Worn ? (c.Index == 0 ? worn : null) : c.Index < bag.Length ? bag[c.Index] : null;

    const EquipSlot Hat = EquipSlot.Headgear;
    const EquipSlot Other = (EquipSlot)1;

    // ---- arrows ----

    [Fact]
    public void TheBagIsThreeColumnsOfTwoRows()
    {
        Assert.Equal(3, EquipmentNav.BagColumns);
        Assert.Equal(2, Drycastle.ShooterCore.Bag.Cells / EquipmentNav.BagColumns);
    }

    [Theory]
    [InlineData(0, NavDir.Right, 1)]
    [InlineData(1, NavDir.Right, 2)]
    [InlineData(2, NavDir.Right, null)]
    [InlineData(3, NavDir.Right, 4)]
    [InlineData(5, NavDir.Right, null)]
    [InlineData(0, NavDir.Down, 3)]
    [InlineData(2, NavDir.Down, 5)]
    [InlineData(3, NavDir.Down, null)]
    [InlineData(4, NavDir.Up, 1)]
    [InlineData(1, NavDir.Up, null)]
    [InlineData(5, NavDir.Left, 4)]
    [InlineData(4, NavDir.Left, 3)]
    public void ArrowsMoveWithinTheBagGridAndStopAtItsEdges(int from, NavDir dir, int? to)
    {
        var next = EquipmentNav.Move(Bag(from), dir);
        Assert.Equal(to is { } t ? Bag(t) : null, next);
    }

    [Fact]
    public void LeftFromTheFirstBagColumnGoesToTheSlotAndRightFromTheSlotEntersTheBag()
    {
        Assert.Equal(Head, EquipmentNav.Move(Bag(0), NavDir.Left));
        Assert.Equal(Head, EquipmentNav.Move(Bag(3), NavDir.Left)); // one slot: both rows reach it
        Assert.Equal(Bag(0), EquipmentNav.Move(Head, NavDir.Right));
    }

    [Fact]
    public void TheSlotColumnMovesUpAndDownAndStopsAtItsEnds()
    {
        Assert.Null(EquipmentNav.Move(Head, NavDir.Up));
        Assert.Null(EquipmentNav.Move(Head, NavDir.Down)); // one slot only
        Assert.Null(EquipmentNav.Move(Head, NavDir.Left));
        var second = CellRef.Slot(Other);
        Assert.Equal(second, EquipmentNav.Move(Head, NavDir.Down, slots: 2));
        Assert.Equal(Head, EquipmentNav.Move(second, NavDir.Up, slots: 2));
        Assert.Equal(Bag(3), EquipmentNav.Move(second, NavDir.Right, slots: 2)); // the bag row level with the slot
        Assert.Equal(second, EquipmentNav.Move(Bag(3), NavDir.Left, slots: 2));
    }

    [Fact]
    public void AShortLastRowStopsTheMovesThatWouldLeaveTheGrid()
    {
        Assert.Null(EquipmentNav.Move(Bag(4), NavDir.Right, bagCells: 5));
        Assert.Null(EquipmentNav.Move(Bag(2), NavDir.Down, bagCells: 5));
        Assert.Equal(Bag(3), EquipmentNav.Move(Bag(0), NavDir.Down, bagCells: 5));
    }

    [Fact]
    public void NoCellIsLeftToMoveFromWhenTheCellDoesNotExist() =>
        Assert.Null(EquipmentNav.Move(Bag(9), NavDir.Left));

    [Fact]
    public void FocusStartsOnTheFirstItemSlotsBeforeTheBagElseTheFirstSlot()
    {
        Assert.Equal(Head, EquipmentNav.First(c => false));
        Assert.Equal(Bag(2), EquipmentNav.First(c => c == Bag(2) || c == Bag(4)));
        Assert.Equal(Head, EquipmentNav.First(c => c == Head || c == Bag(0)));
    }

    [Fact]
    public void ListMovesGoBackAndOnAndStopAtTheEnds()
    {
        Assert.Equal(1, EquipmentNav.MoveInList(0, NavDir.Right, 3));
        Assert.Equal(1, EquipmentNav.MoveInList(2, NavDir.Left, 3));
        Assert.Null(EquipmentNav.MoveInList(2, NavDir.Down, 3));
        Assert.Null(EquipmentNav.MoveInList(0, NavDir.Up, 3));
    }

    // ---- click and Enter ----

    [Fact]
    public void ClickingAWornItemTakesItOffUnlessTheBagIsFull()
    {
        var kit = Kit(Hat);
        Assert.Equal(new ItemWish(WishKind.Unequip, 0), EquipmentWishes.Activate(Head, kit, bagFull: false));
        Assert.Equal(new ItemWish(WishKind.BagFull), EquipmentWishes.Activate(Head, kit, bagFull: true));
    }

    [Fact]
    public void ClickingABagItemPutsItOnEvenWithAFullBag()
    {
        var kit = Kit(Hat, Hat, null, Hat);
        Assert.Equal(new ItemWish(WishKind.Equip, 0), EquipmentWishes.Activate(Bag(0), kit, bagFull: true));
        Assert.Equal(new ItemWish(WishKind.Equip, 2), EquipmentWishes.Activate(Bag(2), kit, bagFull: false));
    }

    [Fact]
    public void AnEmptyCellDoesNothingOnClickOrDelete()
    {
        var kit = Kit(null, null, Hat);
        Assert.Equal(ItemWish.None, EquipmentWishes.Activate(Head, kit, false));
        Assert.Equal(ItemWish.None, EquipmentWishes.Activate(Bag(0), kit, false));
        Assert.Equal(ItemWish.None, EquipmentWishes.Drop(Head, kit));
        Assert.Equal(ItemWish.None, EquipmentWishes.Drop(Bag(0), kit));
    }

    // ---- Delete ----

    [Fact]
    public void DeleteDropsTheItemOfTheCellFromWhereItIs()
    {
        var kit = Kit(Hat, null, Hat);
        Assert.Equal(new ItemWish(WishKind.DropWorn, 0), EquipmentWishes.Drop(Head, kit));
        Assert.Equal(new ItemWish(WishKind.DropBagged, 1), EquipmentWishes.Drop(Bag(1), kit));
    }

    // ---- drag and drop ----

    [Fact]
    public void ABagItemDroppedOnItsSlotOrOnTheAvatarIsPutOn()
    {
        var kit = Kit(null, Hat);
        Assert.Equal(new ItemWish(WishKind.Equip, 0), EquipmentWishes.Drag(Bag(0), DropTarget.Onto(Head), kit));
        Assert.Equal(new ItemWish(WishKind.Equip, 0), EquipmentWishes.Drag(Bag(0), DropTarget.Avatar, kit));
    }

    [Fact]
    public void ABagItemDoesNotGoInAnotherSlotNorMoveWithinTheBag()
    {
        var kit = Kit(null, Other, null);
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Bag(0), DropTarget.Onto(Head), kit)); // not a hat
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Bag(0), DropTarget.Onto(Bag(2)), kit)); // the bag is not reordered
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Bag(0), DropTarget.Onto(Bag(0)), kit));
    }

    [Fact]
    public void AWornItemDroppedOnAnEmptyBagCellIsTakenOff()
    {
        var kit = Kit(Hat, null, null);
        Assert.Equal(new ItemWish(WishKind.Unequip, 0), EquipmentWishes.Drag(Head, DropTarget.Onto(Bag(1)), kit));
    }

    [Fact]
    public void AWornItemDroppedOnABagItemOfItsSlotSwapsThemAndOnAnotherKindIsRefused()
    {
        var kit = Kit(Hat, Hat, Other);
        Assert.Equal(new ItemWish(WishKind.Equip, 0), EquipmentWishes.Drag(Head, DropTarget.Onto(Bag(0)), kit));
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Head, DropTarget.Onto(Bag(1)), kit));
    }

    [Fact]
    public void AWornItemIsNotAValidDropOnItsOwnSlotOrTheAvatar()
    {
        var kit = Kit(Hat);
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Head, DropTarget.Onto(Head), kit));
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Head, DropTarget.Avatar, kit));
    }

    [Fact]
    public void DroppingOutsideThePanelDropsOnTheGroundAndInsideItButOnNothingChangesNothing()
    {
        var kit = Kit(Hat, Hat);
        Assert.Equal(new ItemWish(WishKind.DropWorn, 0), EquipmentWishes.Drag(Head, DropTarget.Ground, kit));
        Assert.Equal(new ItemWish(WishKind.DropBagged, 0), EquipmentWishes.Drag(Bag(0), DropTarget.Ground, kit));
        Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Bag(0), DropTarget.Nothing, kit));
    }

    [Fact]
    public void DraggingAnEmptyCellDoesNothingAnywhere()
    {
        var kit = Kit(null);
        foreach (var to in new[] { DropTarget.Ground, DropTarget.Avatar, DropTarget.Onto(Head), DropTarget.Onto(Bag(1)) })
            Assert.Equal(ItemWish.None, EquipmentWishes.Drag(Bag(0), to, kit));
    }

    [Fact]
    public void ADropIsValidExactlyWhenItWouldDoSomething()
    {
        var kit = Kit(null, Hat);
        Assert.True(EquipmentWishes.CanDrop(Bag(0), DropTarget.Onto(Head), kit));
        Assert.False(EquipmentWishes.CanDrop(Bag(0), DropTarget.Onto(Bag(3)), kit));
    }

    // ---- hints ----

    [Fact]
    public void TheHintRowShowsKeysOnTheKeyboardAndButtonsOnAGamepad()
    {
        Assert.Equal(("F", "Enter", "Delete"), EquipmentHints.Names(InputDevice.KeyboardMouse, "F"));
        Assert.Equal(("Y", "A", "X"), EquipmentHints.Names(InputDevice.Gamepad, "F"));
        Assert.Equal(("G", "Enter", "Delete"), EquipmentHints.Names(InputDevice.KeyboardMouse, "G")); // a rebound open key
    }
}
