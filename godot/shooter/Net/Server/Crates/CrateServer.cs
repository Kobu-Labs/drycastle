using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.CarePackages;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Crates;

/// <summary>
/// Care packages on the server (ticket #124, reward reworked in #151): the crates the care package killstreak drops and the
/// opening request (<see cref="EventKind.OpenCarePackage"/>). Opening grants a random killstreak from killstreaks.json
/// (<see cref="StreakRewards"/>) into the opener's reward slots (<see cref="KillstreakService.Grant"/>), which tells the
/// owner through <see cref="EventKind.StreakGranted"/>. The rules live in <see cref="CarePackageBook"/>; this class wires
/// them to the match, the physics ground and the streak service.
/// </summary>
internal sealed class CrateServer
{
    readonly ServerContext _ctx;
    readonly Func<WorldQuery> _query;
    readonly Func<KillstreakService> _streaks;
    readonly CarePackageBook _book;
    readonly Random _rng = new();

    /// <summary>Registers the crate type and builds the book from the killstreak reward table. Built once, after the match exists.</summary>
    /// <param name="streaks">The streak service, which is made after this (the care package streak needs this class).</param>
    public CrateServer(ServerContext ctx, StreakRewards rewards, Func<WorldQuery> query, Func<KillstreakService> streaks)
    {
        _ctx = ctx; _query = query; _streaks = streaks;
        CarePackageRules.Register(WorldEntityRegistry.Shared);
        _book = new CarePackageBook(ctx.Match.Entities, rewards);
    }

    /// <summary>The care package streak, for <c>BuildStreaks</c>.</summary>
    public IKillstreak Streak() => new CarePackageStreak(_book, ThrowCanister, GroundBelow);

    /// <summary>Whether a crate lies within reach of the interact key at <paramref name="at"/>.</summary>
    public bool AnyInReach(NVec3 at) => _book.AnyInReach(at);

    /// <summary>Where the owner's thrown canister lands (Q): from the eye along the look, stopped by the first surface.</summary>
    CarePackageRules.ThrowFlight ThrowCanister(ushort owner, NVec3 feet)
    {
        if (!_ctx.Players.TryGetValue(owner, out var c)) return new CarePackageRules.ThrowFlight(feet, NVec3.Zero, 0f, CarePackageRules.DropPoint(feet, 0f));
        var eye = new NVec3(feet.X, feet.Y + MovementStep.EyeHeight(c.Body.State.Stance), feet.Z);
        return CarePackageRules.Throw(eye, c.Body.Yaw, c.Body.Pitch, (from, dir, len) =>
            _query().Shot(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len, null).Distance);
    }

    /// <summary>Height of the ground under a point (a vertical ray, the Drop row), or null when nothing is below it.</summary>
    public float? GroundBelow(NVec3 p) =>
        _query().GroundY(MoverKind.Drop, new Vector3(p.X, p.Y, p.Z), CarePackageRules.StartHeightMetres, CarePackageRules.StartHeightMetres);

    /// <summary>A client asked to open a crate (after holding the key). The server checks the player, the crate and the reach.</summary>
    public void OnOpen(ServerClient c, byte[] payload)
    {
        if (!CarePackageCodec.TryDecodeOpen(payload, out var crate)) return;
        if (!c.Joined || c.W == null || c.Car != null || !_ctx.IsAlive(c.Id)) return;
        var result = _book.TryOpen(crate, c.Body.GlobalPosition.ToNumerics(), _rng, out var streakId);
        if (result != OpenResult.Ok || streakId == null)
        {
            GD.Print($"server: {c.Name} care package {crate} not opened: {result}");
            return;
        }
        // the reward does not touch the Streak counter; the service sends StreakGranted to the opener
        var grant = _streaks().Grant(c.Id, streakId);
        GD.Print($"server: {c.Name} opened a care package: {streakId}" + (grant?.Replaced is { } old ? $" (replaced {old})" : ""));
    }
}
