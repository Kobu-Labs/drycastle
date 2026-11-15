namespace Drycastle.ShooterCore;

/// <summary>
/// A status a player carries: not an Item (no slot, no drop, no corpse). Append only, the value is a bit of <see cref="PlayerStatuses.Mask"/>.
/// </summary>
public enum StatusKind : byte
{
    /// <summary>The devil's curse (killing the demon): the player's hits set the victim ablaze (<see cref="CurseRules"/>). Until the player dies.</summary>
    Curse = 0,
    /// <summary>Divine blessing: one lethal hit is negated. The save is <see cref="BlessingRules"/>, hooked into <see cref="MatchSim.SaveFromDeath"/>. Until used up or the player dies.</summary>
    Blessing = 1,
}

/// <summary>
/// The statuses of one player. Session state of the server next to <see cref="Equipment"/>, never in the profile (ADR 0016's reasoning: an
/// event reward must not outlive its event). A new round keeps them (nothing to call), <see cref="OnDeath"/> clears them, leaving the match
/// drops the object with the client.
/// </summary>
public sealed class PlayerStatuses
{
    byte _mask;

    /// <summary>Set whenever the set changed; the host clears it after telling the player (and the others, if it is drawn).</summary>
    public bool Changed { get; set; }

    /// <summary>One bit per <see cref="StatusKind"/>: what a message would carry.</summary>
    public byte Mask => _mask;

    public bool Has(StatusKind kind) => (_mask & Bit(kind)) != 0;

    public bool Any => _mask != 0;

    /// <summary>Gives the status; false when the player had it already.</summary>
    public bool Grant(StatusKind kind)
    {
        if (Has(kind)) return false;
        _mask |= Bit(kind);
        Changed = true;
        return true;
    }

    /// <summary>Takes the status away (a used-up blessing); false when the player did not have it.</summary>
    public bool Remove(StatusKind kind)
    {
        if (!Has(kind)) return false;
        _mask &= (byte)~Bit(kind);
        Changed = true;
        return true;
    }

    /// <summary>The player died: every status ends.</summary>
    public void OnDeath()
    {
        if (_mask == 0) return;
        _mask = 0;
        Changed = true;
    }

    /// <summary>A client's copy, from a message.</summary>
    public void SetMask(byte mask)
    {
        if (mask == _mask) return;
        _mask = mask;
        Changed = true;
    }

    static byte Bit(StatusKind kind) => (byte)(1 << (int)kind);
}
