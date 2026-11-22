using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

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
        ThrowCanister,
        GroundBelow);

    /// <summary>Where the owner's thrown canister lands (Q): from the eye along the look, stopped by the first surface.</summary>
    CarePackageRules.ThrowFlight ThrowCanister(ushort owner, NVec3 feet)
    {
        if (!_byId.TryGetValue(owner, out var c)) return new CarePackageRules.ThrowFlight(feet, NVec3.Zero, 0f, CarePackageRules.DropPoint(feet, 0f));
        var eye = new NVec3(feet.X, feet.Y + MovementStep.EyeHeight(c.Body.State.Stance), feet.Z);
        return CarePackageRules.Throw(eye, c.Body.Yaw, c.Body.Pitch, (from, dir, len) =>
            Query.Shot(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len, null).Distance);
    }

    /// <summary>Height of the ground under a point (a vertical ray), or null when nothing is below it.</summary>
    float? GroundBelow(NVec3 p)
    {
        return Query.GroundY(MoverKind.Drop, new Vector3(p.X, p.Y, p.Z), CarePackageRules.StartHeightMetres, CarePackageRules.StartHeightMetres);
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
