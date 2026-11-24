using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Status;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The bales the harvester drops on a field, drawn from the cut itself: the client replays the reel's path (<see cref="EventKind.HarvesterCut"/>) on the same plan the server made
/// (<see cref="FieldPlanner"/>), squashes the crop in the swath to stubble (<c>MapLoader.CropCut</c>) and derives the bales from the same segments the server counted. Their fire and ash
/// (<see cref="EventKind.HarvesterFx"/>) can come before the stretch that makes the bale; a field's bales fade out after the last cut there, as the server's do. All cosmetic.
/// </summary>
internal sealed class DroppedBales
{
    readonly ClientContext _ctx;
    readonly BaleCorpses _bales;
    readonly Func<Vector3, CarView?> _nearestHarvester;
    List<CropField>? _fields;

    /// <param name="nearestHarvester">The harvester nearest a point, for the pop of a bale leaving its chute.</param>
    public DroppedBales(ClientContext ctx, BaleCorpses bales, Func<Vector3, CarView?> nearestHarvester) { _ctx = ctx; _bales = bales; _nearestHarvester = nearestHarvester; }

    /// <summary>What the reels cut on a field, as the server sent it (<see cref="EventKind.HarvesterCut"/>), and the bales derived from it, by field index.</summary>
    sealed class ClientSwath
    {
        public required FieldSwath Swath;
        public double LastCut;
        public readonly List<Node3D> Bales = new();
        public readonly List<BaleState> States = new();
    }
    readonly Dictionary<ushort, ClientSwath> _swaths = new();
    /// <summary>Fire or ash that came before the bale it belongs to (the effect can overtake the stretch that makes the bale).</summary>
    readonly List<(Node3D Root, BaleState State)> _orphans = new();
    double _fadeCheck;

    List<CropField> Fields => _fields ??= _ctx.Map == null
        ? new List<CropField>()
        : CropFields.From(_ctx.Map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())));

    /// <summary>A stretch of the reel's real path: the crop in the swath is squashed to stubble (<c>MapLoader.CropCut</c>), and on grain and rapeseed the bales it dropped are derived from the same segments the server counted.</summary>
    public void OnCut(SwathCodec.Msg m)
    {
        if (_ctx.Map == null || !ShotFx.Enabled) return; // headless clients draw no field
        var fields = Fields;
        if (m.Field >= fields.Count) return;
        if (!_swaths.TryGetValue(m.Field, out var sw)) _swaths[m.Field] = sw = new ClientSwath { Swath = new FieldSwath(new HarvesterConfig(), fields[m.Field].DropsBales) };
        sw.LastCut = _ctx.Now - m.AgeS;
        for (var i = 1; i < m.Points.Count; i++)
        {
            var a = m.Points[i - 1].Metres; var b = m.Points[i].Metres;
            _ctx.Map.CropCut(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), m.WidthM / 2f);
            if (!m.NoBales) sw.Swath.Add(m.Points[i - 1], m.Points[i]);
        }
        for (var i = sw.Bales.Count; i < sw.Swath.Bales.Count; i++) Spawn(sw, i, pop: !m.Replay);
    }

    /// <summary>Puts bale <paramref name="i"/> of the field's derived list on the ground (adopting fire or ash that arrived first).</summary>
    void Spawn(ClientSwath sw, int i, bool pop)
    {
        var (pos, yaw) = sw.Swath.Bales[i];
        var ground = _ctx.Map!.GroundHeight(pos.X, pos.Y);
        if (float.IsNaN(ground)) ground = _ctx.Map.GroundHeightOrMean(pos.X, pos.Y);
        var at = new Vector3(pos.X, ground, pos.Y);
        var state = BaleState.Intact;
        Node3D? root = null;
        for (var k = 0; k < _orphans.Count; k++)
        {
            if (!GodotObject.IsInstanceValid(_orphans[k].Root) || new Vector2(_orphans[k].Root.Position.X - at.X, _orphans[k].Root.Position.Z - at.Z).Length() > 0.6f) continue;
            (root, state) = _orphans[k]; _orphans.RemoveAt(k); break;
        }
        root ??= new Node3D { Name = $"DroppedBale{i}", Position = at };
        if (root.GetParent() == null) _ctx.Owned.AddChild(root);
        root.Rotation = new Vector3(0, yaw, 0);
        if (state != BaleState.Ash) { var model = _bales.MakeModel(); root.AddChild(model); }
        sw.Bales.Add(root); sw.States.Add(state);
        if (pop) _ctx.Cars.Sounds.PlayOnce("harvester.pop", at, _nearestHarvester(at)); // a live message, not the joiner's replay: the machine's unloader (heard from inside when it is the one we ride)
    }

    /// <summary>The server's bale fire or ash at <paramref name="at"/>: the nearest dropped bale there (or a node of its own that the bale adopts when its stretch arrives).</summary>
    public void SetState(Vector3 at, BaleState state)
    {
        Node3D? root = null; ClientSwath? owner = null; var idx = -1; var best = 1.5f;
        foreach (var sw in _swaths.Values)
            for (var i = 0; i < sw.Bales.Count; i++)
            {
                if (!GodotObject.IsInstanceValid(sw.Bales[i])) continue;
                var d = new Vector2(sw.Bales[i].Position.X - at.X, sw.Bales[i].Position.Z - at.Z).Length();
                if (d < best) { best = d; root = sw.Bales[i]; owner = sw; idx = i; }
            }
        if (root == null)
        {
            root = new Node3D { Name = "DroppedBaleFx", Position = at };
            _ctx.Owned.AddChild(root);
            _orphans.Add((root, state));
        }
        else if (owner!.States[idx] >= state) return;
        else owner.States[idx] = state;
        for (var k = 0; k < _orphans.Count; k++) if (_orphans[k].Root == root) _orphans[k] = (root, state);
        if (state == BaleState.Burning) { if (!root.HasNode("Fire")) BaleBurn.Light(root, at); }
        else
        {
            root.GetNodeOrNull<Node3D>("Fire")?.QueueFree();
            root.GetNodeOrNull<Node3D>("Model")?.QueueFree();
            root.AddChild(BaleBurn.Ash());
        }
    }

    /// <summary>Once a second: a field's bales fade out <see cref="HarvesterConfig.DroppedBaleFadeS"/> after the last cut there, as the server's do.</summary>
    public void Fade()
    {
        if (_swaths.Count == 0 || _ctx.Now < _fadeCheck) return;
        _fadeCheck = _ctx.Now + 1;
        var fade = new HarvesterConfig().DroppedBaleFadeS;
        foreach (var sw in _swaths.Values)
            if (sw.Bales.Count > 0 && _ctx.Now - sw.LastCut >= fade) Clear(sw, fadeOnly: true);
    }

    void Clear(ClientSwath sw, bool fadeOnly)
    {
        foreach (var b in sw.Bales) if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        sw.Bales.Clear(); sw.States.Clear();
        if (fadeOnly) sw.Swath.FadeBales();
    }

    /// <summary>A new round: the server forgot every cut and bale.</summary>
    public void Restart()
    {
        foreach (var sw in _swaths.Values) Clear(sw, false);
        _swaths.Clear();
        foreach (var o in _orphans) if (GodotObject.IsInstanceValid(o.Root)) o.Root.QueueFree();
        _orphans.Clear();
    }
}

/// <summary>The fire and the ash of a bale (a corpse's or a field's own): the flame with its warm light and the whoosh of it catching, and the dark heap it burns down to.</summary>
internal static class BaleBurn
{
    /// <summary>A flame on a bale (the corpse's node, or a node of its own in the world for a field's bale); a warm light, no shadows. Returns the fire node.</summary>
    public static Node3D Light(Node3D parent, Vector3 at)
    {
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
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at, 0.15f); // the whoosh of it catching
        return fire;
    }

    /// <summary>The heap a bale burns down to.</summary>
    public static MeshInstance3D Ash() => new()
    {
        Name = "Ash", Mesh = new CylinderMesh { TopRadius = 0.95f, BottomRadius = 1.15f, Height = 0.18f, RadialSegments = 10 },
        Position = new Vector3(0, 0.09f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.09f, 0.08f), Roughness = 1f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };
}
