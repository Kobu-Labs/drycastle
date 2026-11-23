using Drycastle.ShooterCore.Turrets;

namespace Drycastle.Shooter.Net.Server;

public partial class NetServer
{
    /// <summary>turret.json: set by the host before the server starts.</summary>
    public TurretFile Turret = null!;
}
