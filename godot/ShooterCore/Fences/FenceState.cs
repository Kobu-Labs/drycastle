namespace Drycastle.ShooterCore.Fences;

/// <summary>
/// Which pieces of the thin sections are broken (a 64-bit mask a section, bit j = piece j), and on the server their hit points (the kind's piece_hp until damaged).
/// A wall section never has pieces. Clients only set masks (<see cref="SetBroken"/>).
/// </summary>
public sealed class FenceState
{
    readonly IReadOnlyList<FenceSectionInfo> _sections;
    readonly int[] _pieceHp;          // per section, the kind's piece_hp; 0 for a wall
    readonly ulong[] _broken;
    readonly int[]?[] _hp;            // per section, allocated at its first damage
    int _brokenSections;

    public FenceState(IReadOnlyList<FenceSectionInfo> sections, FenceDestructionConfig cfg)
    {
        _sections = sections;
        _pieceHp = new int[sections.Count];
        for (var i = 0; i < sections.Count; i++)
            if (sections[i].Thin) _pieceHp[i] = cfg.Of(sections[i].Kind).PieceHp;
        _broken = new ulong[sections.Count];
        _hp = new int[]?[sections.Count];
    }

    /// <summary>Bit j: piece j of <paramref name="section"/> is broken.</summary>
    public ulong Broken(int section) => (uint)section < (uint)_broken.Length ? _broken[section] : 0;

    /// <summary>Any piece of any section is broken.</summary>
    public bool Damaged => _brokenSections > 0;

    /// <summary>The piece's hit points: its kind's piece_hp until damaged, 0 when broken (or no such piece).</summary>
    public int Hp(int section, int piece)
    {
        if (!Valid(section, piece)) return 0;
        if ((_broken[section] >> piece & 1) != 0) return 0;
        return _hp[section]?[piece] ?? _pieceHp[section];
    }

    /// <summary>Takes <paramref name="amount"/> off a standing piece; true when this broke it. A broken or missing piece, or no damage, changes nothing.</summary>
    public bool Damage(int section, int piece, int amount)
    {
        if (amount <= 0 || !Valid(section, piece) || (_broken[section] >> piece & 1) != 0) return false;
        var hp = _hp[section] ??= Full(section);
        hp[piece] -= amount;
        if (hp[piece] > 0) return false;
        hp[piece] = 0;
        Set(section, _broken[section] | 1UL << piece);
        return true;
    }

    /// <summary>Exactly this mask (the client's catch-up and breaks); bits of pieces the section does not have are dropped.</summary>
    public void SetBroken(int section, ulong mask)
    {
        if ((uint)section >= (uint)_broken.Length) return;
        var n = _sections[section].Pieces;
        mask &= n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
        var healed = _broken[section] & ~mask;
        if (healed != 0 && _hp[section] is { } hp)
            for (var j = 0; j < hp.Length; j++)
                if ((healed >> j & 1) != 0) hp[j] = _pieceHp[section];
        Set(section, mask);
    }

    /// <summary>Every piece whole, at full hit points.</summary>
    public void Restore()
    {
        Array.Clear(_broken);
        Array.Clear(_hp);
        _brokenSections = 0;
    }

    /// <summary>The sections with a broken piece, ascending.</summary>
    public IEnumerable<int> BrokenSections()
    {
        for (var i = 0; i < _broken.Length; i++)
            if (_broken[i] != 0) yield return i;
    }

    bool Valid(int section, int piece) => (uint)section < (uint)_broken.Length && (uint)piece < (uint)_sections[section].Pieces;

    int[] Full(int section)
    {
        var hp = new int[_sections[section].Pieces];
        Array.Fill(hp, _pieceHp[section]);
        return hp;
    }

    void Set(int section, ulong mask)
    {
        var was = _broken[section] != 0;
        _broken[section] = mask;
        _brokenSections += (mask != 0 ? 1 : 0) - (was ? 1 : 0);
    }
}
