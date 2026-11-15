using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a Thief can steal (GLOSSARY "Loot"). The value + 1 is what a Gang member's hp byte says it carries (<see cref="GangMember.Carried"/>: 0 nothing).</summary>
public enum LootKind : byte { Tire = 0, Pet = 1, Dish = 2, Connector = 3 }

public static class LootKinds
{
    public const int Count = 4;

    public static readonly IReadOnlyList<LootKind> All = new[] { LootKind.Tire, LootKind.Pet, LootKind.Dish, LootKind.Connector };

    /// <summary>The value of <see cref="GangMember.Carried"/> for a piece of this kind.</summary>
    public static byte CarriedOf(LootKind kind) => (byte)((byte)kind + 1);

    /// <summary>The kind a <see cref="GangMember.Carried"/> value says, or null for nothing (0) and values this build does not know.</summary>
    public static LootKind? OfCarried(byte carried) => carried >= 1 && carried <= Count ? (LootKind)(carried - 1) : null;
}

/// <summary>Where a piece of Loot belongs: its kind and the id its <see cref="IStealSource"/> knows it by (a tire: car id * 4 + wheel).</summary>
public readonly record struct LootOrigin(LootKind Kind, int Id);

/// <summary>
/// One thing a Thief can go and steal: where it is (<see cref="Position"/>, for choosing), where the Thief works on it (<see cref="WorkSpot"/>, on the ground), and, when the Thief must climb
/// to it, the point it works at (<see cref="Top"/>: straight up from the climb's base, which is <see cref="WorkSpot"/>). <see cref="Lying"/> is Loot that lies on the ground after its carrier
/// was killed (a <see cref="LyingLoot"/>; <see cref="Id"/> is then its world entity id and <see cref="Kind"/> what it is).
/// </summary>
public readonly record struct StealTarget(LootKind Kind, int Id, Vector3 Position, Vector3 WorkSpot, Vector3? Top = null, bool Lying = false, FenceHop? Hop = null)
{
    /// <summary>The Thief climbs from <see cref="WorkSpot"/> up to <see cref="Top"/>, works there, and climbs down (the member machine's ClimbUp, WorkAloft, ClimbDown).</summary>
    public bool NeedsClimb => Top.HasValue && !Lying;

    /// <summary>The same thing, wherever it is: the key claims are kept under.</summary>
    public (LootKind Kind, int Id, bool Lying) Key => (Kind, Id, Lying);
}

/// <summary>
/// One kind of Loot as the world has it: the things of that kind, which of them a Thief may take now, what taking one does and what putting it back does. A new kind of Loot is one class that
/// implements this (in its own file) and one line that registers it (<c>NetServer.Thieves.Loot.cs</c>, <see cref="LootRegistry.Register"/>); the Thief states, lying Loot, the van's count, the
/// return and the reset are all generic and need no change. Ids are the source's own and stable while the thing exists. Godot-free: the server's adapter gives the source the world it needs.
/// <para>
/// Claiming (one Thief per target) is the registry's, not the source's: <see cref="TryFind"/> and <see cref="FreeNear"/> only say what is free in the world.
/// </para>
/// </summary>
public interface IStealSource
{
    LootKind Kind { get; }

    /// <summary>Appends every thing of this kind that is free (not already taken) and lies within <paramref name="radius"/> metres (flat) of <paramref name="near"/>.</summary>
    void FreeNear(Vector3 near, float radius, List<StealTarget> into);

    /// <summary>Appends the ground (x, z) of every free thing of this kind: where the van may stop for it (<see cref="RaidPlanner"/>).</summary>
    void Places(List<Vector2> into);

    /// <summary>The thing with this id as it is now, if it is still free (a car that was driven off, or one that was taken, is not). The Thief asks every tick while it goes for it and works at it.</summary>
    bool TryFind(int id, out StealTarget target);

    /// <summary>The Thief took it: it is gone from its place (a tire: the wheel bit is set).</summary>
    void Take(int id);

    /// <summary>It goes back to its place, by itself or sent back by a player (a tire: the wheel bit is cleared).</summary>
    void Return(int id);

    /// <summary>A new round: everything is back where it was.</summary>
    void Reset();

    /// <summary>
    /// Whether a piece of this kind lies on the ground when its carrier is killed (a <see cref="LyingLoot"/>, which a Thief may take up and a player sends back). A kind that deals with it by itself
    /// (a pet walks home) says false: the piece then goes back at once (<see cref="Return"/>). Default true.
    /// </summary>
    bool LiesWhenDropped => true;

    /// <summary>
    /// Called every tick (server time) for each piece a Thief carries, with where the carrier stands and which way it faces, for a kind whose thing travels with the Thief in the world (a pet held under
    /// its arm). Default: nothing.
    /// </summary>
    void Carried(int id, Vector3 carrierAt, float carrierYaw) { }
}
