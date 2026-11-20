using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Which gang-rig clip each state plays (docs/events/thieves/08). Walking and standing share two clips (<c>moving</c>); chasing runs, fleeing and hauling sprint; the blow clip is started so its peak lands with the
/// server's blow (<see cref="GangLook.Plan"/>); stealing kneels down (sped up) and loops, and is left by a blend, never by the pack's 7.5 s way up; the climbs play at the speed of <c>loot.climb_speed</c> and the body is slid
/// to the wall plane; a hop is the jump clip with a short landing after it; the electrocution states play the knockdown clips; and an idle Gang boss (or an Enforcer on guard) now and then plays a gesture of its own.
/// Everything with a name is data (<see cref="GangClips"/>, <see cref="GangPlayback"/>, <see cref="GangBlows"/>, <see cref="GangClimbView"/>, <see cref="GangGestures"/>).
/// </summary>
public sealed partial class GangMemberView
{
    /// <summary>One clip to play: its key (what the state wants: the same key means "already playing"), where it starts, how fast, what follows it, the crossfade.</summary>
    readonly record struct Want(string Key, string Clip, float Speed = 1f, float From = 0f, string Then = "", double Blend = -1);

    bool _hasBlow, _hasSteal;
    string _key = "";
    GangBlow? _blow;
    BlowPlan _plan;
    int _blowSerial;
    double _blowDelay, _landLeft, _gestureIn = -1, _gestureLeft;
    Random? _rng;
    Tween? _slide;

    /// <summary>Which of the clips this body has (a member without a blow clip leans in code; one without a kneel bends over).</summary>
    void FindClips()
    {
        var c = _view.Clips;
        _hasBlow = _view.Blows.Of(_kind).Any(b => _anim!.HasAnimation(b.Clip));
        _hasSteal = _anim!.HasAnimation(ClipOf(c.Steal)) || _anim.HasAnimation(ClipOf(c.StealLoop));
    }

    bool Has(string clip) => clip != "" && _anim != null && _anim.HasAnimation(clip);

    double BlendS => _view.Playback.BlendS;

    /// <summary>A state has changed: the blow of a windup is chosen and timed, a hop's landing is scheduled.</summary>
    void NoteState(GangMemberState before, GangMemberState now, bool first)
    {
        if (now is GangMemberState.Windup or GangMemberState.Swing && !(now == GangMemberState.Swing && before == GangMemberState.Windup))
        {
            _blow = GangLook.Blow(_view, _kind, _look, _blows++);
            if (_blow != null && Has(_blow.Clip))
            {
                _blowSerial++;
                // from a windup: the peak lands when the windup ends; a swing seen without its windup (first seen, a lost snapshot) starts at the peak
                _plan = now == GangMemberState.Windup ? GangLook.Plan(_blow, _cfg.Of(_kind).Melee.WindupSeconds) : new BlowPlan(0, _blow.PeakS);
                _blowDelay = _plan.DelayS;
            }
            else _blow = null;
        }
        if (before is GangMemberState.HopIn or GangMemberState.HopOut && now is not (GangMemberState.HopIn or GangMemberState.HopOut) && !first) _landLeft = _view.Playback.LandSeconds;
    }

    int _blows;

    /// <summary>Starts the clip the state wants, when it is not already playing.</summary>
    void Apply(GangMemberState state, bool moving, bool first)
    {
        if (_anim == null) return;
        var w = WantFor(state, moving);
        if (w.Key == _key) return;
        _key = w.Key;
        var clip = w.Clip;
        if (!Has(clip)) clip = ClipOf(_view.Clips.Idle); // a clip that is not there: stand, rather than freeze
        if (!Has(clip)) return;
        _anim.Play(clip, w.Blend >= 0 ? w.Blend : state == GangMemberState.Dead ? 0.1 : BlendS, w.Speed);
        if (w.From > 0) _anim.Seek(w.From, true);
        if (state == GangMemberState.Dead && first) _anim.Seek(_anim.GetAnimation(clip).Length, true); // already dead when first seen: lies at once
        if (Has(w.Then)) _anim.Queue(w.Then);
    }

    float LengthOf(string clip) => Has(clip) ? (float)_anim!.GetAnimation(clip).Length : 1f;

    Want WantFor(GangMemberState state, bool moving)
    {
        var c = _view.Clips;
        var p = _view.Playback;
        switch (state)
        {
            case GangMemberState.Dead: return new Want("dead", _death);
            case GangMemberState.Windup or GangMemberState.Swing:
                if (_blow == null) return new Want("idle", ClipOf(c.Idle));
                if (state == GangMemberState.Windup && _blowDelay > 0) return new Want("blow-wait", ClipOf(c.Idle)); // the clip's wind-in is shorter than the windup: stand until it is time
                return new Want("blow" + _blowSerial, _blow.Clip, 1f, _plan.FromS);
            case GangMemberState.Chase or GangMemberState.Board: return new Want("run", ClipOf(c.Run));
            case GangMemberState.Flee: return new Want("sprint", ClipOf(c.Sprint));
            case GangMemberState.Haul: return new Want("walk", ClipOf(c.Walk), Math.Clamp(_data.Loot.HaulSpeedMps / _cfg.Of(_kind).WalkSpeed, 0.6f, 1.6f)); // a loaded Thief strolls back: no sprint
            case GangMemberState.Steal:
            {
                var down = ClipOf(c.Steal);
                if (!Has(down)) return new Want("steal", ClipOf(c.StealLoop));
                return new Want("steal", down, LengthOf(down) / Math.Max(0.1f, p.StealEnterSeconds), 0f, ClipOf(c.StealLoop));
            }
            case GangMemberState.ClimbUp: return new Want("climb-up", ClipOf(c.ClimbUp), GangLook.ClimbPlayback(_view.Climb, _data.Loot.ClimbSpeed));
            case GangMemberState.ClimbDown: return new Want("climb-down", ClipOf(c.ClimbDown), GangLook.ClimbPlayback(_view.Climb, _data.Loot.ClimbSpeed));
            case GangMemberState.WorkAloft: return new Want("climb-work", ClipOf(c.ClimbWork));
            case GangMemberState.HopIn: return new Want("hop", ClipOf(c.HopIn), p.HopSpeed, p.HopSeekS);
            case GangMemberState.HopOut: return new Want("hop", ClipOf(c.HopOut), p.HopSpeed, p.HopSeekS);
            case GangMemberState.Shocked: return new Want("stun", ClipOf(c.StunLoop));
            case GangMemberState.Falling: return new Want("fall", ClipOf(c.KnockdownBegin));
            case GangMemberState.Down: return new Want("down", ClipOf(c.KnockdownLoop));
            case GangMemberState.GetUp:
            {
                var end = ClipOf(c.KnockdownEnd);
                var seek = Math.Min(p.GetUpSeekS, LengthOf(end) * 0.9f);
                return new Want("get-up", end, Math.Max(0.5f, (LengthOf(end) - seek) / Math.Max(0.2f, _data.Loot.Connector.GetUpSeconds)), seek);
            }
        }
        if (_landLeft > 0) return new Want("land", ClipOf(c.Land), LengthOf(ClipOf(c.Land)) / Math.Max(0.1f, p.LandSeconds));
        if (_key == "gesture" && !moving && GestureState(state)) return new Want("gesture", "");   // keeps playing until its time is up
        return moving ? new Want("walk", ClipOf(c.Walk)) : new Want("idle", ClipOf(c.Idle));
    }

    /// <summary>Time that runs by itself: a blow waiting for its start, a landing, a gesture.</summary>
    double _owed;
    bool _manual;

    /// <summary>Plays the clips at the creature's update rate (<see cref="CreatureLod"/>); returns the time this frame's update covers.</summary>
    public double Pace(double dt, bool slow, bool due) => CreatureLod.Pace(_anim, ref _manual, ref _owed, dt, slow, due);

    public override void _Process(double delta)
    {
        if (_anim == null) return;
        if (_blowDelay > 0 && (_blowDelay -= delta) <= 0) Reapply();
        if (_landLeft > 0 && (_landLeft -= delta) <= 0) Reapply();
        Gesture(delta);
    }

    void Reapply() { _key = ""; Apply(_stateNow, _movingNow, false); }

    // ---- gestures ----

    bool GestureState(GangMemberState state) => _kind == GangMemberKind.GangBoss ? state is GangMemberState.Idle or GangMemberState.Guard : _kind == GangMemberKind.Enforcer && state == GangMemberState.Guard;

    /// <summary>An idle Gang boss or a guarding Enforcer now and then plays a taunt, or a drag when it smokes: random by the look and time, never while it walks, fights or works.</summary>
    void Gesture(double delta)
    {
        if (_anim == null || !GestureState(_stateNow) || _movingNow || _firstShow) { if (_key == "gesture") Reapply(); _gestureIn = -1; return; }
        _rng ??= new Random((int)GangLook.Mix(_look, 9));
        if (_key == "gesture")
        {
            if ((_gestureLeft -= delta) <= 0) Reapply();
            return;
        }
        var g = _view.Gestures;
        if (_gestureIn < 0) _gestureIn = g.MinGapSeconds + _rng.NextDouble() * (g.MaxGapSeconds - g.MinGapSeconds);
        if ((_gestureIn -= delta) > 0) return;
        _gestureIn = -1;
        var pool = g.Taunts.Select(ClipOf).Where(Has).ToList();
        if (Smokes && Has(ClipOf(g.Drink))) pool.Add(ClipOf(g.Drink));
        if (pool.Count == 0) return;
        var clip = Smokes && Has(ClipOf(g.Drink)) && _rng.Next(2) == 0 ? ClipOf(g.Drink) : pool[_rng.Next(pool.Count)];
        _key = "gesture";
        _gestureLeft = LengthOf(clip);
        _anim.Play(clip, BlendS);
    }

    bool Smokes => GangLook.Smokes(_view, _look);

    // ---- the climb ----

    /// <summary>On the wall, the body is slid forward to the surface it climbs (the clips hang the body 0.27 m from the wall plane; the member stands <c>climb.surface_m</c> from the surface), and back off it.</summary>
    void SlideToWall(GangMemberState state, bool first)
    {
        if (_model == null) return;
        var cl = _view.Climb;
        var onWall = state is GangMemberState.ClimbUp or GangMemberState.WorkAloft or GangMemberState.ClimbDown or GangMemberState.Shocked or GangMemberState.Falling;
        var to = onWall ? new Vector3(0, 0, -(cl.SurfaceM - cl.WallPlaneM)) : Vector3.Zero; // the pivot faces -Z, towards the wall
        _slide?.Kill();
        if (first || cl.SlideSeconds <= 0) { _model.Position = to; return; }
        _slide = CreateTween();
        _slide.TweenProperty(_model, "position", to, cl.SlideSeconds);
    }
}
