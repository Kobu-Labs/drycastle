namespace Drycastle.ShooterCore;

/// <summary>Who holds a church seat on the crowd's side of the books.</summary>
public enum SeatHolder : byte
{
    /// <summary>Nobody: a citizen may be sent to it and a player may sit in it.</summary>
    Free = 0,
    /// <summary>A citizen waits to leave for it, walks to it, sits in it, or lies dead at it until the body is removed.</summary>
    Citizen = 1,
    /// <summary>Kept for players, or a player sits in it: no citizen is sent to it.</summary>
    Reserved = 2,
}

/// <summary>
/// Which seats a citizen has and which are kept for players: the one place the crowd's seat bookkeeping lives, so the two sets cannot drift apart.
/// A seat is held by at most one side: <see cref="Reserve"/> refuses a citizen's seat and <see cref="Assign"/> is only called for a free one.
/// (The player-to-seat map and the config's kept seats stay in <see cref="HolyMassEvent"/>; a seat a player sits in is <see cref="SeatHolder.Reserved"/> here.)
/// </summary>
public sealed class SeatBook
{
    readonly HashSet<int> _citizen = new();
    readonly HashSet<int> _reserved = new();

    public SeatHolder HolderOf(int seatId) => _citizen.Contains(seatId) ? SeatHolder.Citizen : _reserved.Contains(seatId) ? SeatHolder.Reserved : SeatHolder.Free;

    public bool IsFree(int seatId) => HolderOf(seatId) == SeatHolder.Free;

    /// <summary>A citizen is sent to the seat (waiting to leave counts).</summary>
    public void Assign(int seatId) => _citizen.Add(seatId);

    /// <summary>The citizen is gone (removed, or never left): the seat is free again unless it is reserved.</summary>
    public void Release(int seatId) => _citizen.Remove(seatId);

    /// <summary>Keeps the seat for players. False when a citizen holds it (or it is reserved already).</summary>
    public bool Reserve(int seatId) => !_citizen.Contains(seatId) && _reserved.Add(seatId);

    public void Unreserve(int seatId) => _reserved.Remove(seatId);

    /// <summary>Everything is free again (the event ended).</summary>
    public void Clear() { _citizen.Clear(); _reserved.Clear(); }
}
