using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Animals on the client (#254): the world entities of type <see cref="AnimalRules.TypeId"/>. Each is drawn as its species'
/// model (animals.json; looked up in the asset library, then the private packs) and plays the clip of the state the server sends: idle, eating,
/// walking, running, sitting (sit-down, hold, stand-up when it ends), barking once, a hit reaction when a hit that did not kill lands, and the
/// death clip, whose last frame holds while the body lies. The clips are found by <see cref="AnimalClips"/> (the pet packs and the animal pack name
/// them differently). A one-hit pet is simply despawned by the server (<see cref="WorldDespawnReason.Destroyed"/>) and leaves a puff. Without a model
/// (headless, or the file is missing) a box of the model's hit box stands in. The server owns the animal; the client only shows it.
/// </summary>
public partial class NetClient
{
    /// <summary>animals.json: set by the host (the species table; the species index of a state is a position in it).</summary>
    public AnimalsFile AnimalData = new();
    /// <summary>The hit shapes of the species: set by the host. The client only draws them (the hit diagram); the server tests bullets against them.</summary>
    public AnimalShapes AnimalShapes = new();

    sealed class AnimalView
    {
        public Node3D Node = null!;
        public AnimationPlayer? Anim;
        public AnimalDef Def = null!;
        /// <summary>The model's name for each <see cref="AnimalClip"/> (indexed by it), null where it has none; empty without a model.</summary>
        public string?[] Clips = Array.Empty<string?>();
        /// <summary>The body box of the look, times its size: for the puff's size, the tumble and the binoculars.</summary>
        public IReadOnlyList<float> HitBox = Array.Empty<float>();
        /// <summary>How much bigger than its model as measured it stands (the animal's size roll; 1 for a species with no weight).</summary>
        public float Size = 1f;
        /// <summary>The state whose clip is showing; 255 = none yet.</summary>
        public byte Shown = 255;
        public bool Toggle;
        public bool HitLeft;
        /// <summary>Time (seconds, client clock) until which a hit reaction plays; the state's clip waits for it.</summary>
        public double HitUntil;
        /// <summary>The ragdoll of a body a vehicle threw: it rolls about its own length (radians) while it flies and settles on a side where it lands.</summary>
        public float Roll;
        public bool Tumbling;
        public Vector3 LastFeet;
        public bool SeenFeet;
    }

    readonly Dictionary<ushort, AnimalView> _animals = new();

    /// <summary>A thrown animal's roll in flight and its settling onto a side (rad/s), and the speed (m/s) above which a dead body is taken to be flying.</summary>
    const float AnimalRollRate = 9f, AnimalSettleRate = 9f, AnimalFlyingSpeed = 1.5f;

    /// <summary>How long a hit reaction may hold back the clip of the state (a gallop must not wait for a long clip).</summary>
    const double AnimalHitReactMaxSeconds = 0.6;

    /// <summary>Called once from <see cref="_Ready"/>: registers the animal type and follows its entities.</summary>
    void InitAnimals()
    {
        AnimalRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnAnimalSpawned;
        WorldEntities.DespawnedFor += OnAnimalDespawnedFor;
        WorldEntities.Despawned += OnAnimalDespawned;
        WorldEntities.DormancyChanged += OnAnimalDormancy;
    }

    /// <summary>An animal outside the interest range (ADR 0025) is hidden and its animation stops; one that comes back starts its clip afresh (its pose is from a keyframe, not from the last time we saw it).</summary>
    void OnAnimalDormancy(WorldReplica.Entity e)
    {
        if (e.Type == AnimalRules.TypeId && _animals.TryGetValue(e.Id, out var v)) SetAnimalDormant(v, e);
    }

    static void SetAnimalDormant(AnimalView v, WorldReplica.Entity e)
    {
        v.Node.Visible = !e.Dormant;
        v.Node.ProcessMode = e.Dormant ? Node.ProcessModeEnum.Disabled : Node.ProcessModeEnum.Inherit; // an AnimationPlayer keeps playing while hidden otherwise
        if (e.Dormant) return;
        v.Shown = 255; v.HitUntil = 0; v.Toggle = AnimalRules.HitToggled(e.State.State);
    }

    void OnAnimalSpawned(WorldReplica.Entity e)
    {
        if (e.Type != AnimalRules.TypeId) return;
        var species = AnimalRules.SpeciesOf(e.State.Owner);
        if (species >= AnimalData.Animals.Count)
        {
            GD.PrintErr($"client: animal {e.Id} has species {species}, animals.json has {AnimalData.Animals.Count}; not drawn (data files differ from the server's?)");
            return;
        }
        var def = AnimalData.Animals[species];
        var variant = AnimalRules.VariantOf(e.State.Owner);
        // a species with a weight rolls a size at spawn (the state byte's bits 5-7); the model and every box of it are scaled by it
        var size = def.WeightKgOf(variant) != null ? AnimalRules.SizeOfStep(AnimalRules.SizeStepOf(e.State.State)) : 1f;
        var box = def.HitBoxOf(variant).Select(x => x * size).ToArray();
        var node = new Node3D { Name = $"Animal{e.Id}" };
        AnimationPlayer? anim = null;
        var clips = Array.Empty<string?>();
        if (ModelLib.Instance(def.ModelOf(variant)) is { } model)
        {
            model.Scale = Vector3.One * (def.Scale * size);
            model.RotationDegrees = new Vector3(0, def.ModelYawDeg, 0);
            node.AddChild(model);
            anim = model.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
            if (anim != null)
            {
                clips = AnimalClips.Resolve(n => anim.HasAnimation(n));
                for (var c = 0; c < clips.Length; c++)
                    if (clips[c] is { } name) anim.GetAnimation(name).LoopMode = AnimalClips.Loops((AnimalClip)c) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            }
        }
        else
        {
            var (w, h, l) = (box[0], box[1], box[2]);
            node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, h, l) }, Position = new Vector3(0, h / 2, 0) });
        }
        Owned.AddChild(node);
        var view = new AnimalView { Node = node, Anim = anim, Def = def, Clips = clips, HitBox = box, Size = size, Toggle = AnimalRules.HitToggled(e.State.State) };
        _animals[e.Id] = view;
        if (e.Dormant) SetAnimalDormant(view, e); // spawned outside the interest range: hidden until a keyframe shows it
    }

    /// <summary>A pet that died of a hit (it has no corpse) goes in a puff where it stood; an animal that expired or was removed just leaves. Runs before <see cref="OnAnimalDespawned"/> frees the node.</summary>
    void OnAnimalDespawnedFor(ushort id, WorldDespawnReason reason)
    {
        if (reason != WorldDespawnReason.Destroyed || !_animals.TryGetValue(id, out var v)) return;
        var height = v.HitBox.Count == 3 ? v.HitBox[1] : 0.5f;
        PetFx.Puff(Owned, v.Node.GlobalPosition + Vector3.Up * (height / 2), height);
    }

    void OnAnimalDespawned(ushort id)
    {
        if (!_animals.Remove(id, out var v)) return;
        v.Node.QueueFree();
    }

    /// <summary>Places the animals (interpolated, a little in the past like every remote body) and plays the clip of their state.</summary>
    void RenderAnimals()
    {
        if (_animals.Count == 0 || _lastTick == 0) return;
        var rt = RenderTickNow();
        var now = Time.GetTicksMsec() / 1000.0;
        var dt = (float)GetProcessDeltaTime();
        foreach (var (id, v) in _animals)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant) continue;
            var raw = e.State.State;
            var state = AnimalRules.StateOf(raw);
            if (e.Buf.At(rt) is { } s) PlaceAnimal(v, state, new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), s.Yaw, dt);
            var toggle = AnimalRules.HitToggled(raw);
            if (toggle != v.Toggle)
            {
                v.Toggle = toggle;
                if (state != AnimalState.Dead) PlayAnimalHit(v, now);
            }
            if (v.Tumbling) { ShowAnimalTumble(v, state); continue; } // thrown by a vehicle: no clip of the state, the body is turned whole
            if (state == AnimalState.Dead) { if (v.Shown != (byte)state) ShowAnimalState(v, state); continue; }
            if (now < v.HitUntil) continue;
            if (v.Shown != (byte)state) ShowAnimalState(v, state); // also after a reaction: Shown is 254 until its clip is over
            v.HitUntil = 0;
        }
    }

    /// <summary>
    /// Puts the animal at its feet. One a vehicle threw is a ragdoll (the scripted tumble of the thrown player bodies, for a four-legged one): while it
    /// flies (state Thrown, or a dead body still moving) it rolls about its own length, turning about its middle; landed (Down, or dead) it settles on the
    /// nearer side and lies on the ground. When it gets up it is upright again.
    /// </summary>
    void PlaceAnimal(AnimalView v, AnimalState state, Vector3 feet, float yaw, float dt)
    {
        var speed = v.SeenFeet && dt > 0 ? (feet - v.LastFeet).Length() / dt : 0f;
        v.LastFeet = feet; v.SeenFeet = true;
        var flying = state == AnimalState.Thrown || state == AnimalState.Dead && speed > AnimalFlyingSpeed;
        if (flying) v.Tumbling = true;
        else if (state is not (AnimalState.Down or AnimalState.Dead)) { v.Tumbling = false; v.Roll = 0; }
        if (!v.Tumbling)
        {
            v.Node.GlobalPosition = feet;
            v.Node.Rotation = new Vector3(0, yaw, 0);
            return;
        }
        if (flying) v.Roll += AnimalRollRate * dt;
        else v.Roll = Mathf.MoveToward(v.Roll, Mathf.Round((v.Roll - Mathf.Pi / 2) / Mathf.Pi) * Mathf.Pi + Mathf.Pi / 2, AnimalSettleRate * dt); // on a side, whichever is nearer
        float halfWidth = v.HitBox.Count == 3 ? v.HitBox[0] / 2 : 0.2f, halfHeight = v.HitBox.Count == 3 ? v.HitBox[1] / 2 : 0.4f;
        var upright = new Basis(Vector3.Up, yaw);
        var basis = new Basis(upright * Vector3.Forward, v.Roll) * upright;
        var middle = halfWidth + (halfHeight - halfWidth) * Mathf.Abs(Mathf.Cos(v.Roll)); // the middle's height: half the body upright, half its width on a side
        v.Node.GlobalTransform = new Transform3D(basis, feet + Vector3.Up * middle - basis * (Vector3.Up * halfHeight));
    }

    /// <summary>The clip of a thrown animal: its legs run in the air; landed, the pose it has is held (a dead one in a standing pose: the tumble, not the death clip, lays it down).</summary>
    static void ShowAnimalTumble(AnimalView v, AnimalState state)
    {
        var shown = (byte)(state == AnimalState.Thrown ? 200 : 201); // 200 in the air, 201 lying: never a state's own number, so its clip is due when the tumble is over
        if (v.Shown == shown) return;
        var was = v.Shown;
        v.Shown = shown;
        v.HitUntil = 0;
        if (v.Anim == null || v.Clips.Length == 0) return;
        if (shown == 200) { if (v.Clips[(int)AnimalClip.Run] is { } run) v.Anim.Play(run, 0.05); return; }
        if (was != 200 && v.Clips[(int)AnimalClip.Idle] is { } idle) { v.Anim.Play(idle, 0.05); v.Anim.Seek(0, true); } // killed by the hit: the death clip had begun
        v.Anim.Pause();
    }

    void PlayAnimalHit(AnimalView v, double now)
    {
        var clip = v.Clips.Length == 0 ? null : v.Clips[(int)(v.HitLeft ? AnimalClip.HitLeft : AnimalClip.HitRight)];
        v.HitLeft = !v.HitLeft;
        if (v.Anim == null || clip == null) return;
        v.Anim.Play(clip, 0.05);
        v.HitUntil = now + Math.Min(v.Anim.GetAnimation(clip).Length, AnimalHitReactMaxSeconds);
        v.Shown = 254; // the state's clip is due again when the reaction is over
    }

    static void ShowAnimalState(AnimalView v, AnimalState state)
    {
        var was = v.Shown;
        var first = was == 255;
        v.Shown = (byte)state;
        if (v.Anim == null || v.Clips.Length == 0) return;
        string? Clip(AnimalClip c) => v.Clips[(int)c];
        if (state == AnimalState.Carried)
        {
            // held under a Thief's arm: no clip of its own, the standing pose frozen
            if (Clip(AnimalClip.Idle) is { } held) { v.Anim.Play(held, 0.1); v.Anim.Seek(0, true); v.Anim.Pause(); }
            return;
        }
        if (state == AnimalState.Sit)
        {
            // sits down, then holds the sat pose; one that was already sitting when we saw it just sits
            if (Clip(AnimalClip.SitIdle) is not { } hold) return;
            if (!first && Clip(AnimalClip.SitStart) is { } down) { v.Anim.Play(down, 0.2); v.Anim.Queue(hold); }
            else v.Anim.Play(hold, 0.2);
            return;
        }
        var clip = state switch
        {
            AnimalState.Eat => Clip(AnimalClip.Eat),
            AnimalState.Walk => Clip(AnimalClip.Walk),
            AnimalState.Flee => Clip(AnimalClip.Run),
            AnimalState.Dead => Clip(AnimalClip.Death),
            AnimalState.Bark => Clip(AnimalClip.Bark),
            _ => Clip(GD.Randf() < 0.25f ? AnimalClip.IdleAlt : AnimalClip.Idle),
        };
        if (clip == null) return;
        if (was == (byte)AnimalState.Sit && state == AnimalState.Idle && Clip(AnimalClip.SitUp) is { } up)
        {
            // the sit ended: it stands up, then stands (the server keeps it still for at least the idle time)
            v.Anim.Play(up, 0.2);
            v.Anim.Queue(clip);
            return;
        }
        v.Anim.Play(clip, state == AnimalState.Dead ? 0.1 : 0.2);
        // an animal that was already dead when we saw it lies in its final pose; one that dies in front of us falls
        if (state == AnimalState.Dead && first) v.Anim.Seek(v.Anim.GetAnimation(clip).Length, true);
    }
}
