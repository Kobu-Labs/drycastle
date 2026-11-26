namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// Live state of one house: the grid and which of its solid cells are removed this round. Carve, hash and the wire runs are WP-A's;
/// the removed bitmask, <see cref="Solid"/> and the internal <see cref="Remove"/> are real so WP-B's tests can shape a house without a carve.
/// </summary>
public sealed class HouseCells
{
    readonly ulong[] _removed;
    int _removedCount;

    public HouseCells(HouseGrid grid)
    {
        Grid = grid;
        _removed = new ulong[(grid.Count + 63) >> 6];
    }

    public HouseGrid Grid { get; }
    /// <summary>Ops applied this round.</summary>
    public ushort OpSeq { get; private set; }
    /// <summary>Original solid and not removed.</summary>
    public bool Solid(int i) => Grid.Cells.Span[i] != 0 && (_removed[i >> 6] & (1UL << (i & 63))) == 0;
    /// <summary>Whether any cell is removed.</summary>
    public bool Damaged => _removedCount > 0;

    /// <summary>Carve, then <see cref="Support.Settle"/>, OpSeq++. WP-A.</summary>
    public CarveResult Apply(CarveOp op, DestructionConfig cfg) => throw new NotImplementedException("WP-A: HouseCells.Apply");
    /// <summary>FNV-1a 32 over the removed bitmask. WP-A.</summary>
    public uint Hash() => throw new NotImplementedException("WP-A: HouseCells.Hash");

    /// <summary>Every cell whole again, OpSeq 0 (round reset).</summary>
    public void Restore()
    {
        Array.Clear(_removed);
        _removedCount = 0;
        OpSeq = 0;
    }

    /// <summary>Varint runs (kept, removed, ...) over the original solid cells in index order. WP-A.</summary>
    public byte[] EncodeRemoved() => throw new NotImplementedException("WP-A: HouseCells.EncodeRemoved");
    public void DecodeRemoved(ReadOnlySpan<byte> runs, ushort opSeq) => throw new NotImplementedException("WP-A: HouseCells.DecodeRemoved");

    /// <summary>Marks solid cell <paramref name="i"/> removed (no support pass, no OpSeq change). Air or already removed: nothing.</summary>
    internal void Remove(int i)
    {
        if (!Solid(i)) return;
        _removed[i >> 6] |= 1UL << (i & 63);
        _removedCount++;
    }

    /// <summary>Whether cell <paramref name="i"/> was solid and is removed.</summary>
    internal bool Removed(int i) => (_removed[i >> 6] & (1UL << (i & 63))) != 0;

    /// <summary>The removed bitmask, one bit per cell index (low bit first). Read-only view for the hash and the wire.</summary>
    internal ReadOnlySpan<ulong> RemovedBits => _removed;

    /// <summary>Sets OpSeq (the wire's decode and Apply).</summary>
    internal void SetOpSeq(ushort seq) => OpSeq = seq;
}
