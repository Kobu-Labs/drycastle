using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Menu;

/// <summary>
/// The camouflage screen's "Apply sent, waiting for the server" state with a correlation and a deadline. The server answers a save with the new profile
/// (whose camo for that slot is exactly what was sent) or an error, but it answers nothing when it rate-limits the save, and a profile that was sent
/// for another reason (a loadout acknowledgement) must not count as the answer. So: only a profile carrying the sent look for the sent slot is the
/// ack, and the wait gives up after <see cref="GiveUpAfter"/> seconds so Apply, Revert and Undo never stay disabled for good.
/// </summary>
public struct CamoSaveWait
{
    /// <summary>Seconds without an answer before the screen says so and unlocks itself (a profile is a single small reliable packet; this is generous).</summary>
    public const double GiveUpAfter = 8.0;

    PendingAnswer _pending;
    int _slot;
    CamoSpec? _sent;

    public readonly bool Waiting => _pending.Active;

    public void Begin(int slot, CamoSpec sent, double now)
    {
        _slot = slot; _sent = sent;
        _pending.Begin(now);
    }

    /// <summary>A profile arrived with <paramref name="serverCopy"/> as the look of <paramref name="slot"/>. True when that is the answer to the save (the wait ends).</summary>
    public bool Acked(int slot, CamoSpec? serverCopy)
    {
        if (!_pending.Active || slot != _slot || serverCopy is null || !serverCopy.Equals(_sent)) return false;
        _pending.End();
        return true;
    }

    /// <summary>The server said no (an error), or the player left the slot: the wait ends without an answer.</summary>
    public void Cancel() => _pending.End();

    /// <summary>True once when the wait ran out: the caller reports that no answer came.</summary>
    public bool GaveUp(double now) => _pending.GiveUp(now, GiveUpAfter);
}
