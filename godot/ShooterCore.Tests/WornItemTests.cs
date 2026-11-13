using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Issue #266: what a player wears reaches every client (event 194), the item's model, and its icon.</summary>
public class WornItemTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static ItemsFile Items => Shipped.Items;
    static int Bigfoot => Items.IndexOf("bigfoot_head");

    static Equipment Wearing(int pickups = 0)
    {
        var worn = new Equipment(Items);
        worn.Wear(EquipSlot.Headgear, new ItemStack(Bigfoot, pickups));
        return worn;
    }

    // ---- the wire ----

    [Fact]
    public void WornItemsNamePlayerAndItemPerSlotAndLeaveThePickupCountOut()
    {
        var bytes = ItemEvents.EncodeWorn(7, Wearing(pickups: 2));
        Assert.Equal(new byte[] { 7, 0, 1, (byte)(Bigfoot + 1) }, bytes);
        Assert.True(ItemEvents.TryDecodeWorn(bytes, out var player, out var slots));
        Assert.Equal((ushort)7, player);
        Assert.Equal(new int?[] { Bigfoot }, slots);
    }

    [Fact]
    public void BareHandsAndHighPlayerIdsRoundTrip()
    {
        Assert.True(ItemEvents.TryDecodeWorn(ItemEvents.EncodeWorn(0x1234, new Equipment(Items)), out var player, out var slots));
        Assert.Equal((ushort)0x1234, player);
        Assert.Equal(new int?[] { null }, slots); // nothing worn: the other clients take the item off again
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 7, 0 })]                 // no slot count
    [InlineData(new byte[] { 7, 0, 1 })]              // the slot is missing
    [InlineData(new byte[] { 7, 0, 1, 1, 1 })]        // one slot too many
    [InlineData(new byte[] { 7, 0, 2, 1 })]           // the count says two
    public void MalformedWornItemsAreRefused(byte[] payload) => Assert.False(ItemEvents.TryDecodeWorn(payload, out _, out _));

    [Fact]
    public void WornItemsIsKind194InTheItemsBlockAndTheProtocolMovedOn()
    {
        Assert.Equal(194, (int)EventKind.WornItems);
        Assert.True(Wire.ProtocolVersion >= 30);
    }

    [Fact]
    public void AWornItemsEventTravelsAsAnEventMessage()
    {
        var msg = new EventMsg((byte)EventKind.WornItems, ItemEvents.EncodeWorn(3, Wearing()));
        var back = Assert.IsType<EventMsg>(Wire.DecodeReliable(Wire.Encode(msg)));
        Assert.Equal((byte)EventKind.WornItems, back.Kind);
        Assert.True(ItemEvents.TryDecodeWorn(back.Payload, out var player, out var slots));
        Assert.Equal((ushort)3, player);
        Assert.Equal(new int?[] { Bigfoot }, slots);
        Assert.False(Wire.IsEffect(EventKind.WornItems)); // state, not an effect: it must not be lost
    }

    // ---- the model ----

    [Fact]
    public void TheBigfootHeadHasAModelThatIsInTheAssetLibrary()
    {
        var head = Items.Find("bigfoot_head")!;
        Assert.Equal("drycastle-generated-models/bigfoot_head.glb", head.Model);
        var nodes = AmphibiousModelTests.Nodes(head.Model); // finds assets/<model>, checks it is a .glb and reads its nodes
        Assert.Contains("bigfoot_head", nodes.Keys);
    }

    [Fact]
    public void AnItemMayHaveNoModelAndIsStillAnItem()
    {
        var items = ItemsFile.Parse("""{"version":1,"items":[{"id":"hat","name":"Hat"}]}""");
        Assert.Equal("", items.Items[0].Model);
    }

    [Theory]
    [InlineData("../secret.glb")]
    [InlineData("/etc/hat.glb")]
    [InlineData("models/hat.png")]
    [InlineData("models/hat")]
    public void AModelThatLeavesTheAssetLibraryOrIsNotAGlbIsRefused(string model) =>
        Assert.Throws<DataFileException>(() => ItemsFile.Parse($$"""{"version":1,"items":[{"id":"hat","name":"Hat","model":"{{model}}"}]}"""));

    // ---- the icon ----

    /// <summary>godot/shooter/ui/icons in the repository, found by walking up from the test output folder.</summary>
    static string IconsDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "godot", "shooter", "ui", "icons");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("godot/shooter/ui/icons not found above " + AppContext.BaseDirectory);
        }
    }

    static string OnDisk(string resPath) => Path.Combine(IconsDir, resPath.Substring(IconRegistry.Root.Length));

    [Fact]
    public void EveryShippedItemHasAnIconFileAndTheFallbackExists()
    {
        var registry = new IconRegistry(Shipped);
        foreach (var item in Items.Items)
        {
            Assert.True(registry.Knows(IconKind.Item, item.Id), item.Id);
            Assert.True(File.Exists(OnDisk(IconRegistry.PathFor(IconKind.Item, item.Id))), "no icon for item " + item.Id);
            Assert.Equal("res://ui/icons/items/" + item.Id + ".png", registry.Resolve(IconKind.Item, item.Id, p => File.Exists(OnDisk(p))));
        }
        Assert.True(File.Exists(OnDisk(IconRegistry.ItemFallback)), "item fallback icon missing");
    }

    [Fact]
    public void AnItemWithoutAnIconGetsTheItemFallbackNotThePlaceholder()
    {
        var registry = new IconRegistry([], [], [], [], ["bigfoot_head", "hat"]);
        Assert.Equal(IconRegistry.ItemFallback, registry.Resolve(IconKind.Item, "not_an_item"));          // unknown id
        Assert.Equal(IconRegistry.ItemFallback, registry.Resolve(IconKind.Item, "hat", _ => false));       // known, no file
        Assert.Equal(IconRegistry.ItemFallback, registry.Resolve(IconKind.Item, null));
        Assert.Equal("res://ui/icons/items/hat.png", registry.Resolve(IconKind.Item, "hat", _ => true));
        Assert.NotEqual(IconRegistry.Placeholder, IconRegistry.ItemFallback);
        Assert.Equal(IconRegistry.Placeholder, registry.Resolve(IconKind.Perk, "hat"));                   // the other kinds are as they were
    }
}
