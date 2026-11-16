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
        for (var i = sw.Bales.Count; i < sw.Swath.Bales.Count; i++) SpawnDroppedBale(sw, i);
    }

    /// <summary>Per frame: the reel of every harvester somebody drives turns.</summary>
    void RenderHarvester(float dt)
    {
        if (!ShotFx.Enabled || Vehicles == null) return;
        FadeDroppedBales();
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
            case HarvesterCodec.FxKind.Drawn: if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at, 0.3f); break; // a dull thump as the machine takes the body in
        }
    }

    // ---- bales ----

    /// <summary>The corpse a bale is drawn as: the Synty bale (a cylinder without the pack), lying on the ground where the unloader put it.</summary>
    /// <summary>The picture of a bale (Synty round bale, a cylinder without the pack); no collision.</summary>
    Node3D MakeBaleModel()
    {
        var model = Map?.InstantiateAsset(BaleAsset);
        if (model != null)
        {
            foreach (var co in model.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; }
            model.Name = "Model";
            return model;
        }
        return new MeshInstance3D
        {
            Name = "Model", Mesh = new CylinderMesh { TopRadius = 0.8f, BottomRadius = 0.8f, Height = 1.2f, RadialSegments = 12 },
            Position = new Vector3(0, 0.6f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#c9a748"), Roughness = 1f },
        };
    }

    CorpseView MakeBaleCorpse(WorldReplica.Entity e)
    {
        var node = new Node3D { Name = $"Bale{e.Id}" };
        if (ShotFx.Enabled) node.AddChild(MakeBaleModel());
        Owned.AddChild(node);
        return new CorpseView { Node = node, View = null, Bale = true };
    }

    bool IsBaleCorpse(ushort corpse) => WorldEntities.Entities.TryGetValue(corpse, out var e) && HarvesterRules.IsBale(e.State.Owner);

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
            if (v.Node.FindChild("Model", false, false) is Node3D model) model.Visible = false;
            v.Node.AddChild(ash);
        }
    }

    void HarvesterCorpseGone(ushort id) => _baleFires.Remove(id);

    // ---- bales the machine dropped ----

    /// <summary>Puts bale <paramref name="i"/> of the field's derived list on the ground (adopting fire or ash that arrived first).</summary>
    void SpawnDroppedBale(ClientSwath sw, int i)
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
        foreach (var sw in _swathsC.Values) ClearDroppedBales(sw, false);
        _swathsC.Clear();
        foreach (var o in _orphanBales) if (GodotObject.IsInstanceValid(o.Root)) o.Root.QueueFree();
        _orphanBales.Clear();
    }
}
