using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Bigfoot on the client (issue #264): the world entity of type <see cref="BigfootRules.TypeId"/> drawn as the placeholder giant (bigfoot.json), the logs it
/// throws (type <see cref="BigfootRules.TrunkTypeId"/>), its one-shot effects (<see cref="EventKind.BigfootFx"/>: the shockwave ring, the sounds, the shake), the
/// footprints that lead to its corpse (<see cref="EventKind.BigfootTrail"/>) and the giant's corpse itself (a corpse owned by <see cref="BigfootRules.TargetId"/>).
/// The model has seven clips (Idle, Walk, Run, Attack, HitRecieve, Death, Jump); the stomp, the throws and the grab reuse the one Attack clip with a procedural
/// lean, rise and wobble of the whole figure, until the real animated model arrives (the swap is this file and bigfoot.json, <see cref="BigfootView"/>).
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
        public const string Roar = "explosion.vehicle", StompWindup = "land", Shockwave = "explosion.bazooka", Throw = "shot.knife", Land = "explosion.vehicle", Log = "impact", Grab = "death";
    }

    /// <summary>The figure of one Bigfoot: the node at its feet, the pivot the procedural motion turns and scales, and the model with its clips.</summary>
    sealed class BigfootView
    {
        public Node3D Node = null!, Pivot = null!;
        public AnimationPlayer? Anim;
        /// <summary>The clip name the model uses for each plain clip name ("Attack" is "EnemyArmature|EnemyArmature|EnemyArmature|Attack").</summary>
        public readonly Dictionary<string, string> Clips = new();
        public BigfootAct Shown = (BigfootAct)255;
        public bool Toggle;
        /// <summary>Client time (s) the shown act began, and until which a hit reaction holds the act's clip back.</summary>
        public double Since, HitUntil;
        /// <summary>The skeleton and its two arm bones (-1 without them), with each arm's pose at rest and raised straight up: the vehicle lift poses them by hand.</summary>
        public Skeleton3D? Skel;
        public int ArmL = -1, ArmR = -1;
        public Quaternion ArmLRest, ArmRRest, ArmLUp, ArmRUp;
        public bool ArmsRaised;
        /// <summary>The log on its shoulder while it winds a throw up (the thrown one is a world entity of its own).</summary>
        public Node3D? HeldLog;
    }

    /// <summary>Finds the arm bones of the giant and works out the pose that points each straight up, from the skeleton's rest (no clip is read).</summary>
    static void IndexArms(BigfootView v)
    {
        if (v.Node.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D sk) return;
        v.Skel = sk;
        var up = sk.GlobalBasis.Orthonormalized().Inverse() * Vector3.Up; // "up" in the skeleton's own space
        (int Bone, Quaternion Hang, Quaternion Raised) Arm(string side)
        {
            var arm = sk.FindBone("Arm." + side); if (arm < 0) arm = sk.FindBone("Arm_" + side);
            if (arm < 0) return (-1, Quaternion.Identity, Quaternion.Identity);
            var kids = sk.GetBoneChildren(arm);
            var rest = sk.GetBoneGlobalRest(arm);
            var dir = kids.Length > 0 ? sk.GetBoneGlobalRest(kids[0]).Origin - rest.Origin : rest.Basis.Y;
            if (dir.LengthSquared() < 1e-8f) return (-1, Quaternion.Identity, Quaternion.Identity);
            var raised = new Basis(new Quaternion(dir.Normalized(), up.Normalized())) * rest.Basis.Orthonormalized();
            var parent = sk.GetBoneParent(arm);
            var parentBasis = parent >= 0 ? sk.GetBoneGlobalRest(parent).Basis.Orthonormalized() : Basis.Identity;
            return (arm, sk.GetBoneRest(arm).Basis.Orthonormalized().GetRotationQuaternion(), (parentBasis.Inverse() * raised).GetRotationQuaternion());
        }
        (v.ArmL, v.ArmLRest, v.ArmLUp) = Arm("L");
        (v.ArmR, v.ArmRRest, v.ArmRUp) = Arm("R");
    }

    /// <summary>Both arms between hanging (0) and straight up over the head (1). The clip must be paused, or it poses them again the next frame.</summary>
    static void RaiseArms(BigfootView v, float k)
    {
        if (v.Skel == null) return;
        if (v.ArmL >= 0) v.Skel.SetBonePoseRotation(v.ArmL, v.ArmLRest.Slerp(v.ArmLUp, k));
        if (v.ArmR >= 0) v.Skel.SetBonePoseRotation(v.ArmR, v.ArmRRest.Slerp(v.ArmRUp, k));
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
        var v = new BigfootView { Node = new Node3D { Name = $"Bigfoot{e.Id}" }, Pivot = new Node3D { Name = "Pivot" }, Toggle = BigfootRules.HitToggled(e.State.State) };
        v.Node.AddChild(v.Pivot);
        var model = MakeGiant(out var anim);
        v.Pivot.AddChild(model);
        v.Anim = anim;
        IndexClips(v.Clips, anim);
        Owned.AddChild(v.Node);
        IndexArms(v);
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
                {
                    var loops = name.EndsWith(BigfootRules.ClipIdle) || name.EndsWith(BigfootRules.ClipWalk) || name.EndsWith(BigfootRules.ClipRun) || name.EndsWith(BigfootRules.ClipAttack);
                    anim.GetAnimation(name).LoopMode = loops ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
                }
            }
            return model;
        }
        var h = BigfootData.HeightM;
        var box = new Node3D();
        box.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(h * 0.5f, h, h * 0.35f) }, Position = new Vector3(0, h / 2, 0) });
        return box;
    }

    /// <summary>Maps the plain clip names to the model's (it names them with a prefix: "EnemyArmature|EnemyArmature|EnemyArmature|Idle").</summary>
    static void IndexClips(Dictionary<string, string> clips, AnimationPlayer? anim)
    {
        if (anim == null) return;
        foreach (var plain in BigfootRules.Clips)
            foreach (var name in anim.GetAnimationList())
                if (name == plain || name.EndsWith("|" + plain)) { clips[plain] = name; break; }
    }

    void OnBigfootDespawned(ushort id, WorldDespawnReason why)
    {
        if (_bigfoots.Remove(id, out var v)) v.Node.QueueFree();
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
        var held = false;
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
                    v.Node.Rotation = new Vector3(0, s.Yaw, 0);
                }
                var raw = e.State.State;
                var act = BigfootRules.ActOf(raw);
                if (_myId != 0 && e.State.Owner == _myId && act == BigfootAct.Hold) held = true;
                var toggle = BigfootRules.HitToggled(raw);
                if (toggle != v.Toggle)
                {
                    v.Toggle = toggle;
                    // a flinch at most every few seconds: under steady fire it would otherwise restart on every hit and never let the act's clip play
                    if (act is BigfootAct.Idle or BigfootAct.Walk or BigfootAct.Run && now >= v.HitUntil + HitReactionGapSeconds) PlayBigfootHit(v, now);
                }
                if (now >= v.HitUntil && v.Shown != act) ShowBigfootAct(v, act, now);
                MoveBigfoot(v, act, BigfootRules.IsEnraged(raw), now);
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
        // its music plays while a living one is near the player (the file is private: without it there is none)
        var near = false;
        if (_me != null)
            foreach (var v in _bigfoots.Values)
                if (v.Shown != BigfootAct.Dead && v.Node.GlobalPosition.DistanceTo(_me.GlobalPosition) <= BigfootData.Music.RadiusM) near = true;
        Sfx.FightMusic(near, BigfootData.Music.File, BigfootData.Music.VolumeDb);
        RenderRings();
    }

    const double HitReactionGapSeconds = 4;

    void PlayBigfootHit(BigfootView v, double now)
    {
        if (v.Anim == null || !v.Clips.TryGetValue(BigfootRules.ClipHit, out var clip)) return;
        v.Anim.SpeedScale = 1;
        v.Anim.Play(clip, 0.05);
        v.HitUntil = now + Math.Min(0.45, v.Anim.GetAnimation(clip).Length);
        v.Shown = (BigfootAct)254; // the act's clip is due again when the reaction is over
    }

    /// <summary>Picks the clip of an act. The windups stretch the Attack clip over the telegraph's time, so its blow lands when the server's does.</summary>
    void ShowBigfootAct(BigfootView v, BigfootAct act, double now)
    {
        var first = v.Shown == (BigfootAct)255;
        v.Shown = act; v.Since = now;
        if (v.Anim == null) return;
        string plain; float seconds = 0; var blend = 0.2;
        if (v.ArmsRaised) { v.ArmsRaised = false; v.Anim.Play(); } // the clips pose the arms again
        if (act == BigfootAct.VehicleWindup && v.Skel != null && v.ArmL >= 0 && v.ArmR >= 0)
        {
            // the lift is posed by hand (MoveBigfoot): it stands in its idle pose, held still, and the arms go up with the vehicle
            if (v.Clips.TryGetValue(BigfootRules.ClipIdle, out var idle)) { v.Anim.SpeedScale = 1; v.Anim.Play(idle, 0.1); v.Anim.Seek(0, true); v.Anim.Pause(); }
            v.ArmsRaised = true;
            return;
        }
        switch (act)
        {
            case BigfootAct.Walk: plain = BigfootRules.ClipWalk; break;
            case BigfootAct.Run: plain = BigfootRules.ClipRun; break;
            case BigfootAct.TrunkWindup: plain = BigfootRules.ClipAttack; seconds = BigfootData.Trunk.WindupSeconds; blend = 0.1; break;
            case BigfootAct.StompWindup: plain = BigfootRules.ClipAttack; seconds = BigfootData.Stomp.WindupSeconds; blend = 0.1; break;
            case BigfootAct.VehicleWindup: plain = BigfootRules.ClipAttack; seconds = BigfootData.VehicleThrow.WindupSeconds; blend = 0.1; break;
            case BigfootAct.GrabReach: plain = BigfootRules.ClipAttack; seconds = BigfootData.Grab.ReachSeconds; blend = 0.1; break;
            case BigfootAct.Stomp: case BigfootAct.Launch: plain = BigfootRules.ClipAttack; seconds = 0.5f; blend = 0.05; break;
            case BigfootAct.Hold: plain = BigfootRules.ClipAttack; seconds = 0.8f; break; // flails: the clip loops fast
            case BigfootAct.Dead: plain = BigfootRules.ClipDeath; blend = 0.1; break;
            default: plain = BigfootRules.ClipIdle; break;
        }
        if (!v.Clips.TryGetValue(plain, out var clip)) return;
        var length = v.Anim.GetAnimation(clip).Length;
        v.Anim.SpeedScale = seconds > 0 && length > 0.05 ? (float)(length / seconds) : 1f;
        v.Anim.Play(clip, blend);
        // a body that was already dead when we first saw it lies in its last pose; one that dies in front of us falls
        if (act == BigfootAct.Dead && first) v.Anim.Seek(length, true);
    }

    /// <summary>The procedural motion on top of the clip: the whole figure leans, rises, squashes and wobbles about its feet, and breathes harder enraged.</summary>
    void MoveBigfoot(BigfootView v, BigfootAct act, bool enraged, double now)
    {
        var t = (float)(now - v.Since);
        float pitch = 0, roll = 0, rise = 1;
        switch (act)
        {
            case BigfootAct.TrunkWindup: // leans back to wind up, then forward at the release
                pitch = MathF.Min(1f, t / BigfootData.Trunk.WindupSeconds) < 0.75f ? -0.28f * MathF.Min(1f, t / (BigfootData.Trunk.WindupSeconds * 0.75f)) : 0.22f;
                break;
            case BigfootAct.StompWindup: // rises up on its toes
                rise = 1f + 0.14f * MathF.Min(1f, t / BigfootData.Stomp.WindupSeconds); pitch = -0.1f * MathF.Min(1f, t / BigfootData.Stomp.WindupSeconds);
                break;
            case BigfootAct.Stomp: rise = 0.88f + 0.12f * MathF.Min(1f, t / 0.5f); pitch = 0.12f; break; // comes down hard
            case BigfootAct.VehicleWindup:
            {
                // bends down to the vehicle, takes it in both hands, straightens with it and holds it over its head, leaning back for the throw
                var k = Math.Clamp(t / BigfootData.VehicleThrow.WindupSeconds, 0f, 1f);
                var up = Math.Clamp((k - 0.2f) / 0.5f, 0f, 1f); up = up * up * (3 - 2 * up);
                pitch = k < 0.2f ? 0.35f * (k / 0.2f) : 0.35f + (-0.12f - 0.35f) * up;
                rise = 1f + 0.06f * up;
                if (v.ArmsRaised) RaiseArms(v, 0.35f + 0.65f * up); // reaching forward already as it bends
                break;
            }
            case BigfootAct.GrabReach: pitch = 0.35f * MathF.Min(1f, t / BigfootData.Grab.ReachSeconds); break;
            case BigfootAct.Hold: pitch = -0.12f; roll = MathF.Sin(t * 11f) * 0.16f; rise = 1f + 0.03f * MathF.Sin(t * 22f); break; // flails with the player in its hand
            case BigfootAct.Launch: pitch = 0.3f; break;
        }
        // the log it is about to throw: up on its right shoulder, drawn back, gone at the release (the flying one takes over)
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
                var h = BigfootData.HeightM;
                v.HeldLog.Position = new Vector3(0.85f, h * (0.55f + 0.42f * MathF.Min(1f, k * 2f)), 0.3f + 0.5f * k); // lifted from the hip to over the shoulder, then back
                v.HeldLog.Rotation = new Vector3(-0.35f * k, 0, 0); // its front end tips up
            }
        }
        if (enraged) rise *= 1f + 0.025f * MathF.Sin((float)now * 13f);
        v.Pivot.Rotation = new Vector3(pitch, 0, roll);
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
            case BigfootFxKind.Appear: Sfx.Play3D(BigfootSounds.Roar, at, 4f); ShakeFor(at, 25); break;
            case BigfootFxKind.Roar: Sfx.Play3D(BigfootSounds.Roar, at, 6f); ShakeFor(at, 30); break;
            case BigfootFxKind.Death: Sfx.Play3D(BigfootSounds.Roar, at, 6f); ShakeFor(at, 30); break;
            case BigfootFxKind.StompWindup: Sfx.Play3D(BigfootSounds.StompWindup, at, 6f); break; // the telegraph: a sound, and the pose
            case BigfootFxKind.Shockwave:
                Sfx.Play3D(BigfootSounds.Shockwave, at, 2f);
                ShakeFor(at, BigfootData.Stomp.WaveMaxRadiusM);
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
                ShakeFor(at, 18);
                break;
            case BigfootFxKind.Grab: Sfx.Play3D(BigfootSounds.Grab, at); break;
            case BigfootFxKind.Launch: Sfx.Play3D(BigfootSounds.Throw, at, 4f); break;
        }
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
