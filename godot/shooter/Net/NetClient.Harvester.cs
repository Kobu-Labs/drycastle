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
    HarvestPlan? _harvPlan;
    int _harvPlanField = -1;
    HarvestProgress _harvCut = HarvestProgress.None;
    bool _harvLive;
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
        if (Map == null || !ShotFx.Enabled) return; // headless clients draw no field
        var fields = HarvFields;
        if (m.Field >= fields.Count) return;
        if (_harvPlanField != m.Field || _harvPlan == null)
        {
            var cfg = new HarvesterConfig { HeaderWidthM = m.HeaderM, EdgeMarginM = m.MarginM, PassOverlapM = m.OverlapM };
            _harvPlan = FieldPlanner.Plan(fields[m.Field].Ring, cfg);
            _harvPlanField = m.Field;
            _harvCut = HarvestProgress.None;
        }
        if (_harvPlan == null || m.Pass == 255) return;
        var to = new HarvestProgress(m.Pass, m.Along);
        foreach (var (a, b) in HarvestProgress.Cuts(_harvPlan.Passes, _harvCut, to))
            Map.CropCut(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), m.HeaderM / 2f);
        if (to.Pass > _harvCut.Pass || (to.Pass == _harvCut.Pass && to.Along > _harvCut.Along)) _harvCut = to;
    }

    /// <summary>Per frame: the reel of every harvester somebody drives turns.</summary>
    void RenderHarvester(float dt)
    {
        if (!ShotFx.Enabled || Vehicles == null) return;
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
            case HarvesterCodec.FxKind.BaleLit: LightBale(m.Id, at); break;
            case HarvesterCodec.FxKind.BaleAsh: BurnOutBale(m.Id, at); break;
            case HarvesterCodec.FxKind.Drawn: if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at, 0.3f); break; // a dull thump as the machine takes the body in
        }
    }

    // ---- bales ----

    /// <summary>The corpse a bale is drawn as: the Synty bale (a cylinder without the pack), lying on the ground where the unloader put it.</summary>
    CorpseView MakeBaleCorpse(WorldReplica.Entity e)
    {
        var node = new Node3D { Name = $"Bale{e.Id}" };
        if (ShotFx.Enabled)
        {
            var model = Map?.InstantiateAsset(BaleAsset);
            if (model != null)
            {
                foreach (var co in model.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; }
                model.Name = "Model";
                node.AddChild(model);
            }
            else node.AddChild(new MeshInstance3D
            {
                Name = "Model", Mesh = new CylinderMesh { TopRadius = 0.8f, BottomRadius = 0.8f, Height = 1.2f, RadialSegments = 12 },
                Position = new Vector3(0, 0.6f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#c9a748"), Roughness = 1f },
            });
        }
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
}
