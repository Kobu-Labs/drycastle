using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where a player stands with the Cartel (machine <c>Cartel.Membership</c>). Stored on the Profile as its byte (ADR 0045): do not renumber.</summary>
public enum CartelMembership : byte
{
    Outsider = 0,
    Member = 1,
    Quit = 2,
}

/// <summary>A player's Cartel side as the store keeps it on the Profile: membership and Respect (the rank follows from the Respect, <see cref="CartelFile.RankFor"/>).</summary>
public readonly record struct CartelRecord(CartelMembership Membership, int Respect)
{
    public static readonly CartelRecord None = new(CartelMembership.Outsider, 0);
}

/// <summary>What adding Respect did to the rank: <see cref="Before"/> and <see cref="After"/> are rank indices of cartel.json; a rank-up when After is higher.</summary>
public readonly record struct CartelRankChange(int Added, int Before, int After)
{
    public bool RankedUp => After > Before;
}

/// <summary>
/// One player's standing with the Cartel (GLOSSARY "Cartel", "Cartel rank", "Respect"; docs/cartel.md "Membership and rank"): an outsider until they join at the Contact, then a
/// member who earns Respect, or quit with rank and Respect frozen until they rejoin. Loaded from the Profile and written back by the server; this keeps only the rules.
/// </summary>
public sealed class CartelStanding
{
    public static readonly StateMachineSpec<CartelMembership> MembershipSpec = StateMachineSpec<CartelMembership>
        .Define("Cartel.Membership", "Whether a player works for the Cartel: an outsider until they join at the Contact, a member who runs Jobs and earns Respect, or quit with rank and Respect frozen until they rejoin.", CartelMembership.Outsider)
        .Allow(CartelMembership.Outsider, CartelMembership.Member)
        .Allow(CartelMembership.Member, CartelMembership.Quit)
        .Allow(CartelMembership.Quit, CartelMembership.Member)
        .Note(CartelMembership.Outsider, "never joined: no rank, 0 Respect")
        .Note(CartelMembership.Member, "starts Jobs (as the leader of their party) and earns Respect")
        .Note(CartelMembership.Quit, "left the Cartel: rank and Respect kept frozen; a rejoin carries on from them")
        .Build();

    readonly StateMachine<CartelMembership> _membership = new(MembershipSpec);

    public CartelMembership Membership => _membership.Current;
    public bool IsMember => _membership.Is(CartelMembership.Member);
    /// <summary>All the Respect earned, never less than 0.</summary>
    public int Respect { get; private set; }
    public CartelRecord Record => new(Membership, Respect);

    /// <summary>A standing as the store keeps it. An unknown stored membership byte is an outsider (the Respect is kept).</summary>
    public static CartelStanding From(CartelRecord r)
    {
        var s = new CartelStanding { Respect = Math.Max(0, r.Respect) };
        s._membership.Reset(Enum.IsDefined(r.Membership) ? r.Membership : CartelMembership.Outsider);
        return s;
    }

    public int Rank(CartelFile data) => data.RankFor(Respect);

    /// <summary>Joins (or rejoins) the Cartel. False when already a member.</summary>
    public bool Join() => !IsMember && _membership.Enter(CartelMembership.Member);

    /// <summary>Quits the Cartel; rank and Respect stay. False when not a member.</summary>
    public bool Quit() => IsMember && _membership.Enter(CartelMembership.Quit);

    /// <summary>Adds Respect to a member (a Job's reward, an admin's grant). Nothing for a player who is not a member, or for an amount of 0 or less. Stops at int.MaxValue.</summary>
    public CartelRankChange AddRespect(int amount, CartelFile data)
    {
        var before = Rank(data);
        if (!IsMember || amount <= 0) return new CartelRankChange(0, before, before);
        var added = (int)Math.Min(amount, (long)int.MaxValue - Respect);
        Respect += added;
        return new CartelRankChange(added, before, Rank(data));
    }
}
