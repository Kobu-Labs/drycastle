using System.Numerics;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>The state of one trailer. The machine is <see cref="TowLink.Spec"/>.</summary>
public enum TrailerState : byte { Loose, Hitched, Released }

/// <summary>
/// One trailer's link to whatever tows it: the machine (<see cref="TrailerState"/>, with the lockout as the clock of Released) and the <see cref="TrailerBody"/> the model steps. The
/// server owns the true one; the driver's client steps a copy of the body with <see cref="TowModel"/> the same way (ADR 0014). Latching and unhitching are decided here; the
/// numbers are <see cref="TowGeometry"/>'s. The order of a tick with a tractor: step the tractor (with <see cref="Effect"/> as its tow argument), then <see cref="StepHitched"/>.
/// </summary>
public sealed class TowLink
{
    /// <summary>A loose trailer is latched by a tractor that reverses its hitch onto the eye (<see cref="TowRules.CheckLatch"/>); a hitched one is released on request, then stands released for the lockout (2 s by default) in which nothing can latch it, and is loose again.</summary>
    public static readonly StateMachineSpec<TrailerState> Spec = StateMachineSpec<TrailerState>.Define("Trailer.Tow", "A trailer: standing loose, pulled by a tractor, or just released and not yet to be latched again.", TrailerState.Loose)
        .Allow(TrailerState.Loose, TrailerState.Hitched)
        .Allow(TrailerState.Hitched, TrailerState.Released)
        .Allow(TrailerState.Released, TrailerState.Loose)
        .Note(TrailerState.Loose, "Stands still. The first tractor that reverses its hitch onto the eye (close, lined up) latches it.")
        .Note(TrailerState.Hitched, "The eye is pinned to the tractor's hitch and the trailer follows it (TowModel.Step). Ends on the driver's unhitch request.")
        .Note(TrailerState.Released, "Just unhitched: rolls to a stop, and cannot be latched until the lockout (the tractor's relatch_lockout) has run out; then Loose.")
        .Build();

    readonly StateMachine<TrailerState> _fsm = new(Spec);

    /// <summary>The trailer's pose and articulation, written by the steps; the server places the trailer's node from it.</summary>
    public TrailerBody Body;

    public TowLink(Vector3 position, float yaw) { Body = TrailerBody.At(position, yaw); }

    public TrailerState State => _fsm.Current;
    public bool Hitched => _fsm.Is(TrailerState.Hitched);
    /// <summary>Seconds of lockout left (0 unless released).</summary>
    public float LockoutLeft => _fsm.Is(TrailerState.Released) ? MathF.Max(0f, _fsm.Left) : 0f;

    /// <summary>The latch rule for this trailer against the tractor as it stands (<see cref="TowRules.CheckLatch"/>).</summary>
    public LatchCheck CheckLatch(in TowGeometry g, in TractorMotion tractor) => TowRules.CheckLatch(g, tractor, Body, State);

    /// <summary>Latches onto the tractor when <see cref="CheckLatch"/> says so (the auto-latch: call it every tick the tractor may be reversing onto a loose trailer). True when it coupled.</summary>
    public bool TryLatch(in TowGeometry g, in TractorMotion tractor)
    {
        if (CheckLatch(g, tractor) != LatchCheck.Ok) return false;
        TowModel.Couple(ref Body, g, tractor);
        _fsm.Enter(TrailerState.Hitched);
        return true;
    }

    /// <summary>The unhitch request (the driver holds interact). False when not hitched. The trailer is released at the speed it has and may roll on; the lockout starts.</summary>
    public bool Unhitch(in TowGeometry g)
    {
        if (!Hitched) return false;
        Body.Phi = 0; Body.Slide = 0;
        _fsm.Enter(TrailerState.Released, g.Lockout);
        if (!(g.Lockout > 0)) _fsm.Enter(TrailerState.Loose);
        return true;
    }

    /// <summary>What the tractor's step is told while this trailer is on it: the load and the jackknife block. None when not hitched.</summary>
    public TowEffect Effect(VehicleDef tractor, VehicleDef trailer, in TowGeometry g, bool loaded = true) =>
        Hitched ? new TowEffect(TowLoad.Of(tractor, trailer, loaded), TowModel.BlockOf(g, Body)) : TowEffect.None;

    /// <summary>The tick of a hitched trailer, after the tractor's own step. False when the world blocks the trailer's new place (the body stays and the caller holds the tractor, <see cref="TowModel.Hold(ref DriveState, in DriveState)"/>).</summary>
    public bool StepHitched(in TowGeometry g, in TractorMotion tractor, float dt, ITrailerWorld? world = null)
    {
        if (!Hitched) return true;
        if (world == null) { TowModel.Step(ref Body, g, tractor, dt); return true; }
        return TowModel.StepChecked(ref Body, g, tractor, dt, world);
    }

    /// <summary>The tick of a trailer nobody pulls: the lockout runs out, and one released at speed rolls to a stop.</summary>
    public void StepFree(in TowGeometry g, float dt, ITrailerWorld? world = null)
    {
        if (Hitched) return;
        if (_fsm.Tick(dt) && _fsm.Is(TrailerState.Released)) _fsm.Enter(TrailerState.Loose);
        TowModel.StepLoose(ref Body, g, dt, world);
    }
}
