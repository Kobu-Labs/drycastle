using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server;

public partial class NetServer
{
    /// <summary>wildlife.json: which wild species the map holds and how many (set by the host).</summary>
    public WildlifeFile WildlifeData = new();
}
