using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Care packages on the server (ticket #124, reward reworked in #151): the crates the care package killstreak drops and the
/// opening request (<see cref="EventKind.OpenCarePackage"/>). Opening grants a random killstreak from killstreaks.json
/// (<see cref="StreakRewards"/>) into the opener's reward slots (<see cref="KillstreakService.Grant"/>), which tells the
/// owner through <see cref="EventKind.StreakGranted"/>. The rules live in <see cref="CarePackageBook"/>; this file wires
/// them to the match, the physics ground and the streak service.
/// </summary>
public partial class NetServer
{
    CarePackageBook _carePackages = null!;
    readonly Random _carePackageRng = new();

    /// <summary>Registers the crate type and builds the book from the killstreak reward table. Runs after the match exists.</summary>
    void BuildCarePackages()
    {
        CarePackageRules.Register(WorldEntityRegistry.Shared);
        _carePackages = new CarePackageBook(_match.Entities, Killstreaks.Rewards);
    }

    /// <summary>The care package implementation for <see cref="BuildStreaks"/>.</summary>
    IKillstreak CarePackageImpl() => new CarePackageStreak(
        _carePackages,
        owner => _byId.TryGetValue(owner, out var c) ? c.Body.Yaw : 0f,
        GroundBelow);

    /// <summary>Height of the ground under a point (a vertical ray), or null when nothing is below it.</summary>
    float? GroundBelow(NVec3 p)
    {
        var space = World.GetWorld3D().DirectSpaceState;
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(p.X, p.Y + CarePackageRules.StartHeightMetres, p.Z),
            new Vector3(p.X, p.Y - CarePackageRules.StartHeightMetres, p.Z));
        var hit = space.IntersectRay(q);
        return hit.Count == 0 ? null : hit["position"].AsVector3().Y;
    }

    /// <summary>A client asked to open a crate (after holding the key). The server checks the player, the crate and the reach.</summary>
    void OnOpenCarePackage(Client c, byte[] payload)
    {
        if (!CarePackageCodec.TryDecodeOpen(payload, out var crate)) return;
        if (!c.Joined || c.W == null || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) return;
        var result = _carePackages.TryOpen(crate, N(c.Body.GlobalPosition), _carePackageRng, out var streakId);
        if (result != OpenResult.Ok || streakId == null)
        {
            GD.Print($"server: {c.Name} care package {crate} not opened: {result}");
            return;
        }
        // the reward does not touch the Streak counter; the service sends StreakGranted to the opener
        var grant = _streaks.Grant(c.Id, streakId);
        GD.Print($"server: {c.Name} opened a care package: {streakId}" + (grant?.Replaced is { } old ? $" (replaced {old})" : ""));
    }
}
