using Godot;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>What a navmesh walker meets on its way (<see cref="WalkerGround"/>); here the parked vehicles it is asked about.</summary>
public partial class NetServer
{
    WalkerGround? _walkerGround;

    /// <summary>The geometry the walkers ask, shared by every citizen and member.</summary>
    IWalkerGround WalkerWorld => _walkerGround ??= new WalkerGround(Map, () => Query);
}
