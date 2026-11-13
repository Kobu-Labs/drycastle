using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Animals on the client (#254): the world entities of type <see cref="AnimalRules.TypeId"/>. Each is drawn as its species'
/// model (animals.json) and plays the clip of the state the server sends: idle, eating, walking, galloping, a hit reaction when
/// a hit that did not kill lands, and the death clip, whose last frame holds while the body lies. Without a model (headless,
/// or the file is missing) a box of the species' size stands in. The server owns the animal; the client only shows it.
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
        WorldEntities.Despawned += OnAnimalDespawned;
    }

    void OnAnimalSpawned(WorldReplica.Entity e)
    {
        if (e.Type != AnimalRules.TypeId) return;
        var species = e.State.Owner;
        if (species >= AnimalData.Animals.Count)
        {
            GD.PrintErr($"client: animal {e.Id} has species {species}, animals.json has {AnimalData.Animals.Count}; not drawn (data files differ from the server's?)");
            return;
        }
        var def = AnimalData.Animals[species];
        var node = new Node3D { Name = $"Animal{e.Id}" };
        AnimationPlayer? anim = null;
        if (ModelLib.Instance(def.Model) is { } model)
        {
            model.Scale = Vector3.One * def.Scale;
            model.RotationDegrees = new Vector3(0, def.ModelYawDeg, 0);
            node.AddChild(model);
            anim = model.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
            if (anim != null)
            {
                foreach (var n in new[] { AnimalRules.ClipIdle, AnimalRules.ClipIdleAlt, AnimalRules.ClipEat, AnimalRules.ClipWalk, AnimalRules.ClipRun })
                    if (anim.HasAnimation(n)) anim.GetAnimation(n).LoopMode = Animation.LoopModeEnum.Linear;
                foreach (var n in new[] { AnimalRules.ClipDeath, AnimalRules.ClipHitLeft, AnimalRules.ClipHitRight })
                    if (anim.HasAnimation(n)) anim.GetAnimation(n).LoopMode = Animation.LoopModeEnum.None;
            }
        }
        else
        {
            var (w, h, l) = (def.HitBox[0], def.HitBox[1], def.HitBox[2]);
            node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, h, l) }, Position = new Vector3(0, h / 2, 0) });
        }
        Owned.AddChild(node);
        _animals[e.Id] = new AnimalView { Node = node, Anim = anim, Def = def, Toggle = AnimalRules.HitToggled(e.State.State) };
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
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
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
        var clip = v.HitLeft ? AnimalRules.ClipHitLeft : AnimalRules.ClipHitRight;
        v.HitLeft = !v.HitLeft;
        if (v.Anim == null || !v.Anim.HasAnimation(clip)) return;
        v.Anim.Play(clip, 0.05);
        v.HitUntil = now + Math.Min(v.Anim.GetAnimation(clip).Length, AnimalHitReactMaxSeconds);
        v.Shown = 254; // the state's clip is due again when the reaction is over
    }

    static void ShowAnimalState(AnimalView v, AnimalState state)
    {
        var first = v.Shown == 255;
        v.Shown = (byte)state;
        if (v.Anim == null) return;
        var clip = state switch
        {
            AnimalState.Eat => AnimalRules.ClipEat,
            AnimalState.Walk => AnimalRules.ClipWalk,
            AnimalState.Flee => AnimalRules.ClipRun,
            AnimalState.Dead => AnimalRules.ClipDeath,
            _ => GD.Randf() < 0.25f ? AnimalRules.ClipIdleAlt : AnimalRules.ClipIdle,
        };
        if (!v.Anim.HasAnimation(clip)) return;
        v.Anim.Play(clip, state == AnimalState.Dead ? 0.1 : 0.2);
        // an animal that was already dead when we saw it lies in its final pose; one that dies in front of us falls
        if (state == AnimalState.Dead && first) v.Anim.Seek(v.Anim.GetAnimation(clip).Length, true);
    }
}
