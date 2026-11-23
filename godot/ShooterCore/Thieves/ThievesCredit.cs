using Drycastle.ShooterCore.Match;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// Who took part in the Thieves event (GLOSSARY "Thieves event"; docs/events/thieves/10), pure. A player is credited for landing a hit on a Gang member
/// (<see cref="CreditedGangHits"/> does that), for a hit on a Raid van (<see cref="NoteVanHit"/>, ticket 03) and for sending Loot back to its place
/// (<see cref="NoteLootReturned"/>, ticket 04). When the event is won each credited player gets the event-win XP; with nobody credited nobody gets any.
/// A cancelled event or a round reset pays nothing. Once per player however many things they did.
/// </summary>
public sealed class ThievesCredit
{
    readonly List<ushort> _credited = new();

    /// <summary>The credited players, in the order they first took part.</summary>
    public IReadOnlyList<ushort> Credited => _credited;

    /// <summary>A player landed a hit on a Gang member that counted (not one the member ignored).</summary>
    public void NoteGangHit(ushort player) => Add(player);

    /// <summary>A player landed a hit on a Raid van that counted (<see cref="CreditedVanHits"/> calls it).</summary>
    public void NoteVanHit(ushort player) => Add(player);

    /// <summary>A player sent a piece of Loot back to its place: called when the hold-to-send-back completes (<c>Thieves/ThievesServer.cs</c>), while the event runs.</summary>
    public void NoteLootReturned(ushort player) => Add(player);

    void Add(ushort player)
    {
        if (player != 0 && !_credited.Contains(player)) _credited.Add(player);
    }

    /// <summary>Nobody is credited any more (the event ended, or a round began).</summary>
    public void Clear() => _credited.Clear();

    /// <summary>
    /// Closes the event: the players to pay the win's XP, then forgets the list. Not won (cancelled, reset) pays nobody. <paramref name="eligible"/> drops those who
    /// have left the match (or earn nothing, like a sandbox player).
    /// </summary>
    public List<ushort> Settle(bool won, Func<ushort, bool> eligible)
    {
        var paid = won ? _credited.Where(eligible).ToList() : new List<ushort>();
        _credited.Clear();
        return paid;
    }
}

/// <summary>The match's Gang member hits with a note to <see cref="ThievesCredit"/> on every hit that counted. It wraps the roster, so every path that reaches a member (a bullet, a blast, a vehicle) credits.</summary>
public sealed class CreditedGangHits : IGangHits
{
    readonly IGangHits _inner;
    readonly ThievesCredit _credit;

    public CreditedGangHits(IGangHits inner, ThievesCredit credit) { _inner = inner; _credit = credit; }

    public DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        var result = _inner.Hit(hitId, attacker, weapon, amount, cause);
        if (result != DamageResult.Ignored) _credit.NoteGangHit(attacker);
        return result;
    }
}

/// <summary>The match's Raid van hits with a note to <see cref="ThievesCredit"/> on every hit that counted (not one the van ignored: weight 0, a wreck, a blast it already took this tick). It wraps the roster of vans, so every path that reaches a van credits.</summary>
public sealed class CreditedVanHits : IGangHits
{
    readonly IGangHits _inner;
    readonly ThievesCredit _credit;

    public CreditedVanHits(IGangHits inner, ThievesCredit credit) { _inner = inner; _credit = credit; }

    public DamageResult Hit(ushort hitId, ushort attacker, byte weapon, float amount, KillCause cause)
    {
        var result = _inner.Hit(hitId, attacker, weapon, amount, cause);
        if (result != DamageResult.Ignored) _credit.NoteVanHit(attacker);
        return result;
    }
}

/// <summary>What the Thieves event leaves behind that needs no world (docs/events/thieves/10).</summary>
public static class ThievesReward
{
    /// <summary>The owner id of the corpse a killed Gang boss leaves (a player id is never this; the Bigfoot's is 60000): the client draws it as a body that carries only its Item.</summary>
    public const ushort ChainCorpseOwner = 60001;

    /// <summary>
    /// Whether a member killed by a hit leaves the Gold chain on its body: only a Gang boss. A boss who boards the van and leaves, or flees off the map, is
    /// never killed (<see cref="ThiefGang.Killed"/> fires for kills only), so he keeps his.
    /// </summary>
    public static bool LeavesChain(GangMemberKind kind) => kind == GangMemberKind.GangBoss;
}
