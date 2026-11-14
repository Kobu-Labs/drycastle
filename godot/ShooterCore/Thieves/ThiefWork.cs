using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// What a Thief needs of the world to steal (GLOSSARY "Loot"): a target to go for, whether it is still there, the thing in its hands, the van's door to carry it to, and what happens to it when the
/// Thief is killed. One per Raid (<see cref="RaidWork"/>); a gang with none has Thieves that only stand about, as before the Loot existed.
/// </summary>
public interface IThiefWork
{
    LootConfig Config { get; }

    /// <summary>The next target of <paramref name="thief"/> (claimed for it; <paramref name="allow"/> filters targets it gave up lately), or null when nothing is free or the van takes no more Loot.</summary>
    StealTarget? Pick(GangMember thief, Func<StealTarget, bool> allow);

    /// <summary>The target as it is now, if it is still there to take.</summary>
    bool TryFind(in StealTarget target, out StealTarget now);

    /// <summary>The Thief lets the target go (it gave up, was called off or died): the claim ends.</summary>
    void Release(in StealTarget target);

    /// <summary>The Thief takes the target: it is gone from its place, the claim ends. The piece to carry, or null when it was no longer there.</summary>
    LootOrigin? Take(in StealTarget target);

    /// <summary>Where to carry Loot: the van's door, while it takes Loot. Null when it does not (shut, called the crew back, full).</summary>
    Vector3? Door { get; }

    /// <summary>The Thief tosses a piece into the van at the door. False when it cannot take it (the Thief drops it instead).</summary>
    bool Load(LootOrigin piece);

    /// <summary>A piece lies where <paramref name="at"/> is (its carrier was killed, or gave up).</summary>
    void Drop(LootOrigin piece, Vector3 at);

    /// <summary>Every tick a Thief carries a piece: where it stands and faces (a kind whose thing travels with it, a pet, moves it there).</summary>
    void Carrying(LootOrigin piece, Vector3 at, float yaw);
}

/// <summary>
/// The Thieves of one Raid at work (<see cref="IThiefWork"/>), over the event's <see cref="LootBook"/> and the Raid's van. A Thief picks targets only while the van is Opening or Parked and the Loot
/// loaded plus the Loot on its way (carried by a Thief or being stolen) is below its capacity; the Loot goes into the Raid's list (<see cref="Raid.Loaded"/>), which comes back when the van is destroyed.
/// </summary>
public sealed class RaidWork : IThiefWork
{
    readonly LootBook _book;
    readonly Raid _raid;

    public RaidWork(LootBook book, Raid raid) { _book = book; _raid = raid; }

    public LootConfig Config => _book.Config;

    /// <summary>The van takes Loot: its door is open (opening or parked).</summary>
    bool DoorOpen => _raid.Van.State is RaidVanState.Opening or RaidVanState.Parked;

    /// <summary>Loot in the van plus Loot on its way to it.</summary>
    public int Committed
    {
        get
        {
            var n = _raid.Van.Load;
            foreach (var m in _raid.Gang.Members)
                if (m.Alive && (m.HasJob || m.Carried != 0)) n++;
            return n;
        }
    }

    public StealTarget? Pick(GangMember thief, Func<StealTarget, bool> allow)
    {
        if (!DoorOpen || Committed >= _raid.Van.Capacity) return null;
        return _book.Pick(_raid.Van.Position, _book.Config.SeekRadiusM, thief.Position, allow);
    }

    public bool TryFind(in StealTarget target, out StealTarget now) => _book.TryFind(target, out now);

    public void Release(in StealTarget target) => _book.Release(target);

    public LootOrigin? Take(in StealTarget target) => _book.Take(target);

    public Vector3? Door => DoorOpen ? _raid.Van.DoorPoint : null;

    public bool Load(LootOrigin piece) => DoorOpen && _raid.LoadIn(piece);

    public void Drop(LootOrigin piece, Vector3 at) => _book.Drop(piece, at);

    public void Carrying(LootOrigin piece, Vector3 at, float yaw) => _book.Carrying(piece, at, yaw);
}
