using Godot;
using Drycastle.ShooterCore;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// The harvester event on the client (#331): the machine is an ordinary vehicle (vehicles.json <c>harvester</c>) whose reel turns while somebody drives it; the cut is drawn from the job's compact state
/// (<see cref="EventKind.HarvesterState"/>: field, pass, metres done), which the client replays on the same plan the server made (<see cref="FieldPlanner"/>) and squashes the crop in the swath to stubble
/// (<c>MapLoader.CropCut</c>), nothing per plant on the wire. A bale is a corpse owned by <see cref="HarvesterRules.BaleOwner"/>, drawn as a hay bale (placeholder cylinder without the paid pack); its fire and ash
/// are <see cref="EventKind.HarvesterFx"/>, also for the field's own bales (their instance leaves the crop layer when they have burnt). All cosmetic.
/// </summary>
public partial class NetClient
{
    /// <summary>The synty prefab of a bale lying on the ground (the field's own bales use the same).</summary>
    const string BaleAsset = "pack:synty-farm/SM_Prop_Hay_Bale_Round_01";
    const string ReelNode = "SM_Veh_Harvester_Reel";
    /// <summary>Radians per second the reel turns while somebody drives the machine.</summary>
    const float ReelSpin = 3.4f;

    List<CropField>? _harvFields;
    bool _harvLive;
    /// <summary>What the reels cut on a field, as the server sent it (<see cref="EventKind.HarvesterCut"/>), and the bales derived from it, by field index.</summary>
    sealed class ClientSwath
    {
        public required FieldSwath Swath;
        public double LastCut;
        public readonly List<Node3D> Bales = new();
        public readonly List<BaleState> States = new();
    }
    readonly Dictionary<ushort, ClientSwath> _swathsC = new();
    /// <summary>Fire or ash that came before the bale it belongs to (the effect can overtake the stretch that makes the bale).</summary>
    readonly List<(Node3D Root, BaleState State)> _orphanBales = new();
    double _baleFadeCheck;
    readonly Dictionary<ushort, Node3D> _reels = new();
    /// <summary>The catch as one transformation: corpses spinning round the reel (id to when, from where and by which machine), bales just out of the chute (id to when), machines that rumble while they work (car id to until).</summary>
    sealed class DrawIn
    {
        public double Start;
        public CarView? Car;
        /// <summary>Where the body lay when the reel took it (the first frame it is drawn).</summary>
        public Vector3? From;
    }
    readonly Dictionary<ushort, DrawIn> _drawIn = new();
    readonly Dictionary<ushort, double> _baleEmerge = new();
    /// <summary>Players the harvester caught (id to when): their dead avatar stays hidden through the machine and while the body is inside its bale (the corpse entity is gone at Swallowed), and the bale (corpse id to victim) draws the body inside.</summary>
    readonly Dictionary<ushort, double> _harvHidden = new();
    readonly Dictionary<ushort, ushort> _baleBody = new();
    readonly Dictionary<ushort, double> _shakeUntil = new();
    static readonly float HarvesterConfigDraw = new HarvesterConfig().DrawSeconds, HarvesterConfigChew = new HarvesterConfig().ChewSeconds;
    const float EmergeSeconds = 0.25f;
    /// <summary>The orbit's angle when the body first reaches it (in front of the axis, a little below), and how fast the body is pulled in over the first seconds.</summary>
    const float OrbitStartAngle = 1.8f, OrbitGatherSeconds = 0.45f;
    /// <summary>A harvester's wheels (SM_Veh_Harvester_Wheel_fl, fr, rl, rr; the model has no hull to hang them under) and how far they have rolled and steered.</summary>
    sealed class HarvWheels
    {
        public Node3D?[] Nodes = new Node3D?[4];
        public Vector3 LastPos; public float LastYaw; public bool Seen;
        public readonly float[] Roll = new float[4]; public float Steer;
    }
    readonly Dictionary<ushort, HarvWheels> _harvWheels = new();
    static readonly string[] WheelNodes = { "SM_Veh_Harvester_Wheel_fl", "SM_Veh_Harvester_Wheel_fr", "SM_Veh_Harvester_Wheel_rl", "SM_Veh_Harvester_Wheel_rr" };
    /// <summary>Rolling radii of the wheels (the model's mesh: the big front pair 0.733 m, the rear pair 0.554 m) and the wheelbase (z 0.86 to -2.82) the front steering angle follows from.</summary>
    static readonly float[] WheelRadius = { 0.733f, 0.733f, 0.554f, 0.554f };
    const float HarvWheelBase = 3.68f;
    readonly Dictionary<ushort, Node3D> _baleFires = new();
    readonly List<Node3D> _ashHeaps = new();

    List<CropField> HarvFields => _harvFields ??= Map == null
        ? new List<CropField>()
        : CropFields.From(Map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())));

    void OnHarvesterEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.HarvesterState when HarvesterCodec.DecodeState(payload) is { } s: OnHarvesterState(s); break;
            case EventKind.HarvesterFx when HarvesterCodec.DecodeFx(payload) is { } f: OnHarvesterFx(f); break;
            case EventKind.HarvesterCut when SwathCodec.Decode(payload) is { } c: OnHarvesterCut(c); break;
            case EventKind.CropCrush when CrushCodec.Decode(payload) is { } cr: OnCropCrush(cr); break;
        }
    }

    void OnHarvesterState(HarvesterCodec.StateMsg m)
    {
        if (m.Phase == HarvesterPhase.Gone && _cars.TryGetValue(m.Car, out var gone))
        {
            // it drove off the field: the server hides it (a destroyed record), which must not look like a blast
            gone.Destroyed = true; gone.Exploded = true; gone.ExplodedAt = Now;
            gone.Snd?.Release(); gone.Snd = null; gone.Engine?.Release();
            gone.Node.Visible = false;
        }
        _harvLive = m.Phase is not (HarvesterPhase.Gone or HarvesterPhase.Stopped);
    }

    /// <summary>A stretch of the reel's real path: the crop in the swath is squashed to stubble (<c>MapLoader.CropCut</c>), and on grain and rapeseed the bales it dropped are derived from the same segments the server counted.</summary>
    void OnHarvesterCut(SwathCodec.Msg m)
    {
        if (Map == null || !ShotFx.Enabled) return; // headless clients draw no field
        var fields = HarvFields;
        if (m.Field >= fields.Count) return;
        if (!_swathsC.TryGetValue(m.Field, out var sw)) _swathsC[m.Field] = sw = new ClientSwath { Swath = new FieldSwath(new HarvesterConfig(), fields[m.Field].DropsBales) };
        sw.LastCut = Now - m.AgeS;
        for (var i = 1; i < m.Points.Count; i++)
        {
            var a = m.Points[i - 1].Metres; var b = m.Points[i].Metres;
            Map.CropCut(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), m.WidthM / 2f);
            if (!m.NoBales) sw.Swath.Add(m.Points[i - 1], m.Points[i]);
        }
        for (var i = sw.Bales.Count; i < sw.Swath.Bales.Count; i++) SpawnDroppedBale(sw, i, pop: !m.Replay);
    }

    /// <summary>Per frame: the reel of every harvester somebody drives turns.</summary>
    void RenderHarvester(float dt)
    {
        if (!ShotFx.Enabled || Vehicles == null) return;
        FadeDroppedBales();
        StepCarVoices();
        StampCrush(); // after RenderCars: from the drawn poses
        FlushCrush();
        foreach (var v in _cars.Values)
        {
            if (v.Kind >= Vehicles.Vehicles.Count || Vehicles.Vehicles[v.Kind].Id != HarvesterRules.VehicleId || v.Destroyed) continue;
            if (!_reels.TryGetValue(v.Id, out var reel) || !GodotObject.IsInstanceValid(reel))
            {
                reel = v.Node.FindChild(ReelNode, true, false) as Node3D;
                if (reel == null) continue; // the box without the pack has no reel
                _reels[v.Id] = reel;
            }
            if (v.Driver != 0) reel.RotateX(-ReelSpin * dt); // the cut side moves down at the front: the top turns forward
            RollHarvesterWheels(v);
        }
        AnimateCatch();
    }

    CarView? NearestHarvester(Vector3 at)
    {
        CarView? best = null; var bd = 12f;
        if (Vehicles == null) return null;
        foreach (var v in _cars.Values)
        {
            if (v.Destroyed || v.Kind >= Vehicles.Vehicles.Count || Vehicles.Vehicles[v.Kind].Id != HarvesterRules.VehicleId) continue;
            var d = v.Node.GlobalPosition.DistanceTo(at);
            if (d < bd) { bd = d; best = v; }
        }
        return best;
    }

    /// <summary>
    /// After the corpses and the cars are placed: the body the reel caught spins round the reel's axis at the reel's speed (<see cref="ReelSpin"/>, tumbling with it, <see cref="HarvesterCatch.OrbitOffset"/>), gathered
    /// onto the orbit in the first moments and drawn into the machine at the end (the server holds it at the axis for <see cref="HarvesterConfig.DrawSeconds"/>); the machine rumbles while it chews; the bale
    /// that leaves the chute grows in quickly and the server throws it from there. Cosmetic: nothing here touches the server positions.
    /// </summary>
    void AnimateCatch()
    {
        if (!ShotFx.Enabled) return;
        var cfg = new HarvesterConfig();
        foreach (var (id, d) in _drawIn.ToArray())
        {
            var t = (float)(Now - d.Start);
            if (t > HarvesterConfigDraw * 1.6f || !_corpses.TryGetValue(id, out var v) || !GodotObject.IsInstanceValid(v.Node)) { if (t > HarvesterConfigDraw * 1.6f) _drawIn.Remove(id); continue; }
            if (d.Car == null || !GodotObject.IsInstanceValid(d.Car.Node)) continue;
            d.From ??= v.Node.GlobalPosition;
            var machine = d.Car.Node.GlobalBasis;
            var axis = d.Car.Node.ToGlobal(new Vector3(0, cfg.ReelAxisUpM, -cfg.HeaderForwardM));
            var k = Mathf.Clamp(t / HarvesterConfigDraw, 0f, 1f);
            var tail = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((k - 0.75f) / 0.25f, 0f, 1f)); // the last quarter: into the machine, smaller
            var o = HarvesterCatch.OrbitOffset(HarvesterCatch.OrbitAngle(OrbitStartAngle, ReelSpin, t), cfg.OrbitRadiusM * (1f - 0.5f * tail));
            var orbit = axis + machine * new Vector3(o.X, o.Y, o.Z + 1.3f * tail);
            var gather = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t / OrbitGatherSeconds, 0f, 1f));
            v.Node.GlobalPosition = d.From.Value.Lerp(orbit, gather);
            var turn = new Basis(machine.X.Normalized(), -ReelSpin * t); // forward over the reel, as the reel turns: the top moves into the machine
            v.Node.Basis = (turn * v.Node.Basis).Scaled(Vector3.One * (1f - 0.8f * tail));
        }
        foreach (var (id, start) in _baleEmerge.ToArray())
        {
            var t = (float)((Now - start) / EmergeSeconds);
            if (t >= 1f || !_corpses.TryGetValue(id, out var v) || !GodotObject.IsInstanceValid(v.Node)) { if (t >= 1f) _baleEmerge.Remove(id); continue; }
            const float c1 = 1.70158f, c3 = c1 + 1f;
            var grow = t <= 0 ? 0.01f : 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f); // ease out back: a little overshoot
            v.Node.Basis = v.Node.Basis.Scaled(Vector3.One * Mathf.Max(0.01f, grow));
        }
        foreach (var (carId, until) in _shakeUntil.ToArray())
        {
            if (Now >= until || !_cars.TryGetValue(carId, out var car) || !GodotObject.IsInstanceValid(car.Node)) { if (Now >= until) _shakeUntil.Remove(carId); continue; }
            var env = (float)Math.Min(1.0, (until - Now) / 0.6);
            var tt = (float)Now;
            car.Node.GlobalPosition += new Vector3(MathF.Sin(tt * 47f) * 0.02f, MathF.Sin(tt * 61f) * 0.025f, MathF.Cos(tt * 53f) * 0.02f) * env;
        }
    }

    /// <summary>The wheels roll with the ground distance the machine covers (angle = distance / radius) and the front pair steers by the turn the body makes per metre.</summary>
    void RollHarvesterWheels(CarView v)
    {
        if (!_harvWheels.TryGetValue(v.Id, out var w))
        {
            w = new HarvWheels();
            for (var i = 0; i < 4; i++) w.Nodes[i] = v.Node.FindChild(WheelNodes[i], true, false) as Node3D;
            _harvWheels[v.Id] = w;
        }
        if (w.Nodes[0] == null && w.Nodes[2] == null) return; // the box without the pack
        var pos = v.Node.GlobalPosition; var yaw = v.Node.GlobalRotation.Y;
        if (!w.Seen) { w.LastPos = pos; w.LastYaw = yaw; w.Seen = true; return; }
        var fwd = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var dist = (pos - w.LastPos).Dot(fwd);
        var dYaw = Mathf.Wrap(yaw - w.LastYaw, -Mathf.Pi, Mathf.Pi);
        w.LastPos = pos; w.LastYaw = yaw;
        if (MathF.Abs(dist) > 0.002f)
        {
            var target = Mathf.Clamp(MathF.Atan(HarvWheelBase * dYaw / dist), -0.7f, 0.7f); // yaw grows turning left; a left turn turns the model's wheels about +Y
            w.Steer = Mathf.Lerp(w.Steer, target, 0.25f);
        }
        else w.Steer = Mathf.Lerp(w.Steer, 0f, 0.1f);
        for (var i = 0; i < 4; i++)
        {
            if (w.Nodes[i] is not { } node || !GodotObject.IsInstanceValid(node)) continue;
            w.Roll[i] = Mathf.Wrap(w.Roll[i] + dist / WheelRadius[i], -Mathf.Pi, Mathf.Pi); // forward: the top moves towards the model's +Z
            node.Basis = new Basis(Vector3.Up, i < 2 ? w.Steer : 0f) * new Basis(Vector3.Right, w.Roll[i]);
        }
    }

    void OnHarvesterFx(HarvesterCodec.FxMsg m)
    {
        var at = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        switch (m.Kind)
        {
            case HarvesterCodec.FxKind.BaleLit when HarvesterCodec.IsDroppedBale(m.Id): DroppedBaleState(at, BaleState.Burning); break;
            case HarvesterCodec.FxKind.BaleAsh when HarvesterCodec.IsDroppedBale(m.Id): DroppedBaleState(at, BaleState.Ash); break;
            case HarvesterCodec.FxKind.BaleLit: LightBale(m.Id, at); break;
            case HarvesterCodec.FxKind.BaleAsh: BurnOutBale(m.Id, at); break;
            case HarvesterCodec.FxKind.Drawn: // the reel took somebody: it whirrs up, the engine strains, the body is lifted and tumbled in
                var catcher = NearestHarvester(at);
                if (catcher != null) PlayCarVoice("harvester.reel_in", at, catcher); else if (Sfx.Enabled) Sfx.Play3D("harvester.reel_in", at); // on the machine, heard the same from any camera
                _drawIn[m.Id] = new DrawIn { Start = Now, Car = catcher };
                if (m.Victim != 0) _harvHidden[m.Victim] = Now;
                if (catcher != null) _shakeUntil[catcher.Id] = Now + HarvesterConfigDraw + HarvesterConfigChew;
                break;
            case HarvesterCodec.FxKind.Swallowed: // gone into the machine: a muffled chewing while it works
                if (NearestHarvester(at) is { } chewer) PlayCarVoice("harvester.chew", at, chewer); else if (Sfx.Enabled) Sfx.Play3D("harvester.chew", at);
                _drawIn.Remove(m.Id);
                if (m.Victim != 0) _harvHidden[m.Victim] = Now;
                break;
            case HarvesterCodec.FxKind.BaleOut: // the chute throws the bale out with the body inside: it flies, lands and rolls (the server moves it)
                var chute = NearestHarvester(at);
                PlayCarSound("harvester.eject", at, chute);
                PlayCarSound("harvester.pop", at, chute);
                _baleEmerge[m.Id] = Now;
                if (m.Victim != 0) { _baleBody[m.Id] = m.Victim; _harvHidden[m.Victim] = Now; }
                break;
        }
    }

    // ---- bales ----

    /// <summary>The corpse a bale is drawn as: the Synty bale (a cylinder without the pack), lying on the ground where the unloader put it.</summary>
    /// <summary>
    /// The picture of a bale (Synty round bale, a cylinder without the pack), lying on its side on y = 0, its axis along x; no collision. The Synty mesh is centred on its origin, a cylinder along y
    /// (1.26 x 1.58 x 1.22 m): it is turned onto its side and lifted by its radius. Placed as it comes, its lower half is under the ground.
    /// </summary>
    Node3D MakeBaleModel()
    {
        var onSide = new Basis(Vector3.Back, Mathf.Pi / 2f); // the mesh's y (its axis) to -x, its x up
        var model = Map?.InstantiateAsset(BaleAsset);
        if (model != null)
        {
            foreach (var co in model.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; }
            model.Name = "Model";
            var mi = model as MeshInstance3D ?? model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault(); // the prefab: a plain root over the mesh, no transforms
            model.Transform = new Transform3D(onSide, new Vector3(0, mi?.Mesh is { } mesh ? -mesh.GetAabb().Position.X : BaleRadius, 0));
            return model;
        }
        return new MeshInstance3D
        {
            Name = "Model", Mesh = new CylinderMesh { TopRadius = BaleRadius, BottomRadius = BaleRadius, Height = 1.6f, RadialSegments = 12 },
            Transform = new Transform3D(onSide, new Vector3(0, BaleRadius, 0)), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#c9a748"), Roughness = 1f },
        };
    }

    CorpseView MakeBaleCorpse(WorldReplica.Entity e)
    {
        var node = new Node3D { Name = $"Bale{e.Id}" };
        Node3D? spin = null;
        if (ShotFx.Enabled) { spin = new Node3D { Name = "Spin" }; spin.AddChild(MakeBaleModel()); node.AddChild(spin); } // the bale tumbles about its middle: the model hangs under a node that turns
        Owned.AddChild(node);
        return new CorpseView { Node = node, View = null, Bale = true, Motion = new BaleMotion { Spin = spin } };
    }

    bool IsBaleCorpse(ushort corpse) => WorldEntities.Entities.TryGetValue(corpse, out var e) && HarvesterRules.IsBale(e.State.Owner);

    /// <summary>
    /// A bale corpse as drawn (#351): the server throws it from the unloader, it lands and rolls to a stop; the position arrives at snapshot rate, so the picture follows it smoothly. It lies on its side with its
    /// axis across the way it goes and turns about that axis like a wheel (spinning in the air, by the distance covered on the ground), so it never dips into the ground; on the ground it leans with the slope
    /// under it. The caught body stands out of its top, swaying as the bale turns, upright at rest, for as long as the bale lies (until it burns). The server position is where it is looted.
    /// </summary>
    void RenderBale(ushort id, CorpseView v, WorldState s, Vector3 at, float dt)
    {
        var m = v.Motion!;
        v.Feet = at;
        var moving = s.PitchRad > CorpseRules.RollPitch * 0.5f;
        var flying = s.PitchRad > CorpseRules.FlightPitch * 0.5f;
        if (!m.Seen) m.Yaw = s.YawRad;
        var before = m.Seen ? m.Shown : at;
        m.Shown = !m.Seen || before.DistanceTo(at) > 6f ? at : before.Lerp(at, 1f - Mathf.Exp(-24f * dt));
        if (!moving && m.Shown.DistanceSquaredTo(at) < 1e-3f) m.Shown = at;
        m.Seen = true;
        var step = m.Shown - before;
        var along = new Vector3(step.X, 0, step.Z);
        var ds = along.Length();
        if (moving && ds > 1e-4f)
        {
            var axis = Vector3.Up.Cross(along / ds).Normalized(); // the horizontal axis across the way it goes: the bale's own axis (its local x)
            m.Yaw = Mathf.Atan2(-axis.Z, axis.X);
        }
        if (flying) m.Angle += Mathf.Clamp(dt > 0 ? ds / dt / BaleRadius : 0f, 3f, 10f) * dt;
        else if (moving) m.Angle += ds / BaleRadius; // rolling: a turn for each circumference
        else m.Angle = Mathf.Lerp(m.Angle, Mathf.Round(m.Angle / Mathf.Tau) * Mathf.Tau, 1f - Mathf.Exp(-8f * dt));
        if (flying) { m.UpTarget = Vector3.Up; m.Leaned = false; }
        else if (moving || !m.Leaned) { m.UpTarget = GroundUp(at); m.Leaned = !moving; } // at rest the slope is looked up once
        m.Up = m.Up.Lerp(m.UpTarget, 1f - Mathf.Exp(-10f * dt)).Normalized();
        v.Node.GlobalPosition = m.Shown;
        v.Node.Basis = new Basis(new Quaternion(Vector3.Up, m.Up)) * new Basis(Vector3.Up, m.Yaw);
        if (m.Spin is { } spin && GodotObject.IsInstanceValid(spin))
        {
            var turn = new Basis(Vector3.Right, m.Angle); // about its own axis; the top goes the way it rolls
            spin.Transform = new Transform3D(turn, BaleCentre - turn * BaleCentre);
            if (_baleBody.TryGetValue(id, out var victim) && victim != 0)
            {
                m.Body ??= MakeBaleBody(v.Node, victim);
                if (!GodotObject.IsInstanceValid(m.Body)) return;
                if (m.BodyLoose != moving) { m.BodyLoose = moving; m.Body.SetRagdoll(true, moving ? 1f : BaleBodyRestFlail, frozen: !moving); } // limbs fly while it moves, hang still at rest
                var sway = new Basis(Vector3.Right, BaleBodySwayRad * Mathf.Sin(m.Angle)); // it does not go round with the bale (the head would go under the ground): it rocks about the bale's middle
                m.Body.Transform = new Transform3D(sway, BaleCentre + sway * (new Vector3(0, BaleBodyFeetM, 0) - BaleCentre));
            }
        }
    }

    /// <summary>The terrain's up at <paramref name="at"/> (from four heights 0.6 m around it); straight up where the bale is not on the terrain (a roof, a road deck, no terrain).</summary>
    Vector3 GroundUp(Vector3 at)
    {
        if (Map is not { } map) return Vector3.Up;
        const float e = 0.6f;
        float c = map.GroundHeight(at.X, at.Z), x0 = map.GroundHeight(at.X - e, at.Z), x1 = map.GroundHeight(at.X + e, at.Z), z0 = map.GroundHeight(at.X, at.Z - e), z1 = map.GroundHeight(at.X, at.Z + e);
        if (float.IsNaN(c + x0 + x1 + z0 + z1) || Mathf.Abs(c - at.Y) > 0.4f) return Vector3.Up;
        var up = new Vector3(x0 - x1, 2f * e, z0 - z1).Normalized();
        return up.Y < 0.7f ? Vector3.Up : up; // steeper than about 45 degrees: a step or a wall of the terrain, not a slope to lie on
    }

    /// <summary>The bale lies on its side: a cylinder of this radius, 1.6 m long along its local x; its middle is a radius up.</summary>
    const float BaleRadius = 0.63f;
    static readonly Vector3 BaleCentre = new(0, BaleRadius, 0);

    /// <summary>How high the caught body's feet are in the bale (m): the legs are in the hay, the body from the hips up stands out of the top (1.26 m). How loose its limbs hang at rest, how far it rocks (rad) as the bale turns.</summary>
    const float BaleBodyFeetM = 0.3f, BaleBodyRestFlail = 0.35f, BaleBodySwayRad = 0.5f;

    /// <summary>The body the reel caught, full size, stuck in the bale from the hips down, limbs loose (the dead player's look). It stays for as long as the bale does; <see cref="RenderBale"/> rocks it.</summary>
    CharacterView MakeBaleBody(Node3D parent, ushort victim)
    {
        var view = CharacterView.Make();
        if (Appearances.TryGetValue(victim, out var look)) view.SetCamo(look, CamoData);
        view.ShowThrown("");
        view.SetRagdoll(true, 1f);
        parent.AddChild(view);
        return view;
    }

    /// <summary>Whether the dead avatar of <paramref name="id"/> stays hidden: the harvester caught the player (a few seconds from the effects, until the player is alive again).</summary>
    bool HarvestVictimHidden(ushort id, bool dead)
    {
        if (!_harvHidden.TryGetValue(id, out var t)) return false;
        if (!dead && Now - t > 2) { _harvHidden.Remove(id); return false; }
        return dead;
    }

    /// <summary>A flame on a bale (the corpse's node, or a node of its own in the world for a field's bale); a warm light, no shadows.</summary>
    void LightBale(ushort id, Vector3 at)
    {
        if (!ShotFx.Enabled || _baleFires.ContainsKey(id)) return;
        Node3D? parent;
        if (HarvesterCodec.IsStaticBale(id)) { parent = new Node3D { Name = $"BaleFire{id}", Position = at }; Owned.AddChild(parent); }
        else if (!_corpses.TryGetValue(id, out var v)) return;
        else parent = v.Node;
        var fire = new Node3D { Name = "Fire" };
        parent.AddChild(fire);
        if (StatusFx.MakeFlames(fire) is { } flames)
        {
            flames.Amount = 40; flames.Position = new Vector3(0, 0.7f, 0);
            flames.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; flames.EmissionSphereRadius = 0.6f;
            flames.ScaleAmountMin = 0.8f; flames.ScaleAmountMax = 1.4f; flames.InitialVelocityMax = 2.2f;
            ParticleFx.Run(flames, true);
        }
        fire.AddChild(new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.2f), LightEnergy = 2.5f, OmniRange = 7f, ShadowEnabled = false, Position = new Vector3(0, 1f, 0) });
        _baleFires[id] = HarvesterCodec.IsStaticBale(id) ? parent : fire;
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at, 0.15f); // the whoosh of it catching
    }

    /// <summary>Burnt out: the flame goes, the bale is a dark heap (the field's own instance leaves the crop layer).</summary>
    void BurnOutBale(ushort id, Vector3 at)
    {
        if (_baleFires.Remove(id, out var fire) && GodotObject.IsInstanceValid(fire)) fire.QueueFree();
        if (!ShotFx.Enabled) return;
        var ash = new MeshInstance3D
        {
            Name = "Ash", Mesh = new CylinderMesh { TopRadius = 0.95f, BottomRadius = 1.15f, Height = 0.18f, RadialSegments = 10 },
            Position = new Vector3(0, 0.09f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.09f, 0.08f), Roughness = 1f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (HarvesterCodec.IsStaticBale(id))
        {
            Map?.CropHideArtifact(at, 1.2f);
            var heap = new Node3D { Name = $"BaleAsh{id}", Position = at };
            heap.AddChild(ash);
            Owned.AddChild(heap);
            _ashHeaps.Add(heap);
        }
        else if (_corpses.TryGetValue(id, out var v))
        {
            if (v.Node.FindChild("Model", true, false) is Node3D model) model.Visible = false;
            _baleBody.Remove(id); // burnt with the bale
            if (v.Motion?.Body is { } burnt && GodotObject.IsInstanceValid(burnt)) burnt.QueueFree();
            v.Node.AddChild(ash);
        }
    }

    void HarvesterCorpseGone(ushort id) { _baleFires.Remove(id); _baleBody.Remove(id); _baleEmerge.Remove(id); _drawIn.Remove(id); }

    // ---- bales the machine dropped ----

    /// <summary>Puts bale <paramref name="i"/> of the field's derived list on the ground (adopting fire or ash that arrived first).</summary>
    void SpawnDroppedBale(ClientSwath sw, int i, bool pop)
    {
        var (pos, yaw) = sw.Swath.Bales[i];
        var ground = Map!.GroundHeight(pos.X, pos.Y);
        if (float.IsNaN(ground)) ground = Map.GroundHeightOrMean(pos.X, pos.Y);
        var at = new Vector3(pos.X, ground, pos.Y);
        var state = BaleState.Intact;
        Node3D? root = null;
        for (var k = 0; k < _orphanBales.Count; k++)
        {
            if (!GodotObject.IsInstanceValid(_orphanBales[k].Root) || new Vector2(_orphanBales[k].Root.Position.X - at.X, _orphanBales[k].Root.Position.Z - at.Z).Length() > 0.6f) continue;
            (root, state) = _orphanBales[k]; _orphanBales.RemoveAt(k); break;
        }
        root ??= new Node3D { Name = $"DroppedBale{i}", Position = at };
        if (root.GetParent() == null) Owned.AddChild(root);
        root.Rotation = new Vector3(0, yaw, 0);
        if (state != BaleState.Ash) { var model = MakeBaleModel(); root.AddChild(model); }
        sw.Bales.Add(root); sw.States.Add(state);
        if (pop) PlayCarSound("harvester.pop", at, NearestHarvester(at)); // a live message, not the joiner's replay: the machine's unloader (heard from inside when it is the one we ride)
    }

    /// <summary>The server's bale fire or ash at <paramref name="at"/>: the nearest dropped bale there (or a node of its own that the bale adopts when its stretch arrives).</summary>
    void DroppedBaleState(Vector3 at, BaleState state)
    {
        Node3D? root = null; ClientSwath? owner = null; var idx = -1; var best = 1.5f;
        foreach (var sw in _swathsC.Values)
            for (var i = 0; i < sw.Bales.Count; i++)
            {
                if (!GodotObject.IsInstanceValid(sw.Bales[i])) continue;
                var d = new Vector2(sw.Bales[i].Position.X - at.X, sw.Bales[i].Position.Z - at.Z).Length();
                if (d < best) { best = d; root = sw.Bales[i]; owner = sw; idx = i; }
            }
        if (root == null)
        {
            root = new Node3D { Name = "DroppedBaleFx", Position = at };
            Owned.AddChild(root);
            _orphanBales.Add((root, state));
        }
        else if (owner!.States[idx] >= state) return;
        else owner.States[idx] = state;
        for (var k = 0; k < _orphanBales.Count; k++) if (_orphanBales[k].Root == root) _orphanBales[k] = (root, state);
        if (state == BaleState.Burning) AddBaleFlames(root, at);
        else
        {
            root.GetNodeOrNull<Node3D>("Fire")?.QueueFree();
            root.GetNodeOrNull<Node3D>("Model")?.QueueFree();
            root.AddChild(MakeAsh());
        }
    }

    void AddBaleFlames(Node3D parent, Vector3 at)
    {
        if (parent.HasNode("Fire")) return;
        var fire = new Node3D { Name = "Fire" };
        parent.AddChild(fire);
        if (StatusFx.MakeFlames(fire) is { } flames)
        {
            flames.Amount = 40; flames.Position = new Vector3(0, 0.7f, 0);
            flames.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; flames.EmissionSphereRadius = 0.6f;
            flames.ScaleAmountMin = 0.8f; flames.ScaleAmountMax = 1.4f; flames.InitialVelocityMax = 2.2f;
            ParticleFx.Run(flames, true);
        }
        fire.AddChild(new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.2f), LightEnergy = 2.5f, OmniRange = 7f, ShadowEnabled = false, Position = new Vector3(0, 1f, 0) });
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at, 0.15f);
    }

    static MeshInstance3D MakeAsh() => new()
    {
        Name = "Ash", Mesh = new CylinderMesh { TopRadius = 0.95f, BottomRadius = 1.15f, Height = 0.18f, RadialSegments = 10 },
        Position = new Vector3(0, 0.09f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.09f, 0.08f), Roughness = 1f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    /// <summary>Once a second: a field's bales fade out <see cref="HarvesterConfig.DroppedBaleFadeS"/> after the last cut there, as the server's do.</summary>
    void FadeDroppedBales()
    {
        if (_swathsC.Count == 0 || Now < _baleFadeCheck) return;
        _baleFadeCheck = Now + 1;
        var fade = new HarvesterConfig().DroppedBaleFadeS;
        foreach (var sw in _swathsC.Values)
            if (sw.Bales.Count > 0 && Now - sw.LastCut >= fade) ClearDroppedBales(sw, fadeOnly: true);
    }

    void ClearDroppedBales(ClientSwath sw, bool fadeOnly)
    {
        foreach (var b in sw.Bales) if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        sw.Bales.Clear(); sw.States.Clear();
        if (fadeOnly) sw.Swath.FadeBales();
    }

    /// <summary>A new round: the server forgot every cut and bale.</summary>
    void HarvesterRoundRestart()
    {
        _harvHidden.Clear(); _baleBody.Clear(); _drawIn.Clear(); _baleEmerge.Clear();
        CropCrushRestart(); // the masks and every vehicle's path start afresh
        Map?.CropReset(); // plants stand again, the ground marks go
        foreach (var sw in _swathsC.Values) ClearDroppedBales(sw, false);
        _swathsC.Clear();
        foreach (var o in _orphanBales) if (GodotObject.IsInstanceValid(o.Root)) o.Root.QueueFree();
        _orphanBales.Clear();
    }
}
