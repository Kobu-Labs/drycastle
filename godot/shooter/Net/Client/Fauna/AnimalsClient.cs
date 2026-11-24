using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Wildlife;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Fauna;

/// <summary>
/// Animals on the client (#254): the world entities of type <see cref="AnimalRules.TypeId"/>. Each is drawn as its species'
/// model (animals.json; looked up in the asset library, then the private packs) and plays the clip of the state the server sends: idle, eating,
/// walking, running, sitting (sit-down, hold, stand-up when it ends), barking once, a hit reaction when a hit that did not kill lands, and the
/// death clip, whose last frame holds while the body lies. The clips are found by <see cref="AnimalClips"/> (the pet packs and the animal pack name
/// them differently). A one-hit pet is simply despawned by the server (<see cref="WorldDespawnReason.Destroyed"/>) and leaves a puff. Without a model
/// (headless, or the file is missing) a box of the model's hit box stands in. The server owns the animal; the client only shows it.
/// </summary>
internal sealed class AnimalsClient : IStandingBodies
{
    readonly ClientContext _ctx;
    readonly AnimalsFile _data;
    readonly AnimalShapes _shapes;
    readonly WildlifeFile _wildlife;
    readonly DriveClient _drive;

    /// <param name="data">animals.json (the species table; the species index of a state is a position in it).</param>
    /// <param name="shapes">The hit shapes of the species: the client only draws them (the hit diagram); the server tests bullets against them.</param>
    /// <param name="wildlife">wildlife.json (the Phoenix's burn time).</param>
    public AnimalsClient(ClientContext ctx, AnimalsFile data, AnimalShapes shapes, WildlifeFile wildlife, DriveClient drive)
    {
        _ctx = ctx; _data = data; _shapes = shapes; _wildlife = wildlife; _drive = drive;
        // registers the animal type and follows its entities
        AnimalRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.DespawnedFor += OnDespawnedFor;
        ctx.WorldEntities.Despawned += OnDespawned;
        ctx.WorldEntities.DormancyChanged += OnDormancy;
    }

    /// <summary>The animals drawn, by entity id.</summary>
    public IReadOnlyDictionary<ushort, AnimalView> ById => _animals;

    /// <summary>Leaving: the animals go with the owned node.</summary>
    public void Clear() => _animals.Clear();

    internal sealed class AnimalView
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
        /// <summary>The variant it wears: with the species it gives the Body box the client builds (as the server does, <see cref="Animal.Body"/>).</summary>
        public int Variant;
        /// <summary>The state whose clip is showing; 255 = none yet.</summary>
        public byte Shown = 255;
        public bool Toggle;
        public bool HitLeft;
        /// <summary>Time (seconds, client clock) until which a hit reaction plays; the state's clip waits for it.</summary>
        public double HitUntil;
        /// <summary>The ragdoll of a body a vehicle threw: it rolls about its own length (radians) while it flies and settles on a side where it lands.</summary>
        public float Roll;
        public bool Tumbling;
        /// <summary>The loose limbs of the thrown body, one per skeleton of the model; empty for a rig with none (it is thrown as a rigid body).</summary>
        public List<AnimalFlail> Flail = new();
        public Vector3 LastFeet;
        public bool SeenFeet;
        /// <summary>The driver's own picture of a throw by the vehicle it drives, shown before the server's arrives (<see cref="ThrowGuess"/>).</summary>
        public readonly ThrowGuess Guess = new();
        /// <summary>Where the snapshots had it last frame: how fast the server's body moves (a dead one still flying).</summary>
        public NVec3 LastServer;
        public bool SeenServer;
        /// <summary>The client-only looks of a species whose animals.json entry has an <see cref="AnimalDef.Fx"/> (the Unicorn's horn, sparkles and rainbow trail); null for the rest.</summary>
        public UnicornFx? Fx;
        /// <summary>The client-only fire of the Phoenix (<c>"fx": "phoenix"</c>); null for the rest.</summary>
        public PhoenixFx? Phoenix;
        /// <summary>A flyer (the Phoenix): its drawn pose, smoothed (heading, dive pitch and bank, radians; the turn rate, radians/s, the bank follows), the raw heading it had last frame, and whether the first frame has set them.</summary>
        public float FlyYaw, FlyPitch, FlyBank, FlyTurn, LastYaw;
        public bool SeenFly;
        /// <summary>A gliding flyer hovers now and then: the hover clip (<see cref="AnimalClip.Idle"/>) instead of the glide, and the seconds until it swaps.</summary>
        public bool Hover;
        public Countdown GlideLeft;
        /// <summary>Client clock time until which the animal is highlighted for its watcher (<see cref="Mark"/>, #473); 0 = not marked.</summary>
        public double MarkedUntil;
        public bool MarkShown;
    }

    readonly Dictionary<ushort, AnimalView> _animals = new();

    /// <summary>A thrown animal's roll in flight and its settling onto a side (rad/s), and the speed (m/s) above which a dead body is taken to be flying.</summary>
    const float AnimalRollRate = 9f, AnimalSettleRate = 9f, AnimalFlyingSpeed = 1.5f;
    /// <summary>How loose the limbs of a thrown animal stay once it lies (<see cref="AnimalFlail.Amount"/>; 1 in the air).</summary>
    const float AnimalLyingFlail = 0.5f;
    /// <summary>The smallest radius (m) a rolling body's turn rate is worked out with, and how far over the ground (m) a lying body's loose limbs are kept.</summary>
    const float AnimalRollRadiusMin = 0.15f, AnimalLimbClearance = 0.04f;

    /// <summary>How long a hit reaction may hold back the clip of the state (a gallop must not wait for a long clip).</summary>
    const double AnimalHitReactMaxSeconds = 0.6;

    /// <summary>A gliding flyer's seconds of glide before it hovers (a range) and its seconds of hover; how fast (per second) its pose follows the snapshots: heading, dive pitch, bank, and the turn rate the bank follows.</summary>
    const float GlideMin = 8f, GlideMax = 18f, HoverSeconds = 2.5f, FlyYawRate = 10f, FlyPitchRate = 6f, FlyBankRate = 4f, FlyTurnRate = 3f;

    /// <summary>
    /// The animals that stand, as the player's prediction meets them (<see cref="Player.Bodies"/>): each animal the server last sent that is in a state that stands (<see cref="AnimalRules.Stands"/>), at the
    /// NEWEST position it was sent (not the interpolated, drawn one), as a Body box built as the server builds it (<see cref="Animal.Body"/>). A standing animal does not move, so this is the box the server
    /// stops the player at; one that has just begun to walk or has just stopped is at most one round trip out of step, a correction of a step or two.
    /// </summary>
    void IStandingBodies.Near(float x, float z, float radius, List<BodyObstacle> into)
    {
        foreach (var (id, v) in _animals)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant || !AnimalRules.Stands(AnimalRules.StateOf(e.State.State))) continue;
            if (e.Buf.At(double.MaxValue) is not { } s) continue;
            var box = v.HitBox; // the look's box times its size, as the server's Animal.Body
            if (box.Count < 3) continue;
            var ahead = v.Def.BodyAheadOf(v.Variant) * v.Size;
            var body = BodyBox.Of(s.Pos, s.Yaw, box[0], box[2], ahead);
            var reach = MathF.Sqrt(box[0] * box[0] + box[2] * box[2]) / 2 + MathF.Abs(ahead) + radius;
            float dx = s.Pos.X - x, dz = s.Pos.Z - z;
            if (dx * dx + dz * dz > reach * reach) continue;
            into.Add(new BodyObstacle(body, s.Pos.Y, s.Pos.Y + box[1]));
        }
    }

    /// <summary>An animal outside the interest range (ADR 0025) is hidden and its animation stops; one that comes back starts its clip afresh (its pose is from a keyframe, not from the last time we saw it).</summary>
    void OnDormancy(WorldReplica.Entity e)
    {
        if (e.Type == AnimalRules.TypeId && _animals.TryGetValue(e.Id, out var v)) SetAnimalDormant(v, e);
    }

    static void SetAnimalDormant(AnimalView v, WorldReplica.Entity e)
    {
        v.Node.Visible = !e.Dormant;
        v.Node.ProcessMode = e.Dormant ? Node.ProcessModeEnum.Disabled : Node.ProcessModeEnum.Inherit; // an AnimationPlayer keeps playing while hidden otherwise
        if (e.Dormant) { v.Guess.Reset(); v.SeenServer = false; return; }
        v.Shown = 255; v.HitUntil = 0; v.Toggle = AnimalRules.HitToggled(e.State.State);
        v.SeenFly = false;
    }

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != AnimalRules.TypeId) return;
        var species = AnimalRules.SpeciesOf(e.State.Owner);
        if (species >= _data.Animals.Count)
        {
            GD.PrintErr($"client: animal {e.Id} has species {species}, animals.json has {_data.Animals.Count}; not drawn (data files differ from the server's?)");
            return;
        }
        var def = _data.Animals[species];
        var variant = AnimalRules.VariantOf(e.State.Owner);
        // a species with a weight rolls a size at spawn (the state byte's bits 5-7); the model and every box of it are scaled by it
        var size = def.WeightKgOf(variant) != null ? AnimalRules.SizeOfStep(AnimalRules.SizeStepOf(e.State.State)) : 1f;
        var box = def.HitBoxOf(variant).Select(x => x * size).ToArray();
        var node = new Node3D { Name = $"Animal{e.Id}" };
        var flail = new List<AnimalFlail>();
        var (modelRoot, anim, clips) = AddModel(node, def, variant, size, box);
        if (modelRoot != null)
            foreach (var sk in modelRoot.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>())
                if (AnimalFlail.Create(sk) is { } f) { sk.AddChild(f); flail.Add(f); }
        _ctx.Owned.AddChild(node);
        var view = new AnimalView { Node = node, Anim = anim, Def = def, Clips = clips, HitBox = box, Size = size, Variant = variant, Flail = flail, Toggle = AnimalRules.HitToggled(e.State.State) };
        if (def.Fx == UnicornFx.Id) view.Fx = UnicornFx.Attach(node, modelRoot, def.VanishSeconds);
        if (def.Fx == PhoenixFx.Id) view.Phoenix = PhoenixFx.Attach(node, modelRoot, _wildlife.Phoenix?.BurnSeconds ?? 6f);
        _animals[e.Id] = view;
        if (e.Dormant) SetAnimalDormant(view, e); // spawned outside the interest range: hidden until a keyframe shows it
    }

    /// <summary>
    /// Puts the model of a species' look under <paramref name="node"/>, scaled and turned as animals.json says, and finds its clips (<see cref="AnimalClips"/>; the looping ones are set
    /// to loop). Without a model (headless, or the file is missing) a box of <paramref name="box"/> stands in and there is no model, player or clip. Animals and Companions are both drawn with it.
    /// </summary>
    internal static (Node3D? Model, AnimationPlayer? Anim, string?[] Clips) AddModel(Node3D node, AnimalDef def, int variant, float size, IReadOnlyList<float> box)
    {
        if (ModelLib.Instance(def.ModelOf(variant)) is not { } model)
        {
            var (w, h, l) = (box[0], box[1], box[2]);
            node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, h, l) }, Position = new Vector3(0, h / 2, 0) });
            return (null, null, Array.Empty<string?>());
        }
        model.Scale = Vector3.One * (def.Scale * size);
        model.RotationDegrees = new Vector3(0, def.ModelYawDeg, 0);
        node.AddChild(model);
        var anim = model.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        var clips = Array.Empty<string?>();
        if (anim != null)
        {
            clips = AnimalClips.Resolve(n => anim.HasAnimation(n));
            for (var c = 0; c < clips.Length; c++)
                if (clips[c] is { } name) anim.GetAnimation(name).LoopMode = AnimalClips.Loops((AnimalClip)c) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        }
        return (model, anim, clips);
    }

    /// <summary>A pet that died of a hit (it has no corpse) goes in a puff where it stood; an animal that expired or was removed just leaves. Runs before <see cref="OnDespawned"/> frees the node.</summary>
    void OnDespawnedFor(ushort id, WorldDespawnReason reason)
    {
        if (reason != WorldDespawnReason.Destroyed || !_animals.TryGetValue(id, out var v)) return;
        var height = v.HitBox.Count == 3 ? v.HitBox[1] : 0.5f;
        PetFx.Puff(_ctx.Owned, v.Node.GlobalPosition + Vector3.Up * (height / 2), height);
    }

    void OnDespawned(ushort id)
    {
        if (!_animals.Remove(id, out var v)) return;
        v.Node.QueueFree();
    }

    /// <summary>Places the animals (interpolated, a little in the past like every remote body) and plays the clip of their state.</summary>
    public void Render()
    {
        if (_animals.Count == 0 || _ctx.LastTick == 0) return;
        TickMarks();
        var rt = _ctx.RenderTick;
        var now = Time.GetTicksMsec() / 1000.0;
        var dt = (float)_ctx.Root.GetProcessDeltaTime();
        var car = GuessCar();
        foreach (var (id, v) in _animals)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant) continue;
            var raw = e.State.State;
            var state = AnimalRules.StateOf(raw);
            if (e.Buf.At(rt) is { } s)
            {
                // the vehicle this player drives is drawn ahead of the server, so its run-over is shown here first and handed over to the server's when that shows
                var thrown = state is AnimalState.Thrown or AnimalState.Down or AnimalState.Dead;
                if (car is { } c && !thrown && state is not (AnimalState.Carried or AnimalState.Vanish) && v.Guess.Ready && !v.Def.OneHit && CarTouches(c.State, c.Def, v, s.Pos, s.Yaw))
                    v.Guess.Start(s.Pos, CorpseRules.ThrowFromCar(new NVec3(-MathF.Sin(c.State.Yaw), 0, -MathF.Cos(c.State.Yaw)), c.State.Speed));
                var serverSpeed = v.SeenServer && dt > 0 ? (s.Pos - v.LastServer).Length() / dt : 0f;
                v.LastServer = s.Pos; v.SeenServer = true;
                var inAir = state == AnimalState.Thrown || state == AnimalState.Dead && serverSpeed > AnimalFlyingSpeed;
                var at = v.Guess.Step(dt, s.Pos, thrown, inAir, v.Guess.Active ? GroundUnder(v.Guess.Position) : null);
                PlaceAnimal(v, GuessedState(v, state), new Vector3(at.X, at.Y, at.Z), s.Yaw, dt);
                if (v.Def.Flies) PoseFlyer(v, state, s.Yaw, s.Pitch, dt);
                v.Fx?.Tick(dt, state != AnimalState.Dead, state == AnimalState.Vanish);
                v.Phoenix?.Tick(dt, state);
            }
            var shown = GuessedState(v, state);
            var toggle = AnimalRules.HitToggled(raw);
            if (toggle != v.Toggle)
            {
                v.Toggle = toggle;
                if (state != AnimalState.Dead) PlayAnimalHit(v, now);
            }
            if (v.Tumbling) { ShowAnimalTumble(v, shown); continue; } // thrown by a vehicle: no clip of the state, the body is turned whole
            if (state == AnimalState.Dead) { if (v.Shown != (byte)state) ShowAnimalState(v, state); continue; }
            if (now < v.HitUntil) continue;
            if (v.Def.Flies && state == AnimalState.Flying) GlideOrHover(v, dt);
            if (v.Shown != (byte)state) ShowAnimalState(v, state); // also after a reaction: Shown is 254 until its clip is over
            v.HitUntil = 0;
        }
    }

    /// <summary>
    /// A flyer's pose: its heading follows the snapshots lightly, it dives at the pitch the server sends while Falling (negative: nose down; level otherwise) and banks into a turn (<see cref="FlyerLook.Bank"/>
    /// of the smoothed turn rate). The node turns about its origin: the body centre while it flies, which the model's origin is.
    /// </summary>
    void PoseFlyer(AnimalView v, AnimalState state, float yaw, float pitch, float dt)
    {
        var diving = state == AnimalState.Falling;
        if (!v.SeenFly || dt <= 0)
        {
            v.SeenFly = true;
            v.FlyYaw = v.LastYaw = yaw; v.FlyPitch = diving ? pitch : 0f; v.FlyBank = v.FlyTurn = 0f;
        }
        else
        {
            v.FlyTurn = FlyerLook.Approach(v.FlyTurn, FlyerLook.AngleDelta(v.LastYaw, yaw) / dt, FlyTurnRate, dt);
            v.LastYaw = yaw;
            v.FlyYaw += FlyerLook.AngleDelta(v.FlyYaw, yaw) * (1f - MathF.Exp(-FlyYawRate * dt));
            v.FlyPitch = diving ? FlyerLook.Approach(v.FlyPitch, pitch, FlyPitchRate, dt) : 0f; // landed: the sitting clip lays it down level
            v.FlyBank = FlyerLook.Approach(v.FlyBank, state == AnimalState.Flying ? FlyerLook.Bank(v.FlyTurn) : 0f, FlyBankRate, dt);
        }
        v.Node.Rotation = new Vector3(v.FlyPitch, v.FlyYaw, v.FlyBank);
    }

    /// <summary>A gliding flyer swaps its glide for a few seconds of hover now and then (the clip of the state is picked again).</summary>
    static void GlideOrHover(AnimalView v, float dt)
    {
        if (v.GlideLeft.Ready) v.GlideLeft.Start(GlideMin + GD.Randf() * (GlideMax - GlideMin)); // first seen (a swap restarts it at once)
        if (!v.GlideLeft.Tick(dt)) return;
        v.Hover = !v.Hover;
        v.GlideLeft.Start(v.Hover ? HoverSeconds : GlideMin + GD.Randf() * (GlideMax - GlideMin));
        v.Shown = 255;
    }

    /// <summary>The Phoenix's fall begins (event <see cref="EventKind.PhoenixFall"/>): a flash on the bird and a warning light at the crash site.</summary>
    public void OnPhoenixEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.PhoenixFall || !PhoenixFallCodec.TryDecode(payload, out var id, out _, out var site, out var seconds)) return;
        if (_animals.TryGetValue(id, out var v)) v.Phoenix?.Hit();
        PhoenixFx.CrashWarning(_ctx.Owned, new Vector3(site.X, site.Y, site.Z), seconds);
    }

    /// <summary>
    /// The vehicle whose run-overs this client shows ahead of the server (<see cref="ThrowGuess"/>): the predicted one the local player drives, while it runs bodies over
    /// (on land, moving fast enough to move one: the server's conditions in RunOverCheck).
    /// </summary>
    public (DriveState State, VehicleDef Def)? GuessCar()
    {
        if (_drive.Predicted is not { Def.RunOver: true } p || _ctx.Dead || _ctx.Match.Phase != MatchPhase.Running) return null; // the server throws no animal outside the round
        return p.State.Afloat || !RunOver.Contacts(p.State.Speed) ? null : p;
    }

    /// <summary>The ground's height under a guessed throw (one ray down through the static world), or null where there is none: the guess lands and rolls on the ground it is over, not at the height it left.</summary>
    public float? GroundUnder(NVec3 at)
    {
        var from = new Vector3(at.X, at.Y + 3f, at.Z);
        var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 40f, CollisionRules.StaticMask(MoverKind.Animal)));
        return hit.Count > 0 ? hit["position"].AsVector3().Y : null;
    }

    /// <summary>The state an animal is drawn in: while a guessed throw is what is drawn, a thrown body in the air or one knocked down where the guess landed, whatever the server's state says yet.</summary>
    static AnimalState GuessedState(AnimalView v, AnimalState state) => !v.Guess.Active ? state : v.Guess.Moving ? AnimalState.Thrown : AnimalState.Down;

    /// <summary>Whether the predicted vehicle's footprint touches the animal as it is drawn: the server's run-over test (<see cref="RunOverFootprint"/>), on the Body box the client builds as the server does.</summary>
    static bool CarTouches(in DriveState car, VehicleDef def, AnimalView v, NVec3 feet, float yaw)
    {
        var box = v.HitBox;
        if (box.Count < 3) return false;
        var body = BodyBox.Of(feet, yaw, box[0], box[2], v.Def.BodyAheadOf(v.Variant) * v.Size);
        return RunOverFootprint.OfBody(body, feet.Y).Hits(car.Position, car.Position, car.Yaw, def.HalfWidth, def.HalfLength);
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
        // loose limbs in the air, slack ones where it lies; lying or rolling slowly, none of them sinks into the ground under it
        var low = !flying || speed < AnimalRollRate * AnimalRollRadiusMin * 4f;
        foreach (var f in v.Flail) { f.Amount = !v.Tumbling ? 0f : flying ? 1f : AnimalLyingFlail; f.FloorY = v.Tumbling && low ? feet.Y + AnimalLimbClearance : null; }
        if (!v.Tumbling)
        {
            v.Node.GlobalPosition = feet;
            v.Node.Rotation = new Vector3(0, yaw, 0);
            return;
        }
        // in the air it spins at the full rate; rolling on along the ground it turns as fast as it goes, so the roll dies with its speed
        if (flying) v.Roll += Mathf.Min(AnimalRollRate, speed / Mathf.Max(v.HitBox.Count == 3 ? v.HitBox[0] / 2 : 0.2f, AnimalRollRadiusMin)) * dt;
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
        if (v.Def.Flies)
        {
            // a flyer's clips by its phase (Flying glides or hovers, Falling tumbles, Crashed, Burning and _ctx.Dead sit); no hit reactions, no death clip
            if (FlyerLook.ClipOf(state, v.Hover) is { } fc && Clip(fc) is { } flyClip) v.Anim.Play(flyClip, 0.3);
            return;
        }
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


    static readonly StandardMaterial3D MarkMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true,
        AlbedoColor = new Color(1f, 0.82f, 0.18f, 0.5f), RenderPriority = 20,
    };

    /// <summary>The mark key: the nearest animal under the middle of the view, not behind the world, within the binoculars' range, is marked (a repeat renews the time).</summary>
    public void Mark(ItemDef? carried)
    {
        if (carried is not { MarkSeconds: > 0 } pair || _ctx.Me == null) return;
        if (AnimalUnderCentre() is not { } v)
        {
            UiShell.Instance?.Toast(Strings.T("toast.mark.none"), ToastKind.Warn);
            Sfx.Ui("ui.error");
            return;
        }
        v.MarkedUntil = _ctx.Now + pair.MarkSeconds;
        Sfx.Ui("ui.accept");
        UiShell.Instance?.Toast(Strings.F("toast.mark.set", (int)pair.MarkSeconds), ToastKind.Info);
    }

    AnimalView? AnimalUnderCentre()
    {
        var cam = _ctx.Me!.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var wall = Binoculars.MaxRangeM;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * Binoculars.MaxRangeM, CollisionLayers.WorldShots);
        q.Exclude = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
        var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) wall = from.DistanceTo(hit["position"].AsVector3());
        var o = new NVec3(from.X, from.Y, from.Z); var d = new NVec3(dir.X, dir.Y, dir.Z);
        AnimalView? best = null;
        var bestAt = wall;
        foreach (var a in _animals.Values)
        {
            if (!GodotObject.IsInstanceValid(a.Node) || !a.Node.Visible || a.HitBox.Count < 3) continue;
            var p = a.Node.GlobalPosition;
            var target = new HitTarget(0, new NVec3(p.X, p.Y, p.Z), Stance.Stand, a.Node.Rotation.Y, Box: new NVec3(a.HitBox[0], a.HitBox[1], a.HitBox[2]), BoxAhead: a.Def.HitBoxAhead * a.Size);
            if (HitMath.RayBox(o, d, target) is { } at && at < bestAt) { best = a; bestAt = at; }
        }
        return best;
    }

    /// <summary>Once a frame: puts the highlight on the animals that are marked and takes it off those whose time ran out; it pulses.</summary>
    void TickMarks()
    {
        var pulse = 0.38f + 0.14f * MathF.Sin((float)_ctx.Now * 5f);
        MarkMaterial.AlbedoColor = new Color(1f, 0.82f, 0.18f, pulse);
        foreach (var v in _animals.Values)
        {
            var on = v.MarkedUntil > _ctx.Now;
            if (on == v.MarkShown || !GodotObject.IsInstanceValid(v.Node)) continue;
            v.MarkShown = on;
            foreach (var mi in v.Node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
                mi.MaterialOverlay = on ? MarkMaterial : null;
        }
    }
}
