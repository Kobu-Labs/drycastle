using Drycastle.ShooterCore.Items;

namespace Drycastle.Shooter.Net.Server;

public partial class NetServer
{
    /// <summary>items.json: the equippable items. Empty when the data directory has none.</summary>
    public ItemsFile Items = new();
}
