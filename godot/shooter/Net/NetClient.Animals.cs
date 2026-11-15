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

    sealed class AnimalView
    {
        public Node3D Node = null!;
        public AnimationPlayer? Anim;
        public AnimalDef Def = null!;
        /// <summary>The model's name for each <see cref="AnimalClip"/> (indexed by it), null where it has none; empty without a model.</summary>
        public string?[] Clips = Array.Empty<string?>();
        /// <summary>The body box of the look: for the puff's size.</summary>
        public IReadOnlyList<float> HitBox = Array.Empty<float>();
        /// <summary>The state whose clip is showing; 255 = none yet.</summary>
        public byte Shown = 255;
        public bool Toggle;
        public bool HitLeft;
        /// <summary>Time (seconds, client clock) until which a hit reaction plays; the state's clip waits for it.</summary>
        public double HitUntil;
    }

    readonly Dictionary<ushort, AnimalView> _animals = new();

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

    /// <summary>An animal outside the interest range (ADR 0024) is hidden and its animation stops; one that comes back starts its clip afresh (its pose is from a keyframe, not from the last time we saw it).</summary>
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
        var box = def.HitBoxOf(variant);
        var node = new Node3D { Name = $"Animal{e.Id}" };
        AnimationPlayer? anim = null;
        var clips = Array.Empty<string?>();
        if (ModelLib.Instance(def.ModelOf(variant)) is { } model)
        {
            model.Scale = Vector3.One * def.Scale;
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
        var view = new AnimalView { Node = node, Anim = anim, Def = def, Clips = clips, HitBox = box, Toggle = AnimalRules.HitToggled(e.State.State) };
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
        foreach (var (id, v) in _animals)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant) continue;
            if (e.Buf.At(rt) is { } s)
            {
                v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                v.Node.Rotation = new Vector3(0, s.Yaw, 0);
            }
            var raw = e.State.State;
            var state = AnimalRules.StateOf(raw);
            var toggle = AnimalRules.HitToggled(raw);
            if (toggle != v.Toggle)
            {
                v.Toggle = toggle;
                if (state != AnimalState.Dead) PlayAnimalHit(v, now);
            }
            if (state == AnimalState.Dead) { if (v.Shown != (byte)state) ShowAnimalState(v, state); continue; }
            if (now < v.HitUntil) continue;
            if (v.Shown != (byte)state) ShowAnimalState(v, state); // also after a reaction: Shown is 254 until its clip is over
            v.HitUntil = 0;
        }
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
