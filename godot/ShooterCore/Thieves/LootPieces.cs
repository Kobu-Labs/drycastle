namespace Drycastle.ShooterCore;

/// <summary>Where one piece of Loot is in its life (GLOSSARY "Loot"): at its place, claimed by a Thief, carried, lying on the ground, claimed there, or loaded in a van.</summary>
public enum LootPieceState : byte { AtPlace, Claimed, Carried, Lying, LyingClaimed, Loaded }

/// <summary>
/// The life of every piece of Loot, as <see cref="LootBook"/> keeps it (<see cref="LootBook.Pieces"/>): one <see cref="StateMachine{TState}"/> per piece that is away from its place, found by its
/// <see cref="LootOrigin"/>; a piece at its place has none. The book moves a piece through the claim, the take, the drop, the return and the van's load (the Raid reports the load, the Thief the loss of a
/// pet in its hands), so the claim dictionaries of the <see cref="LootRegistry"/>, <see cref="GangMember.Carried"/>, <see cref="LyingLoot"/> and <see cref="Raid.Loaded"/> are four views of one lifecycle that
/// is checked here. Strict in tests; in production an undeclared edge is logged and taken (<see cref="StateMachineDiagnostics"/>).
/// </summary>
public sealed class LootPieces
{
    public static readonly StateMachineSpec<LootPieceState> Spec = StateMachineSpec<LootPieceState>
        .Define("Loot.Piece", "Where one piece of Loot is: at its place, claimed by a Thief, carried, lying on the ground (claimed or not) or loaded in a van.", LootPieceState.AtPlace)
        .Allow(LootPieceState.AtPlace, LootPieceState.Claimed, LootPieceState.Lying)
        .Allow(LootPieceState.Claimed, LootPieceState.AtPlace, LootPieceState.Carried)
        .Allow(LootPieceState.Carried, LootPieceState.Lying, LootPieceState.Loaded, LootPieceState.AtPlace)
        .Allow(LootPieceState.Lying, LootPieceState.LyingClaimed, LootPieceState.AtPlace)
        .Allow(LootPieceState.LyingClaimed, LootPieceState.Lying, LootPieceState.Carried, LootPieceState.AtPlace)
        .Allow(LootPieceState.Loaded, LootPieceState.AtPlace)
        .Note(LootPieceState.AtPlace, "In the world where it belongs (a wheel on its car, a dish on its wall, a pet in its garden) and free to be chosen; also where a piece that is gone for good (a pet killed in a Thief's hands) ends. Not tracked: no machine exists for it. A kind that grows back (a pole after its cooldown) offers a new piece under the old id while the old one is still away: its claim starts the new life here and the old one's is forgotten.")
        .Note(LootPieceState.Claimed, "One Thief goes for it or works at it (the registry's claim): no other chooses it. Ends when the Thief takes it, gives up, is called off or dies, or the thing goes (a car driven off).")
        .Note(LootPieceState.Carried, "In a Thief's hands (hp byte of the member), gone from its place: hauled to the van's door. Ends in the van, on the ground (killed, hit, gave up), or back at its place (a kind that does not lie, a van that cannot take it). A Thief that flees with it runs off with it: it stays Carried until the round resets.")
        .Note(LootPieceState.Lying, "On the ground as a world entity (capped): a Thief may take it up, a player sends it back. Also entered by a piece put down with no carrier (tools, tests): AtPlace goes straight to Lying.")
        .Note(LootPieceState.LyingClaimed, "A Thief goes to take up the Loot that lies. The Loot may be sent back or capped away meanwhile: the claim then lapses with it.")
        .Note(LootPieceState.Loaded, "Counted in a van (Raid.Loaded). Comes back to its place when the van is destroyed; stays with a van that drives off.")
        .Build();

    readonly Dictionary<LootOrigin, StateMachine<LootPieceState>> _by = new();

    /// <summary>Where the piece is (<see cref="LootPieceState.AtPlace"/> for one the book has not moved).</summary>
    public LootPieceState Of(LootOrigin piece) => _by.TryGetValue(piece, out var m) ? m.Current : LootPieceState.AtPlace;

    /// <summary>Pieces away from their place now.</summary>
    public int Away => _by.Count;

    /// <summary>Moves the piece to <paramref name="to"/>. A piece back at its place is no longer tracked.</summary>
    internal void Enter(LootOrigin piece, LootPieceState to)
    {
        if (!_by.TryGetValue(piece, out var m))
        {
            if (to == LootPieceState.AtPlace) return;
            _by[piece] = m = new StateMachine<LootPieceState>(Spec);
        }
        m.Enter(to);
        if (to == LootPieceState.AtPlace) _by.Remove(piece);
    }

    /// <summary>Forgets the piece without a transition (the world cleared its entity; a reset is coming or has come).</summary>
    internal void Forget(LootOrigin piece) => _by.Remove(piece);

    /// <summary>A new round: everything is at its place.</summary>
    internal void Clear() => _by.Clear();
}
