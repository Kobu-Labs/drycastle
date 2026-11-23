using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Demon;

/// <summary>Where a client is in the demon's pounce as it sees it (the view of the server's marked / pinned / released). None: no one is marked or held.</summary>
public enum PounceViewStage : byte { None, Wind, Pinned, Rising }

/// <summary>What the client must do besides changing stage when a pounce message arrives.</summary>
public enum PounceViewEffect : byte
{
    None,
    /// <summary>The local player was just pinned: the camera starts to drop (jolt, flash).</summary>
    PinnedMine,
    /// <summary>The local player's pin ended by the demon's release: the camera rises, the red fades.</summary>
    RiseAfterRelease,
    /// <summary>The local player's pin ended without a release (they left, the demon died): the camera rises, no fade.</summary>
    RiseAfterAbort,
    /// <summary>The local player was pinned and a message for another pounce arrived first (the server never does this; defensive): the view ends at once, no rise.</summary>
    PinSuperseded,
}

/// <summary>
/// The client's view of the demon's pounce (<see cref="EventKind.DemonPounce"/>) as an explicit machine: the stage, who is marked or pinned, the yaw they lie at, and
/// who lay down last (for the others' bodies). Godot-free; NetClient.Pounce keeps the clocks, the camera and the screen and asks this what happened. Replaces a
/// nullable stage, two booleans and a victim id that were kept in step by hand.
/// <para>
/// The server's story for one victim is <c>Wind, Pinned, (Released | Aborted)</c>, or <c>Wind, Aborted</c>; a client that joins mid-pounce is told only the current stage.
/// The victim is the local player (<c>me</c>) or someone else; only a pin of the local player has a <see cref="PounceViewStage.Rising"/> tail.
/// </para>
/// </summary>
public sealed class PounceView
{
    public static readonly StateMachineSpec<PounceViewStage> Spec = StateMachineSpec<PounceViewStage>.Define(
            "Pounce.View", "The client's view of the demon's pounce: a victim is marked (Wind), pinned flat, then let go; the local victim's camera rises back afterwards.", PounceViewStage.None)
        .Allow(PounceViewStage.None, PounceViewStage.Wind, PounceViewStage.Pinned)
        .Allow(PounceViewStage.Wind, PounceViewStage.Pinned, PounceViewStage.None)
        .Allow(PounceViewStage.Pinned, PounceViewStage.Rising, PounceViewStage.None, PounceViewStage.Wind)
        .Allow(PounceViewStage.Rising, PounceViewStage.None, PounceViewStage.Wind, PounceViewStage.Pinned)
        .Note(PounceViewStage.None, "Nobody is marked or held (also after the demon is gone).")
        .Note(PounceViewStage.Wind, "A victim is marked: the local victim's look is locked on the demon. A joiner can start here.")
        .Note(PounceViewStage.Pinned, "The victim lies pinned. Locally: the body is the server's (no prediction, no weapon), the camera is on the floor. A joiner can start here.")
        .Note(PounceViewStage.Rising, "The local victim was let go: the camera rises back to standing height while the red fades. Only after a pin of the local player.")
        .Build();

    readonly StateMachine<PounceViewStage> _m = new(Spec);

    public PounceViewStage Stage => _m.Current;
    /// <summary>Someone is marked or pinned (the stage the old nullable <c>_pStage</c> held).</summary>
    public bool Marked => _m.IsAny(PounceViewStage.Wind, PounceViewStage.Pinned);
    public bool Pinned => _m.Is(PounceViewStage.Pinned);
    public bool Rising => _m.Is(PounceViewStage.Rising);
    /// <summary>The marked or pinned player; the last one after the pounce ended.</summary>
    public ushort Victim { get; private set; }
    /// <summary>The yaw the pinned victim lies at (head towards (sin yaw, cos yaw)).</summary>
    public float Yaw { get; private set; }
    /// <summary>The player whose body lies (or just lay) on the floor, for the others' view of it. Not cleared: the view fades the lie out by its own clock.</summary>
    public ushort LieVictim { get; private set; }

    /// <summary>The marked or pinned player is <paramref name="me"/>.</summary>
    public bool MarkedMe(ushort me) => Marked && Victim == me;
    /// <summary>Pinned under the demon: the server holds the local body.</summary>
    public bool PinnedMe(ushort me) => Pinned && Victim == me;

    /// <summary>A pounce message arrived. Changes the stage and says what the local view must start.</summary>
    public PounceViewEffect OnMessage(DemonPounceMsg m, ushort me)
    {
        switch (m.Stage)
        {
            case PounceStage.Wind:
            {
                var effect = SupersededPin(me);
                Go(PounceViewStage.Wind);
                Victim = m.Victim;
                return effect;
            }
            case PounceStage.Pinned:
            {
                var effect = m.Victim == me ? PounceViewEffect.PinnedMine : SupersededPin(me);
                Go(PounceViewStage.Pinned);
                Victim = m.Victim; Yaw = m.Yaw; LieVictim = m.Victim;
                return effect;
            }
            default: // Released or Aborted: let go. Only the current victim's end counts.
            {
                if (!Marked || m.Victim != Victim) return PounceViewEffect.None;
                var mine = Pinned && Victim == me;
                _m.Enter(mine ? PounceViewStage.Rising : PounceViewStage.None);
                if (!mine) return PounceViewEffect.None;
                return m.Stage == PounceStage.Released ? PounceViewEffect.RiseAfterRelease : PounceViewEffect.RiseAfterAbort;
            }
        }
    }

    /// <summary>The camera finished rising.</summary>
    public void RiseDone()
    {
        if (Rising) _m.Enter(PounceViewStage.None);
    }

    /// <summary>The demon is gone (died, the event ended): nobody is marked or held, the rise is cut.</summary>
    public void Reset() => _m.Reset(PounceViewStage.None);

    /// <summary>A pounce message starts while the local player is still pinned by an earlier one: the old pin is over without a word from the server.</summary>
    PounceViewEffect SupersededPin(ushort me) => PinnedMe(me) ? PounceViewEffect.PinSuperseded : PounceViewEffect.None;

    /// <summary>Goes to a stage; a stage that repeats (a second Wind, a second Pinned) is a new pounce: its clock starts over.</summary>
    void Go(PounceViewStage next)
    {
        if (!_m.Enter(next)) _m.Restart();
    }
}
