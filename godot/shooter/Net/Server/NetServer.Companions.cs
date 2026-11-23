using Drycastle.ShooterCore.Companions;

namespace Drycastle.Shooter.Net.Server;

public partial class NetServer
{
    /// <summary>companions.json: the Companions of the game (set by the host).</summary>
    public CompanionsFile CompanionData = new();
}
