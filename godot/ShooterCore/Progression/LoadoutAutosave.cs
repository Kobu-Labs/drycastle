using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Progression;

/// <summary>Where a slot's autosave stands. Saved = nothing unsent or in flight; the screen words each one.</summary>
public enum AutosaveState { Saved, Pending, Saving, Retrying, Rejected }

/// <summary>
/// Autosave for the loadout editor (ticket #145), Godot-free so the timing rules are unit-tested. Every edit becomes the
/// slot's draft; a draft is sent once it has been quiet for <see cref="DebounceMs"/> (rapid edits coalesce into one send of
/// the newest value), and only one save per slot is in flight at a time. The server's ack (matched by seq) makes the draft
/// the server copy. A timed-out or failed send retries with backoff. An explicit server rejection stops the retries and keeps
/// the draft (never dropped silently); the next edit or undo sends again. Undo steps back through the slot's own edits, and
/// every undo is itself an edit, so it saves too.
/// </summary>
public sealed class LoadoutAutosave
{
    public const long DebounceMs = 300;
    public const long AckTimeoutMs = 5000;
    public const int MaxUndoSteps = 50;

    /// <summary>
    /// Real transitions, per slot (who takes them). The state is authoritative: "on the wire" and "has unsent changes" are derived from it.
    /// <list type="bullet">
    /// <item>Saved/Rejected -> Pending: Edit, Undo (a new draft; a rejection is not retried, only a newer edit sends again)</item>
    /// <item>Pending -> Saving: Poll, once due</item>
    /// <item>Saving -> Saved: Acknowledge ok, no newer edit queued (the draft becomes the server copy)</item>
    /// <item>Saving -> Pending: Acknowledge (ok or rejected) while a newer edit waited: the newer value goes next</item>
    /// <item>Saving -> Rejected: Acknowledge not ok, nothing newer (draft kept, shown, not resent)</item>
    /// <item>Saving -> Retrying: ack timeout (Poll) or Abandon (no connection); backoff in DueAt</item>
    /// <item>Retrying -> Saving: Poll, once the backoff has run out</item>
    /// </list>
    /// An edit or undo in Saving stays Saving; in Retrying it stays Retrying and keeps the backoff clock and reason: the new draft rides the next try.
    /// </summary>
    public static readonly StateMachineSpec<AutosaveState> Spec = StateMachineSpec<AutosaveState>.Define(
            "Loadout.Autosave", "One loadout slot's save: debounce, one send on the wire, ack or retry with backoff; an explicit rejection stops the retries.", AutosaveState.Saved)
        .Allow(AutosaveState.Saved, AutosaveState.Pending)
        .Allow(AutosaveState.Pending, AutosaveState.Saving)
        .Allow(AutosaveState.Saving, AutosaveState.Saved, AutosaveState.Pending, AutosaveState.Rejected, AutosaveState.Retrying)
        .Allow(AutosaveState.Retrying, AutosaveState.Saving)
        .Allow(AutosaveState.Rejected, AutosaveState.Pending)
        .Note(AutosaveState.Saved, "Nothing unsent or in flight; no draft.")
        .Note(AutosaveState.Pending, "Draft waiting for its quiet period (or for the answer to an older send) before it goes out.")
        .Note(AutosaveState.Saving, "One save for the slot is on the wire. An edit meanwhile only marks the draft newer; it goes after the answer.")
        .Note(AutosaveState.Retrying, "The send failed (timeout, no connection): resent with backoff. An edit meanwhile keeps the backoff clock and rides the next try.")
        .Note(AutosaveState.Rejected, "The server refused the draft: shown with its reason, kept, never resent until the next edit or undo.")
        .Build();

    sealed class SlotState
    {
        public Loadout? Server;       // the last copy the server confirmed (or the profile sent)
        public Loadout? Draft;        // the local value, while it is not yet confirmed by an ack
        public readonly StateMachine<AutosaveState> Fsm = new(Spec);
        public bool EditedWhileSaving; // Saving only: Draft is newer than the value on the wire
        public uint InFlightSeq;
        public Loadout? InFlightValue;
        public long SentAt, DueAt;     // absolute ms on the caller's clock (a deadline, not a countdown)
        public int Attempts;
        public string Reason = "";
        public readonly List<Loadout> Undo = new();

        public AutosaveState State => Fsm.Current;
        /// <summary>One save for this slot is on the wire.</summary>
        public bool InFlight => Fsm.Is(AutosaveState.Saving);
        /// <summary>The draft has changes that have not been sent yet.</summary>
        public bool Unsent => Fsm.Current is AutosaveState.Pending or AutosaveState.Retrying || (InFlight && EditedWhileSaving);
    }

    readonly SlotState[] _slots = Enumerable.Range(0, Loadout.SlotCount).Select(_ => new SlotState()).ToArray();
    uint _seq;

    /// <summary>The value the editor shows: the local draft if there is one, else the server copy (null before the profile arrives).</summary>
    public Loadout? Effective(int slot) => Slot(slot) is { } s ? s.Draft ?? s.Server : null;

    public AutosaveState StateOf(int slot) => Slot(slot)?.State ?? AutosaveState.Saved;

    /// <summary>The server's reason for the last rejection of this slot, or "" when the last save was accepted.</summary>
    public string RejectReason(int slot) => Slot(slot)?.Reason ?? "";

    /// <summary>True while the slot has a draft the server has not confirmed (unsent, in flight, retrying or rejected).</summary>
    public bool HasUnconfirmed(int slot) => Slot(slot) is { } s && s.Draft != null;

    public bool CanUndo(int slot) => Slot(slot) is { } s && s.Undo.Count > 0;

    /// <summary>
    /// The server's copy of a slot (from the profile). It never overwrites a draft the player is still editing: the draft
    /// stays on screen until the server confirms it.
    /// </summary>
    public void Seed(Loadout server)
    {
        if (Slot(server.Slot) is { } s) s.Server = server;
    }

    /// <summary>A change to a slot. Coalesces with earlier edits still waiting for the debounce. Starts the quiet period.</summary>
    public void Edit(Loadout next, long nowMs)
    {
        if (Slot(next.Slot) is not { } s) return;
        var before = Effective(next.Slot);
        if (before == next) return;
        if (before != null)
        {
            s.Undo.Add(before);
            if (s.Undo.Count > MaxUndoSteps) s.Undo.RemoveAt(0);
        }
        Dirty(s, next, nowMs + DebounceMs);
    }

    /// <summary>Steps the slot back to its previous value (an edit, so it saves). False when there is nothing to undo.</summary>
    public bool Undo(int slot, long nowMs)
    {
        if (Slot(slot) is not { } s || s.Undo.Count == 0) return false;
        var previous = s.Undo[^1];
        s.Undo.RemoveAt(s.Undo.Count - 1);
        Dirty(s, previous, nowMs);
        return true;
    }

    /// <summary>
    /// A new draft (edit or undo), due at <paramref name="dueAt"/>. Saved/Pending/Rejected: Pending. Saving: stays Saving, the draft is marked newer than
    /// the wire. Retrying: stays Retrying with its backoff clock and reason, so a typing user cannot cut the backoff down to the debounce and the screen
    /// keeps saying "retrying"; the new draft goes with the next try.
    /// </summary>
    static void Dirty(SlotState s, Loadout draft, long dueAt)
    {
        s.Draft = draft;
        switch (s.State)
        {
            case AutosaveState.Retrying:
                return;
            case AutosaveState.Saving:
                s.EditedWhileSaving = true;
                break;
            default:
                s.Fsm.Enter(AutosaveState.Pending);
                break;
        }
        s.DueAt = dueAt;
        s.Reason = "";
    }

    /// <summary>
    /// The saves due now, each with the seq to send it under. Marks them in flight. Also times out saves that got no ack
    /// (they retry with backoff, the same value again).
    /// </summary>
    public List<(int Slot, uint Seq, Loadout Value)> Poll(long nowMs)
    {
        var due = new List<(int, uint, Loadout)>();
        for (var i = 0; i < _slots.Length; i++)
        {
            var s = _slots[i];
            if (s.InFlight && nowMs - s.SentAt >= AckTimeoutMs) Retry(s, nowMs, "no answer from the server");
            if (s.InFlight || !s.Unsent || s.Draft is not { } value || nowMs < s.DueAt) continue;
            var seq = ++_seq;
            s.Fsm.Enter(AutosaveState.Saving);
            s.EditedWhileSaving = false;
            s.InFlightSeq = seq;
            s.InFlightValue = value;
            s.SentAt = nowMs;
            due.Add((i, seq, value));
        }
        return due;
    }

    /// <summary>Makes every unsent draft due now (leaving the editor, switching slots). Saves still go out through <see cref="Poll"/>. Also ends a retry's backoff wait: leaving is worth one more try.</summary>
    public void Flush(long nowMs)
    {
        foreach (var s in _slots)
            if (s.Unsent) s.DueAt = nowMs;
    }

    /// <summary>The send could not go out (no connection): the save goes back in the queue and retries with backoff.</summary>
    public void Abandon(int slot, long nowMs)
    {
        if (Slot(slot) is { } s && s.InFlight) Retry(s, nowMs, "not connected");
    }

    /// <summary>The server answered a save. Answers for an older seq (already timed out) are ignored.</summary>
    public void Acknowledge(LoadoutAckInfo ack)
    {
        if (Slot(ack.Slot) is not { } s || !s.InFlight || s.InFlightSeq != ack.Seq) return;
        var newer = s.EditedWhileSaving;
        s.EditedWhileSaving = false;
        s.Attempts = 0;
        if (ack.Ok)
        {
            s.Server = s.InFlightValue;
            s.Reason = "";
            if (newer) s.Fsm.Enter(AutosaveState.Pending); // edited while it was on the wire: the newer value goes next
            else { s.Draft = null; s.Fsm.Enter(AutosaveState.Saved); }
        }
        else
        {
            // a validation answer would repeat on a retry, so it is shown instead of retried; the draft stays
            s.Reason = ack.Reason;
            s.Fsm.Enter(newer ? AutosaveState.Pending : AutosaveState.Rejected);
        }
        s.InFlightValue = null;
    }

    static void Retry(SlotState s, long nowMs, string reason)
    {
        s.InFlightValue = null;
        s.EditedWhileSaving = false; // Retrying is unsent by definition: the draft (same or newer) is sent again
        s.Attempts++;
        s.DueAt = nowMs + Backoff(s.Attempts);
        s.Fsm.Enter(AutosaveState.Retrying);
        s.Reason = reason;
    }

    /// <summary>1 s, 2 s, 4 s, then 8 s between tries.</summary>
    public static long Backoff(int attempt) => Math.Min(8000, 1000L << Math.Clamp(attempt - 1, 0, 3));

    SlotState? Slot(int slot) => slot >= 0 && slot < _slots.Length ? _slots[slot] : null;
}
