using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The electrocution pose of a Gang member (docs/events/thieves/07): Shocked shivers at the top, Falling tumbles onto its back and Down lies flat (centred on its feet, head forward: the shape of the
/// lying hit volume), GetUp rises. A state whose clip is named in thieves.json (<c>knockdown_begin</c>, <c>knockdown_loop</c>, <c>knockdown_end</c>, <c>stun_loop</c>) plays it and the body is held
/// upright; with no clip the body is moved in code. The beams, sparks and sound are the client's <see cref="ShockFx"/>.
/// </summary>
public sealed partial class GangMemberView
{
    const float ShiverDeg = 5f, ShiverStepS = 0.05f, TumbleS = 0.45f, RiseS = 1f, LyingLiftM = 0.3f;

    /// <summary>Poses the body for the four electrocution states (other states: nothing, <see cref="Lean"/> has set them). A body first seen already in a state is put there at once.</summary>
    void Knockdown(GangMemberState state, bool first)
    {
        if (state is not (GangMemberState.Shocked or GangMemberState.Falling or GangMemberState.Down or GangMemberState.GetUp)) return;
        var c = _cfg.View.Clips;
        var clip = state switch { GangMemberState.Shocked => c.StunLoop, GangMemberState.Falling => c.KnockdownBegin, GangMemberState.Down => c.KnockdownLoop, _ => c.KnockdownEnd };
        _lean?.Kill();
        _pivot.RotationDegrees = new Vector3(_pivot.RotationDegrees.X, 0, 0);
        if (clip != "" && _anim != null && _anim.HasAnimation(clip)) { _pivot.RotationDegrees = Vector3.Zero; _pivot.Position = Vector3.Zero; return; } // the clip lies, falls and rises by itself
        var lyingAt = new Vector3(0, LyingLiftM, 0.5f * _cfg.View.CapsuleHeightM * _cfg.Of(_kind).HitScale); // centred on the feet, the head forward (-Z)
        var lying = new Vector3(-90, 0, 0);
        switch (state)
        {
            case GangMemberState.Shocked:
                _pivot.Position = Vector3.Zero;
                _lean = CreateTween().SetLoops();
                _lean.TweenProperty(_pivot, "rotation_degrees:z", ShiverDeg, ShiverStepS);
                _lean.TweenProperty(_pivot, "rotation_degrees:z", -ShiverDeg, ShiverStepS);
                break;
            case GangMemberState.Falling when !first:
                _lean = CreateTween().SetParallel();
                _lean.TweenProperty(_pivot, "rotation_degrees", lying, TumbleS).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                _lean.TweenProperty(_pivot, "position", lyingAt, TumbleS).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                break;
            case GangMemberState.Falling:
            case GangMemberState.Down:
                _pivot.RotationDegrees = lying;
                _pivot.Position = lyingAt;
                break;
            case GangMemberState.GetUp when !first:
                _lean = CreateTween().SetParallel();
                _lean.TweenProperty(_pivot, "rotation_degrees", Vector3.Zero, RiseS).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
                _lean.TweenProperty(_pivot, "position", Vector3.Zero, RiseS).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
                break;
            default:
                _pivot.RotationDegrees = Vector3.Zero;
                _pivot.Position = Vector3.Zero;
                break;
        }
    }
}
