using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Care packages on the server (ticket #124): the crates the care package killstreak drops, the opening request
/// (<see cref="EventKind.OpenCarePackage"/>) and the perk it grants until death (<see cref="EventKind.PerkGained"/>).
/// The rules live in <see cref="CarePackageBook"/>, <see cref="CarePackage"/> and <see cref="PerkTable"/>; this file wires
/// them to the match, the physics ground and the player.
/// </summary>
public partial class NetServer
{
    CarePackageBook _carePackages = null!;
    readonly Random _carePackageRng = new();

    /// <summary>Registers the crate type and builds the book from the care package's perk weights. Runs after the match exists.</summary>
    void BuildCarePackages()
    {
        CarePackageRules.Register(WorldEntityRegistry.Shared);
        var weights = Killstreaks.Find(CarePackageRules.StreakId)?.PerkWeights ?? new Dictionary<string, int>();
        _carePackages = new CarePackageBook(_match.Entities, PerkTable.Build(weights, Perks));
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
        var result = _carePackages.TryOpen(crate, N(c.Body.GlobalPosition), _carePackageRng, out var perkId);
        if (result != OpenResult.Ok || perkId == null || Perks.Find(perkId) is not { } perk)
        {
            GD.Print($"server: {c.Name} care package {crate} not opened: {result}");
            return;
        }
        GrantPerk(c, perk);
    }

    /// <summary>
    /// The opener's perk is replaced until death. The loadout's perk comes back at the next spawn (<see cref="ApplyLoadout"/>),
    /// so nothing needs undoing here. Hot Streak also lowers the owner's streak thresholds for the rest of this life.
    /// </summary>
    void GrantPerk(Client c, PerkDef perk)
    {
        c.Perks = PerkModifiers.Of(perk);
        c.W.SetPerks(c.Perks);
        _streaks.SetCostReduction(c.Id, c.Perks.StreakCostReduction);
        Send(c, new EventMsg((byte)EventKind.PerkGained, CarePackageCodec.EncodePerk(perk.Id)));
        GD.Print($"server: {c.Name} opened a care package: {perk.Name}");
    }
}
