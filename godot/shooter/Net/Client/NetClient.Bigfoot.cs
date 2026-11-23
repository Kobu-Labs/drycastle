using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Bigfoot on the client (issue #264): the world entity of type <see cref="BigfootRules.TypeId"/> drawn as the giant (bigfoot.json), the logs it
/// throws (type <see cref="BigfootRules.TrunkTypeId"/>), its one-shot effects (<see cref="EventKind.BigfootFx"/>: the shockwave ring, the sounds, the shake), the
/// footprints that lead to its corpse (<see cref="EventKind.BigfootTrail"/>) and the giant's corpse itself (a corpse owned by <see cref="BigfootRules.TargetId"/>).
/// The model (scripts/models/make_bigfoot_model.py) has a clip per act, a flinch and a roar (<see cref="BigfootRules.Clips"/>): <see cref="ClipFor"/> picks one and
/// stretches a wind-up over its act's time, Walk and Run play at the speed the giant moves (<see cref="PaceGait"/>).
/// The server owns everything: a client only shows it, and stops predicting its own body while the Bigfoot holds it (<see cref="_heldByBigfoot"/>) or its
/// vehicle while the Bigfoot throws it (<see cref="_thrownCars"/>).
/// </summary>
public partial class NetClient
{
    /// <summary>bigfoot.json: set by the host.</summary>
    public BigfootFile BigfootData = new();

    /// <summary>The sound events the effects play. There is no roar yet: these are the closest sounds the game already has, and the table to change when real ones arrive.</summary>
    static class BigfootSounds
    {
        public const string Roar = "explosion.vehicle", StompWindup = "land", Shockwave = "explosion.rpg", Throw = "shot.knife", Land = "explosion.vehicle", Log = "impact", Grab = "death";
    }

    /// <summary>The figure of one Bigfoot: the node at its feet, the pivot the procedural motion turns and scales, and the model with its clips.</summary>
    sealed class BigfootView
    {
        public Node3D Node = null!, Pivot = null!;
        public AnimationPlayer? Anim;
        /// <summary>The clip name the model uses for each plain clip name ("Attack" is "EnemyArmature|EnemyArmature|EnemyArmature|Attack").</summary>
        public readonly Dictionary<string, string> Clips = new();
        /// <summary>The act whose clip is playing, as the byte of its <see cref="BigfootAct"/>. A change means the clip is due; <c>Reset</c> after a hit reaction makes it due again.</summary>
        public Change<byte> Drawn;
        /// <summary>The replicated hit bit last seen: a flip is a hit that did not kill.</summary>
        public Change<bool> HitBit;
        /// <summary>An act was drawn at least once: a giant already dead when first seen lies in its last pose, one that dies in front of us falls.</summary>
        public bool Introduced;
        /// <summary>Client time (s) the shown act began, and until which a hit reaction holds the act's clip back.</summary>
        public double Since, HitUntil;
        /// <summary>The skeleton and the bone of its right hand (-1 without one): the log it winds up with rides that hand.</summary>
        public Skeleton3D? Skel;
        public int HandR = -1;
        /// <summary>Where it was drawn last frame and the ground speed measured from that (m/s, smoothed): Walk and Run play at it.</summary>
        public Vector3 LastAt;
        public bool HasLastAt;
        public float Speed;
        /// <summary>The act drawn before the one that plays now: a launch out of a hold is the one-handed throw of a player, any other the two-handed one.</summary>
        public BigfootAct Before;
        public BigfootAct Shown;
        /// <summary>The log on its shoulder while it winds a throw up (the thrown one is a world entity of its own).</summary>
        public Node3D? HeldLog;
        /// <summary>The player in its hand while the act is Hold (0 otherwise): when the act ends, a Launch makes the body fly, anything else lets it drop (#360).</summary>
        public ushort HeldId;
    }

    /// <summary>Finds the skeleton of the giant and its right hand.</summary>
    static void IndexBones(BigfootView v)
    {
        if (v.Node.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D sk) return;
        v.Skel = sk;
        v.HandR = sk.FindBone("Hand.R");
    }

    readonly Dictionary<ushort, BigfootView> _bigfoots = new();
    readonly Dictionary<ushort, Node3D> _trunks = new();
    readonly Dictionary<ushort, Vector3> _trunkLast = new();
    /// <summary>The shockwave rings in the air: the node, its radius now, its age.</summary>
    readonly List<(Node3D Node, float Radius, double Born)> _waveRings = new();
    readonly List<Node3D> _prints = new();
    /// <summary>Vehicles the Bigfoot threw: the client time until which the driver's prediction waits (the server flies the vehicle, the driver's input is ignored).</summary>
    readonly Dictionary<ushort, double> _thrownCars = new();
    /// <summary>The Bigfoot holds this client's player: no prediction, the body is where the server puts it.</summary>
    bool _heldByBigfoot;
    /// <summary>What the flailing hand adds to the held player's own eye: an offset (metres, world) and a roll (radians). Zero when not held.</summary>
    Vector3 _heldSwing;
    float _heldRoll;
    /// <summary>The ragdoll phase of each player the Bigfoot picked up (#360): held, flying after the throw, getting up. Absent or None: the normal figure.</summary>
    readonly Dictionary<ushort, BodyFlingTracker> _flings = new();
    /// <summary>The giant that holds each player now: the hand's shake the body hangs in.</summary>
    readonly Dictionary<ushort, BigfootView> _holders = new();
    Vector3 _ownFlingLast;
    /// <summary>This client's own body is in the fist or flying: its arms and weapon are not drawn.</summary>
    bool OwnBodyFlung => _myId != 0 && _flings.TryGetValue(_myId, out var f) && f.Phase is BodyFling.Held or BodyFling.Flying or BodyFling.Lying;

    /// <summary>True while the Bigfoot's throw has the vehicle (the driver's client does not predict it).</summary>
    bool CarThrown(ushort car) => _thrownCars.TryGetValue(car, out var until) && Now < until;

    /// <summary>Called once from <see cref="_Ready"/>: registers the types and follows their entities.</summary>
    void InitBigfoot()
    {
        BigfootRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnBigfootSpawned;
        WorldEntities.DespawnedFor += OnBigfootDespawned;
    }

    void OnBigfootSpawned(WorldReplica.Entity e)
    {
        if (e.Type == BigfootRules.TrunkTypeId) { SpawnTrunkView(e); return; }
        if (e.Type != BigfootRules.TypeId) return;
        var v = new BigfootView { Node = new Node3D { Name = $"Bigfoot{e.Id}" }, Pivot = new Node3D { Name = "Pivot" } };
        v.HitBit.Update(BigfootRules.HitToggled(e.State.State)); // the bit it spawned with is no hit
        v.Node.AddChild(v.Pivot);
        var model = MakeGiant(out var anim);
        v.Pivot.AddChild(model);
        v.Anim = anim;
        IndexClips(v.Clips, anim);
        Owned.AddChild(v.Node);
        IndexBones(v);
        _bigfoots[e.Id] = v;
    }

    /// <summary>The giant: the model scaled to its height and turned to face forward, or a box of its size without one (headless, or the file is missing).</summary>
    Node3D MakeGiant(out AnimationPlayer? anim)
    {
        anim = null;
        if (ModelLib.Instance(BigfootData.Model) is { } model)
        {
            model.Scale = Vector3.One * BigfootData.ModelScale;
            model.RotationDegrees = new Vector3(0, BigfootData.ModelYawDeg, 0);
            anim = model.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
            if (anim != null)
            {
                foreach (var name in anim.GetAnimationList())
                    anim.GetAnimation(name).LoopMode = BigfootRules.LoopedClips.Contains(name) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            }
            return model;
        }
        var h = BigfootData.HeightM;
        var box = new Node3D();
        box.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(h * 0.5f, h, h * 0.35f) }, Position = new Vector3(0, h / 2, 0) });
        return box;
    }

    /// <summary>Maps the plain clip names to the model's (a model exported from a tool may prefix them: "Armature|Idle").</summary>
    static void IndexClips(Dictionary<string, string> clips, AnimationPlayer? anim)
    {
        if (anim == null) return;
        foreach (var plain in BigfootRules.Clips)
            foreach (var name in anim.GetAnimationList())
                if (name == plain || name.EndsWith("|" + plain)) { clips[plain] = name; break; }
    }

    void OnBigfootDespawned(ushort id, WorldDespawnReason why)
    {
        if (_bigfoots.Remove(id, out var v)) { TrackHold(v, BigfootAct.Dead, 0); v.Node.QueueFree(); } // the one in its hand drops
        if (_trunks.Remove(id, out var t))
        {
            // a log that ended on the ground (or on a player) thuds where it was
            if (_trunkLast.Remove(id, out var at) && why != WorldDespawnReason.Removed) Sfx.Play3D(BigfootSounds.Log, at);
            t.QueueFree();
        }
    }

    /// <summary>A log: a thick trunk with a paler cut face at each end and two branch stubs, lying along -Z. The one it throws and the one on its shoulder.</summary>
    static Node3D MakeLog()
    {
        const float length = 3.8f;
        var node = new Node3D();
        var bark = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.20f, 0.12f), Roughness = 1f };
        var cut = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.62f, 0.40f), Roughness = 1f };
        node.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.44f, Height = length, RadialSegments = 10 }, MaterialOverride = bark, RotationDegrees = new Vector3(90, 0, 0) });
        foreach (var end in new[] { -1f, 1f })
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = end < 0 ? 0.33f : 0.41f, BottomRadius = end < 0 ? 0.33f : 0.41f, Height = 0.03f, RadialSegments = 10 },
                MaterialOverride = cut, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0, end * (length / 2f + 0.005f)),
            });
        node.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.12f, Height = 0.7f, RadialSegments = 6 }, MaterialOverride = bark, Position = new Vector3(0.45f, 0.3f, -0.7f), RotationDegrees = new Vector3(0, 0, -50) });
        node.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.1f, Height = 0.55f, RadialSegments = 6 }, MaterialOverride = bark, Position = new Vector3(-0.4f, -0.2f, 0.8f), RotationDegrees = new Vector3(0, 0, 120) });
        return node;
    }

    void SpawnTrunkView(WorldReplica.Entity e)
    {
        var node = MakeLog();
        node.Name = $"Trunk{e.Id}";
        node.Visible = false; // until the first interpolated position: it must not flash at the world's origin
        Owned.AddChild(node);
        _trunks[e.Id] = node;
    }

    // ---- per frame ----

    void RenderBigfoot()
    {
        if (_thrownCars.Count > 0)
            foreach (var (car, until) in _thrownCars) if (Now >= until) _thrownCars.Remove(car); // expired: CarThrown already says false, the entry just leaked
        var held = false;
        BigfootView? holder = null;
        if (_lastTick != 0)
        {
            var rt = RenderTickNow();
            var now = Now;
            foreach (var (id, v) in _bigfoots)
            {
                if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
                if (e.Buf.At(rt) is { } s)
                {
                    v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                    // the server snaps its yaw at the start of an attack and steps it in a detour: the figure turns there quickly instead of jumping
                    var yaw = v.HasLastAt ? Mathf.LerpAngle(v.Node.Rotation.Y, s.Yaw, 1f - MathF.Exp(-14f * (float)GetProcessDeltaTime())) : s.Yaw;
                    v.Node.Rotation = new Vector3(0, yaw, 0);
                    MeasureSpeed(v);
                }
                var raw = e.State.State;
                var act = BigfootRules.ActOf(raw);
                TrackHold(v, act, e.State.Owner);
                if (_myId != 0 && e.State.Owner == _myId && act == BigfootAct.Hold) held = true;
                if (v.HitBit.Update(BigfootRules.HitToggled(raw)))
                {
                    // it flinches only standing (a flinch in the chase would cut the stride and start it over), and at most every few seconds
                    if (act == BigfootAct.Idle && now >= v.HitUntil + HitReactionGapSeconds) PlayOver(v, BigfootRules.ClipHit, 0.05, now);
                }
                if (act != BigfootAct.Idle && now < v.HitUntil) v.HitUntil = now; // a flinch or a roar gives way to whatever it does next
                if (now >= v.HitUntil && v.Drawn.Update((byte)act)) ShowBigfootAct(v, act, now);
                MoveBigfoot(v, act, BigfootRules.IsEnraged(raw), now);
                if (_myId != 0 && e.State.Owner == _myId && act == BigfootAct.Hold) holder = v;
            }
            foreach (var (id, node) in _trunks)
            {
                if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.Buf.At(rt) is not { } s) continue;
                var at = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                node.GlobalPosition = at;
                node.Visible = true;
                node.Rotation = new Vector3((float)(Now * 5.0), s.Yaw, 0); // it tumbles end over end as it flies
                _trunkLast[id] = at;
            }
        }
        _heldByBigfoot = held;
        TickOwnFling();
        _hud?.SetFlungHidesCrosshair(OwnBodyFlung);
        // the server holds the body at a fixed point in front of the giant; the flailing is drawn here, so the eye rides the same swing as the figure
        var swing = Vector3.Zero; var tilt = 0f;
        if (holder != null && _me != null)
        {
            var local = holder.Node.ToLocal(_me.GlobalPosition);
            var t = (float)(Now - holder.Since);
            var shaken = holder.Pivot.Transform.Basis * local + new Vector3(0, 0.45f * MathF.Sin(t * 22f), 0.3f * MathF.Sin(t * 11f + 1.3f)); // the hand also pumps up and down and jerks back and forth
            swing = holder.Node.GlobalBasis * (shaken - local);
            if (!GameSettings.Current.ReduceMotion) tilt = holder.Pivot.Rotation.Z * 1.6f + 0.1f * MathF.Sin(t * 17f);
        }
        var ease = 1f - MathF.Exp(-14f * (float)GetProcessDeltaTime()); // in and out without a jump
        _heldSwing = _heldSwing.Lerp(swing, ease);
        _heldRoll = Mathf.Lerp(_heldRoll, tilt, ease);
        // its music plays while a living one is near the player (the file is private: without it there is none)
        var near = false;
        if (_me != null)
            foreach (var v in _bigfoots.Values)
                if ((BigfootAct)v.Drawn.Last != BigfootAct.Dead && v.Node.GlobalPosition.DistanceTo(_me.GlobalPosition) <= BigfootData.Music.RadiusM) near = true;
        Sfx.FightMusic(near, BigfootData.Music.File, BigfootData.Music.VolumeDb);
        RenderRings();
    }

    const double HitReactionGapSeconds = 4;

    BodyFlingTracker FlingOf(ushort id)
    {
        if (!_flings.TryGetValue(id, out var f)) _flings[id] = f = new BodyFlingTracker();
        return f;
    }

    /// <summary>
    /// What the giant's act says of the player in its hand: Hold grabs (<paramref name="owner"/> is the player), and when the Hold is over a Launch throws
    /// them while anything else (the hold cut short, the giant dead or gone) lets them drop.
    /// </summary>
    void TrackHold(BigfootView v, BigfootAct act, ushort owner)
    {
        if (act == BigfootAct.Hold && owner != 0)
        {
            v.HeldId = owner; _holders[owner] = v;
            FlingOf(owner).Grabbed();
            return;
        }
        if (v.HeldId == 0) return;
        var id = v.HeldId;
        v.HeldId = 0; _holders.Remove(id);
        if (act == BigfootAct.Launch) FlingOf(id).Flung(); else FlingOf(id).Ended();
    }

    /// <summary>A killed, respawned or departed player is no longer a ragdoll of this machine (a corpse is its own entity).</summary>
    void EndFling(ushort id)
    {
        if (_flings.TryGetValue(id, out var f) && f.Active) { f.Ended(); _holders.Remove(id); }
    }

    /// <summary>The own body only needs its phase (the eye and the hidden arms): the landing is read from the predicted body's speed.</summary>
    void TickOwnFling()
    {
        if (_myId == 0 || _me == null || !_flings.TryGetValue(_myId, out var f) || !f.Active) { if (_me != null) _ownFlingLast = _me.GlobalPosition; return; }
        if (Dead) { f.Ended(); return; }
        var dt = (float)GetProcessDeltaTime();
        var at = _me.GlobalPosition;
        f.Tick(dt, dt > 0 ? (at - _ownFlingLast).Length() / dt : 0, _me.State.OnFloor);
        _ownFlingLast = at;
        if (f.Phase == BodyFling.Landing && f.Elapsed >= GetUpSeconds) f.StoodUp();
    }

    /// <summary>How long a player who landed alive stays a ragdoll turning upright (the tumble's own settle is quicker; this ends it for the own body, which is not drawn).</summary>
    const float GetUpSeconds = 0.4f;

    /// <summary>
    /// Another player the Bigfoot holds or has thrown (#360): a ragdoll the whole time. In the fist it hangs in the hand's shake (the same the held player's
    /// own eye rides) with its limbs flailing; thrown it tumbles about its middle (<see cref="RagdollTumble"/>) until it lands, then turns upright and goes
    /// slack, and the normal figure takes over without a pop (a turn is a whole circle, the pivot height at upright is the body's middle).
    /// </summary>
    void FlingRemote(ushort id, RemotePlayer r, BodyFlingTracker f, Vector3 at, float yaw, float dt)
    {
        var t = r.Tumble;
        var speed = dt > 0 ? (at - r.TumbleLast).Length() / dt : 0;
        r.TumbleLast = at;
        f.Tick(dt, speed, Map is not { } map || at.Y - map.GroundHeight(at.X, at.Z) < 0.35f);
        t.Thrown = true; t.StandUp = f.Phase == BodyFling.Landing; // lying on the ground first, then it turns upright // the tumble owns the ragdoll flag: when the machine is over, TumbleRemote's normal path puts the figure back
        var middle = Vector3.Up * RagdollTumble.Half;
        if (f.Phase == BodyFling.Held && _holders.TryGetValue(id, out var h))
        {
            var local = h.Node.ToLocal(at);
            var tm = (float)(Now - h.Since);
            var hand = h.Node.GlobalBasis;
            var shaken = h.Pivot.Transform.Basis * local + new Vector3(0, 0.45f * MathF.Sin(tm * 22f), 0.3f * MathF.Sin(tm * 11f + 1.3f));
            var tilt = h.Pivot.Rotation.Z * 1.6f + 0.1f * MathF.Sin(tm * 17f);
            t.Amount = 1f; t.Spin = 0;
            r.View?.SetRagdoll(true, 1f, false);
            if (r.View != null) r.View.Position = -middle;
            r.Node.GlobalPosition = at + hand * (shaken - local) + middle;
            r.Node.Basis = new Basis(hand.Z.Normalized(), tilt) * new Basis(hand.X.Normalized(), h.Pivot.Rotation.X * 2f) * new Basis(Vector3.Up, yaw);
            return;
        }
        t.Update(at, f.Phase == BodyFling.Flying, dt);
        if (f.Phase == BodyFling.Landing && t.AtRest) f.StoodUp();
        r.View?.SetRagdoll(true, t.Amount, t.AtRest);
        if (r.View != null) r.View.Position = -middle;
        r.Node.GlobalPosition = at + Vector3.Up * t.PivotHeight;
        r.Node.Basis = t.BasisFor(yaw);
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
    void MeasureSpeed(BigfootView v)
    {
        var at = v.Node.GlobalPosition;
        var dt = (float)GetProcessDeltaTime();
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
        BigfootAct.TrunkWindup => (BigfootRules.ClipTrunkThrow, BigfootData.Trunk.WindupSeconds, 0.12),
        BigfootAct.StompWindup => (BigfootRules.ClipStompWindup, BigfootData.Stomp.WindupSeconds, 0.12),
        BigfootAct.VehicleWindup => (BigfootRules.ClipVehicleLift, BigfootData.VehicleThrow.WindupSeconds, 0.12),
        BigfootAct.GrabReach => (BigfootRules.ClipGrabReach, BigfootData.Grab.ReachSeconds, 0.1),
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
        var natural = (walking ? BigfootData.Movement.WalkStrideM : BigfootData.Movement.RunStrideM) / (float)v.Anim.GetAnimation(clip).Length; // m/s at speed scale 1
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
            v.HeldLog = MakeLog();
            v.Pivot.AddChild(v.HeldLog);
        }
        if (v.HeldLog != null)
        {
            v.HeldLog.Visible = winding;
            if (winding)
            {
                var k = Math.Clamp(t / BigfootData.Trunk.WindupSeconds, 0f, 1f);
                var hand = v.Skel != null && v.HandR >= 0
                    ? v.Pivot.GlobalTransform.AffineInverse() * (v.Skel.GlobalTransform * v.Skel.GetBoneGlobalPose(v.HandR).Origin)
                    : new Vector3(0.85f, BigfootData.HeightM * 0.9f, 0.3f);
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

    void RenderRings()
    {
        for (var i = _waveRings.Count - 1; i >= 0; i--)
        {
            var (node, _, born) = _waveRings[i];
            var radius = (float)(Now - born) * BigfootData.Stomp.WaveSpeedMps;
            if (radius >= BigfootData.Stomp.WaveMaxRadiusM) { node.QueueFree(); _waveRings.RemoveAt(i); continue; }
            _waveRings[i] = (node, radius, born);
            var r = MathF.Max(radius, 0.1f);
            // the wall rises in its first metres, runs out at full height and sinks as it dies away
            var life = radius / BigfootData.Stomp.WaveMaxRadiusM;
            var h = MathF.Max(0.05f, WaveWallM * MathF.Min(1f, r / 3f) * (1f - 0.6f * life * life));
            node.Scale = new Vector3(r, h, r);
            var p = node.GlobalPosition; p.Y = (float)node.GetMeta("ground_y") + h / 2f; node.GlobalPosition = p;
            if (node is MeshInstance3D { MaterialOverride: StandardMaterial3D m }) m.AlbedoColor = new Color(0.86f, 0.74f, 0.52f, 0.8f * (1f - life * life));
        }
    }

    // ---- events ----

    /// <summary>The Bigfoot's one-shot effects and its trail. Called for every event the client receives.</summary>
    void OnBigfootEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.BigfootFx when BigfootFxCodec.Decode(payload) is { } m:
                OnBigfootFx(m);
                break;
            case EventKind.BigfootTrail when BigfootTrail.TryDecode(payload, out var prints):
                ShowTrail(prints);
                break;
        }
    }

    void OnBigfootFx(BigfootFxMsg m)
    {
        var at = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        switch (m.Kind)
        {
            case BigfootFxKind.Appear: Sfx.Play3D(BigfootSounds.Roar, at, 4f); Ctx.Shake.At(at, 25); RoarAt(at); break;
            case BigfootFxKind.Roar: Sfx.Play3D(BigfootSounds.Roar, at, 6f); Ctx.Shake.At(at, 30); RoarAt(at); break;
            case BigfootFxKind.Death: Sfx.Play3D(BigfootSounds.Roar, at, 6f); Ctx.Shake.At(at, 30); break;
            case BigfootFxKind.StompWindup: Sfx.Play3D(BigfootSounds.StompWindup, at, 6f); break; // the telegraph: a sound, and the pose
            case BigfootFxKind.Shockwave:
                Sfx.Play3D(BigfootSounds.Shockwave, at, 2f);
                Ctx.Shake.At(at, BigfootData.Stomp.WaveMaxRadiusM);
                AddRing(at);
                break;
            case BigfootFxKind.TrunkThrow: Sfx.Play3D(BigfootSounds.Throw, at); break;
            case BigfootFxKind.VehicleThrow:
                _thrownCars[m.Id] = Now + m.Extra / 10.0 + 0.5; // the driver's prediction waits for the flight, and a moment after the landing
                Sfx.Play3D(BigfootSounds.Roar, at, 2f);
                break;
            case BigfootFxKind.VehicleLand:
                _thrownCars[m.Id] = Now + 0.3;
                Sfx.Play3D(BigfootSounds.Land, at, -2f);
                Ctx.Shake.At(at, 18);
                break;
            case BigfootFxKind.Grab: Sfx.Play3D(BigfootSounds.Grab, at); break;
            case BigfootFxKind.Launch: Sfx.Play3D(BigfootSounds.Throw, at, 4f); break;
        }
    }

    /// <summary>
    /// The Bigfoot standing at a roar's place plays the Roar clip: its arrival, and a phase that began while it stood. In a fight the server's Roar act
    /// (<see cref="BigfootAct.Roar"/>) stops it and plays the same clip; a giant on the move is left alone here, it would slide.
    /// </summary>
    void RoarAt(Vector3 at)
    {
        foreach (var v in _bigfoots.Values)
            if ((BigfootAct)v.Drawn.Last == BigfootAct.Idle && v.Node.GlobalPosition.DistanceTo(at) < 4f) PlayOver(v, BigfootRules.ClipRoar, 0.15, Now);
    }

    /// <summary>The height of the shockwave's wall of dust at full size (the wave itself is hit below <see cref="BigfootStomp.WaveHeightM"/>: a jump clears it).</summary>
    const float WaveWallM = 1.5f;

    void AddRing(Vector3 at)
    {
        if (!ShotFx.Enabled) return;
        // a standing wall of dust, dense at the ground and thinning to nothing at its top, with a second, lower one just inside it for depth
        var fade = new GradientTexture2D
        {
            Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 0), new Color(1, 1, 1, 0.55f), new Color(1, 1, 1, 1) }, Offsets = new[] { 0f, 0.6f, 1f } },
            Fill = GradientTexture2D.FillEnum.Linear, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 0.5f), // a cylinder's side uses the upper half of the texture
            Width = 4, Height = 64,
        };
        var dust = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.86f, 0.74f, 0.52f, 0.8f), AlbedoTexture = fade, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var tube = new CylinderMesh { TopRadius = 1.04f, BottomRadius = 1f, Height = 1f, RadialSegments = 48, Rings = 1, CapTop = false, CapBottom = false };
        var ring = new MeshInstance3D { Mesh = tube, MaterialOverride = dust, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Scale = new Vector3(0.1f, 0.05f, 0.1f) };
        ring.AddChild(new MeshInstance3D
        {
            Mesh = tube, MaterialOverride = dust, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = new Vector3(0.93f, 0.55f, 0.93f), Position = new Vector3(0, -0.225f, 0), // its foot on the same ground
        });
        Owned.AddChild(ring);
        ring.GlobalPosition = at;
        ring.SetMeta("ground_y", at.Y);
        _waveRings.Add((ring, 0, Now));
    }

    /// <summary>The footprints: one dark print each, flat on the ground, until the next trail replaces them (an empty one clears them).</summary>
    void ShowTrail(List<Footprint> prints)
    {
        foreach (var n in _prints) n.QueueFree();
        _prints.Clear();
        if (!ShotFx.Enabled || prints.Count == 0) return;
        var mesh = new PlaneMesh { Size = new Vector2(0.62f, 1.35f) };
        var material = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.05f, 0.035f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        foreach (var p in prints)
        {
            var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Owned.AddChild(node);
            node.GlobalPosition = new Vector3(p.Position.X, p.Position.Y + 0.04f, p.Position.Z);
            node.Rotation = new Vector3(0, p.Yaw, 0);
            _prints.Add(node);
        }
    }

    // ---- the giant's corpse ----

    /// <summary>True for the corpse the Bigfoot left: a giant lying in its death pose, not a character.</summary>
    bool IsGiantCorpse(ushort corpse) => WorldEntities.Entities.TryGetValue(corpse, out var e) && e.State.Owner == BigfootRules.TargetId;

    /// <summary>Builds the corpse view of the giant: the model at its feet, the Death clip held at its last frame.</summary>
    CorpseView MakeGiantCorpse(WorldReplica.Entity e)
    {
        var node = new Node3D { Name = $"Corpse{e.Id}" };
        var model = MakeGiant(out var anim);
        node.AddChild(model);
        Owned.AddChild(node); // in the tree first: an AnimationPlayer outside it poses nothing, and the giant would stand in its rest pose
        if (anim != null)
        {
            var clips = new Dictionary<string, string>();
            IndexClips(clips, anim);
            if (clips.TryGetValue(BigfootRules.ClipDeath, out var death))
            {
                anim.Play(death);
                anim.Seek(Math.Max(0, anim.GetAnimation(death).Length - 0.01), true); // just short of the end: it lies as it fell
                anim.Pause();
            }
        }
        return new CorpseView { Node = node, View = null, Giant = true };
    }

    /// <summary>The giant's corpse with or without its head: the head is the Item it carries, so once that is taken the body lies there headless.</summary>
    void ShowGiantHead(ushort corpse)
    {
        if (!_corpses.TryGetValue(corpse, out var v) || !v.Giant) return;
        if (v.Node.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D sk) return;
        var head = sk.FindBone("Head");
        if (head < 0) return;
        sk.SetBonePoseScale(head, CorpseItems(corpse).Count > 0 ? Vector3.One : Vector3.One * 0.001f); // the clip is paused: the pose stays as set
    }
}
