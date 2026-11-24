using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Bigfoot on the client (issue #264): the world entity of type <see cref="BigfootRules.TypeId"/> drawn as the giant (bigfoot.json), the logs it
/// throws (type <see cref="BigfootRules.TrunkTypeId"/>), and the player it holds: the eye swings with its hand. Its one-shot effects are <see cref="BigfootFx"/>; the giant's corpse (a
/// corpse owned by <see cref="BigfootRules.TargetId"/>) is drawn by <see cref="CorpsesClient"/>.
/// The model (scripts/models/make_bigfoot_model.py) has a clip per act, a flinch and a roar (<see cref="BigfootRules.Clips"/>): <see cref="ClipFor"/> picks one and
/// stretches a wind-up over its act's time, Walk and Run play at the speed the giant moves (<see cref="PaceGait"/>).
/// The server owns everything: a client only shows it, and stops predicting its own body while the Bigfoot holds it (<see cref="ClientView.HeldByBigfoot"/>) or its
/// vehicle while the Bigfoot throws it (<see cref="CarThrown"/>).
/// </summary>
internal sealed class BigfootClient
{
    readonly ClientContext _ctx;
    readonly BigfootFile _data;
    readonly BodyFlings _flings;
    readonly Dictionary<ushort, BigfootView> _bigfoots = new();
    readonly Dictionary<ushort, Node3D> _trunks = new();
    readonly Dictionary<ushort, Vector3> _trunkLast = new();
    /// <summary>Vehicles the Bigfoot threw: the client time until which the driver's prediction waits (the server flies the vehicle, the driver's input is ignored).</summary>
    readonly Dictionary<ushort, double> _thrownCars = new();
    /// <summary>What the flailing hand adds to the held player's own eye: an offset (metres, world) and a roll (radians). Zero when not held.</summary>
    Vector3 _heldSwing;
    float _heldRoll;
    /// <summary>True while the Bigfoot's throw has the vehicle (the driver's client does not predict it).</summary>
    public bool CarThrown(ushort car) => _thrownCars.TryGetValue(car, out var until) && _ctx.Now < until;

    public BigfootClient(ClientContext ctx, BigfootFile data, BodyFlings flings)
    {
        _ctx = ctx; _data = data; _flings = flings;
        BigfootRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.DespawnedFor += OnDespawned;
    }

    /// <summary>The own eye's offset (metres, world) and roll (radians) from the hand that holds the player; zero when not held.</summary>
    public Vector3 HeldSwing => _heldSwing;
    public float HeldRoll => _heldRoll;

    /// <summary>Where the first Bigfoot stands; null when there is none.</summary>
    public Node3D? FirstNode => _bigfoots.Values.FirstOrDefault()?.Node;

    /// <summary>The driver's prediction of a vehicle waits for the flight and a moment after the landing (the server flies the vehicle, the driver's input is ignored).</summary>
    public void ThrowCar(ushort car, double until) => _thrownCars[car] = until;

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type == BigfootRules.TrunkTypeId) { SpawnTrunkView(e); return; }
        if (e.Type != BigfootRules.TypeId) return;
        var v = new BigfootView { Node = new Node3D { Name = $"Bigfoot{e.Id}" }, Pivot = new Node3D { Name = "Pivot" } };
        v.HitBit.Update(BigfootRules.HitToggled(e.State.State)); // the bit it spawned with is no hit
        v.Node.AddChild(v.Pivot);
        var model = BigfootModel.MakeGiant(_data, out var anim);
        v.Pivot.AddChild(model);
        v.Anim = anim;
        BigfootModel.IndexClips(v.Clips, anim);
        _ctx.Owned.AddChild(v.Node);
        BigfootModel.IndexBones(v);
        _bigfoots[e.Id] = v;
    }

    void OnDespawned(ushort id, WorldDespawnReason why)
    {
        if (_bigfoots.Remove(id, out var v)) { TrackHold(v, BigfootAct.Dead, 0); v.Node.QueueFree(); } // the one in its hand drops
        if (_trunks.Remove(id, out var t))
        {
            // a log that ended on the ground (or on a player) thuds where it was
            if (_trunkLast.Remove(id, out var at) && why != WorldDespawnReason.Removed) Sfx.Play3D(BigfootSounds.Log, at);
            t.QueueFree();
        }
    }

    void SpawnTrunkView(WorldReplica.Entity e)
    {
        var node = BigfootModel.MakeLog();
        node.Name = $"Trunk{e.Id}";
        node.Visible = false; // until the first interpolated position: it must not flash at the world's origin
        _ctx.Owned.AddChild(node);
        _trunks[e.Id] = node;
    }

    // ---- per frame ----

    public void Render(double delta)
    {
        if (_thrownCars.Count > 0)
            foreach (var (car, until) in _thrownCars) if (_ctx.Now >= until) _thrownCars.Remove(car); // expired: CarThrown already says false, the entry just leaked
        var held = false;
        BigfootView? holder = null;
        if (_ctx.LastTick != 0)
        {
            var rt = _ctx.RenderTick;
            PlaceBigfoots(rt, delta, ref held, ref holder);
            PlaceTrunks(rt);
        }
        _ctx.View.HeldByBigfoot = held;
        _flings.TickOwn(delta);
        _ctx.Hud?.SetFlungHidesCrosshair(_flings.OwnFlung);
        SwingHeldEye(holder, delta);
        PlayFightMusic();
    }

    /// <summary>Places every Bigfoot from its interpolated state and shows what it does; notes whether it holds the local player (<paramref name="held"/>) and which one does (<paramref name="holder"/>).</summary>
    void PlaceBigfoots(double rt, double delta, ref bool held, ref BigfootView? holder)
    {
        var now = _ctx.Now;
        foreach (var (id, v) in _bigfoots)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            if (e.Buf.At(rt) is { } s)
            {
                v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                // the server snaps its yaw at the start of an attack and steps it in a detour: the figure turns there quickly instead of jumping
                var yaw = v.HasLastAt ? Mathf.LerpAngle(v.Node.Rotation.Y, s.Yaw, 1f - MathF.Exp(-14f * (float)delta)) : s.Yaw;
                v.Node.Rotation = new Vector3(0, yaw, 0);
                MeasureSpeed(v, delta);
            }
            var raw = e.State.State;
            var act = BigfootRules.ActOf(raw);
            TrackHold(v, act, e.State.Owner);
            if (_ctx.MyId != 0 && e.State.Owner == _ctx.MyId && act == BigfootAct.Hold) held = true;
            if (v.HitBit.Update(BigfootRules.HitToggled(raw)))
            {
                // it flinches only standing (a flinch in the chase would cut the stride and start it over), and at most every few seconds
                if (act == BigfootAct.Idle && now >= v.HitUntil + HitReactionGapSeconds) PlayOver(v, BigfootRules.ClipHit, 0.05, now);
            }
            if (act != BigfootAct.Idle && now < v.HitUntil) v.HitUntil = now; // a flinch or a roar gives way to whatever it does next
            if (now >= v.HitUntil && v.Drawn.Update((byte)act)) ShowBigfootAct(v, act, now);
            MoveBigfoot(v, act, BigfootRules.IsEnraged(raw), now);
            if (_ctx.MyId != 0 && e.State.Owner == _ctx.MyId && act == BigfootAct.Hold) holder = v;
        }
    }

    /// <summary>The thrown trunks fly tumbling along their replicated path.</summary>
    void PlaceTrunks(double rt)
    {
        foreach (var (id, node) in _trunks)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.Buf.At(rt) is not { } s) continue;
            var at = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            node.GlobalPosition = at;
            node.Visible = true;
            node.Rotation = new Vector3((float)(_ctx.Now * 5.0), s.Yaw, 0); // it tumbles end over end as it flies
            _trunkLast[id] = at;
        }
    }

    /// <summary>The server holds the body at a fixed point in front of the giant; the flailing is drawn here, so the eye rides the same swing as the figure.</summary>
    void SwingHeldEye(BigfootView? holder, double delta)
    {
        var swing = Vector3.Zero; var tilt = 0f;
        if (holder != null && _ctx.Me != null)
        {
            var local = holder.Node.ToLocal(_ctx.Me.GlobalPosition);
            var t = (float)(_ctx.Now - holder.Since);
            var shaken = holder.Pivot.Transform.Basis * local + new Vector3(0, 0.45f * MathF.Sin(t * 22f), 0.3f * MathF.Sin(t * 11f + 1.3f)); // the hand also pumps up and down and jerks back and forth
            swing = holder.Node.GlobalBasis * (shaken - local);
            if (!GameSettings.Current.ReduceMotion) tilt = holder.Pivot.Rotation.Z * 1.6f + 0.1f * MathF.Sin(t * 17f);
        }
        var ease = 1f - MathF.Exp(-14f * (float)delta); // in and out without a jump
        _heldSwing = _heldSwing.Lerp(swing, ease);
        _heldRoll = Mathf.Lerp(_heldRoll, tilt, ease);
    }

    /// <summary>Its music plays while a living one is near the player (the file is private: without it there is none).</summary>
    void PlayFightMusic()
    {
        var near = false;
        if (_ctx.Me != null)
            foreach (var v in _bigfoots.Values)
                if ((BigfootAct)v.Drawn.Last != BigfootAct.Dead && v.Node.GlobalPosition.DistanceTo(_ctx.Me.GlobalPosition) <= _data.Music.RadiusM) near = true;
        Sfx.FightMusic(near, _data.Music.File, _data.Music.VolumeDb);
    }

    const double HitReactionGapSeconds = 4;

    /// <summary>
    /// What the giant's act says of the player in its hand: Hold grabs (<paramref name="owner"/> is the player), and when the Hold is over a Launch throws
    /// them while anything else (the hold cut short, the giant dead or gone) lets them drop.
    /// </summary>
    void TrackHold(BigfootView v, BigfootAct act, ushort owner)
    {
        if (act == BigfootAct.Hold && owner != 0)
        {
            v.HeldId = owner;
            _flings.Grab(owner, v);
            return;
        }
        if (v.HeldId == 0) return;
        var id = v.HeldId;
        v.HeldId = 0;
        _flings.Release(id, thrown: act == BigfootAct.Launch);
    }

    /// <summary>Plays a one-shot clip (the flinch, the roar) over the act's: the act's clip is held back until it is over, then started again.</summary>
    void PlayOver(BigfootView v, string plain, double blend, double now)
    {
        if (v.Anim == null || !v.Clips.TryGetValue(plain, out var clip)) return;
        v.Anim.SpeedScale = 1;
        v.Anim.Play(clip, blend);
        v.HitUntil = now + v.Anim.GetAnimation(clip).Length;
        v.Drawn.Reset(); // the act's clip is due again when this one is over
        v.Introduced = true; // it counts as the first sight: a death inside its window plays the fall, not the lying pose
    }

    /// <summary>The ground speed of the figure, from where it was drawn a frame ago (smoothed: the interpolated position steps a little).</summary>
    void MeasureSpeed(BigfootView v, double delta)
    {
        var at = v.Node.GlobalPosition;
        var dt = (float)delta;
        if (v.HasLastAt && dt > 1e-4f)
        {
            var moved = new Vector2(at.X - v.LastAt.X, at.Z - v.LastAt.Z).Length() / dt;
            v.Speed = Mathf.Lerp(v.Speed, moved, 1f - MathF.Exp(-10f * dt));
        }
        v.LastAt = at; v.HasLastAt = true;
    }

    /// <summary>
    /// The clip an act plays, the seconds the clip is stretched over (0: its own length) and the blend into it. A wind-up is stretched over its act's
    /// time, so its blow lands when the server's does.
    /// </summary>
    (string Clip, float Seconds, double Blend) ClipFor(BigfootAct act) => act switch
    {
        BigfootAct.Walk => (BigfootRules.ClipWalk, 0, 0.25),
        BigfootAct.Run => (BigfootRules.ClipRun, 0, 0.25),
        BigfootAct.TrunkWindup => (BigfootRules.ClipTrunkThrow, _data.Trunk.WindupSeconds, 0.12),
        BigfootAct.StompWindup => (BigfootRules.ClipStompWindup, _data.Stomp.WindupSeconds, 0.12),
        BigfootAct.VehicleWindup => (BigfootRules.ClipVehicleLift, _data.VehicleThrow.WindupSeconds, 0.12),
        BigfootAct.GrabReach => (BigfootRules.ClipGrabReach, _data.Grab.ReachSeconds, 0.1),
        BigfootAct.Stomp => (BigfootRules.ClipStomp, BigfootRules.StompSeconds, 0.03), // it begins in the wind-up's last pose
        BigfootAct.Launch => (BigfootRules.ClipLaunch, BigfootRules.LaunchSwingSeconds, 0.08), // a player is thrown with Hurl instead (ShowBigfootAct)
        BigfootAct.Roar => (BigfootRules.ClipRoar, BigfootRules.RoarSeconds, 0.15),
        BigfootAct.Hold => (BigfootRules.ClipHold, 0, 0.2),
        BigfootAct.Dead => (BigfootRules.ClipDeath, 0, 0.1),
        _ => (BigfootRules.ClipIdle, 0, 0.25),
    };

    /// <summary>Picks the clip of an act (<see cref="ClipFor"/>) and starts it.</summary>
    void ShowBigfootAct(BigfootView v, BigfootAct act, double now)
    {
        var first = !v.Introduced;
        v.Introduced = true; v.Since = now;
        if (v.Anim == null) return;
        var (plain, seconds, blend) = ClipFor(act);
        v.Before = v.Shown; v.Shown = act;
        if (act == BigfootAct.Launch && v.Before == BigfootAct.Hold) plain = BigfootRules.ClipHurl;
        if (!v.Clips.TryGetValue(plain, out var clip)) return;
        var length = v.Anim.GetAnimation(clip).Length;
        v.Anim.SpeedScale = seconds > 0 && length > 0.05 ? (float)(length / seconds) : 1f;
        v.Anim.Play(clip, blend);
        // a body that was already dead when we first saw it lies in its last pose; one that dies in front of us falls
        if (act == BigfootAct.Dead && first) v.Anim.Seek(length, true);
    }

    /// <summary>Walk and Run play at the speed the giant moves: one loop of the clip covers its stride (bigfoot.json), so the feet hold the ground.</summary>
    void PaceGait(BigfootView v, BigfootAct act, double now)
    {
        if (v.Anim == null || now < v.HitUntil || act is not (BigfootAct.Walk or BigfootAct.Run)) return;
        var walking = act == BigfootAct.Walk;
        if (!v.Clips.TryGetValue(walking ? BigfootRules.ClipWalk : BigfootRules.ClipRun, out var clip)) return;
        var natural = (walking ? _data.Movement.WalkStrideM : _data.Movement.RunStrideM) / (float)v.Anim.GetAnimation(clip).Length; // m/s at speed scale 1
        v.Anim.SpeedScale = Math.Clamp(v.Speed / natural, 0.35f, 1.8f); // turning on the spot it still steps, slowly
    }

    /// <summary>What the clips do not carry: the gait's pace, the log in its hand, the shake of the fist that holds a player, the heavier breath enraged.</summary>
    void MoveBigfoot(BigfootView v, BigfootAct act, bool enraged, double now)
    {
        var t = (float)(now - v.Since);
        PaceGait(v, act, now);
        float roll = 0, rise = 1;
        // the whole figure rocks with the player in its fist: the held player's eye rides this (RenderBigfoot)
        if (act == BigfootAct.Hold) { roll = MathF.Sin(t * 11f) * 0.1f; rise = 1f + 0.02f * MathF.Sin(t * 22f); }
        // the log it is about to throw rides its right hand: up on the shoulder, drawn back, hurled, gone at the release (the flying one takes over)
        var winding = act == BigfootAct.TrunkWindup;
        if (winding && v.HeldLog == null)
        {
            v.HeldLog = BigfootModel.MakeLog();
            v.Pivot.AddChild(v.HeldLog);
        }
        if (v.HeldLog != null)
        {
            v.HeldLog.Visible = winding;
            if (winding)
            {
                var k = Math.Clamp(t / _data.Trunk.WindupSeconds, 0f, 1f);
                var hand = v.Skel != null && v.HandR >= 0
                    ? v.Pivot.GlobalTransform.AffineInverse() * (v.Skel.GlobalTransform * v.Skel.GetBoneGlobalPose(v.HandR).Origin)
                    : new Vector3(0.85f, _data.HeightM * 0.9f, 0.3f);
                v.HeldLog.Position = hand + new Vector3(-0.1f, 0.35f, 0);
                // its front end tips up as it is drawn back, and comes down with the throw (the clip's last quarter)
                var swing = Math.Clamp((k - 0.74f) / 0.26f, 0f, 1f);
                v.HeldLog.Rotation = new Vector3(Mathf.Lerp(-0.35f * MathF.Min(1f, k / 0.74f), 0.25f, swing), 0, 0);
            }
        }
        if (enraged) rise *= 1f + 0.025f * MathF.Sin((float)now * 13f);
        v.Pivot.Rotation = new Vector3(0, 0, roll);
        v.Pivot.Scale = new Vector3(1, rise, 1);
    }

    /// <summary>
    /// The Bigfoot standing at a roar's place plays the Roar clip: its arrival, and a phase that began while it stood. In a fight the server's Roar act
    /// (<see cref="BigfootAct.Roar"/>) stops it and plays the same clip; a giant on the move is left alone here, it would slide.
    /// </summary>
    public void RoarAt(Vector3 at)
    {
        foreach (var v in _bigfoots.Values)
            if ((BigfootAct)v.Drawn.Last == BigfootAct.Idle && v.Node.GlobalPosition.DistanceTo(at) < 4f) PlayOver(v, BigfootRules.ClipRoar, 0.15, _ctx.Now);
    }

}
