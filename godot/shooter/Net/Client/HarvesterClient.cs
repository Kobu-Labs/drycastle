using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The harvester event on the client (#331): the machine is an ordinary vehicle (vehicles.json <c>harvester</c>) whose reel turns while somebody drives it; the cut is drawn from the job's compact state
/// (<see cref="EventKind.HarvesterState"/>: field, pass, metres done), which the client replays on the same plan the server made (<see cref="FieldPlanner"/>) and squashes the crop in the swath to stubble
/// (<c>MapLoader.CropCut</c>, <see cref="DroppedBales"/>), nothing per plant on the wire. A bale is a corpse owned by <see cref="HarvesterRules.BaleOwner"/>, drawn as a hay bale (<see cref="BaleCorpses"/>); its fire and ash
/// are <see cref="EventKind.HarvesterFx"/>, also for the field's own bales (their instance leaves the crop layer when they have burnt). All cosmetic.
/// </summary>
internal sealed class HarvesterClient
{
    const string ReelNode = "SM_Veh_Harvester_Reel";
    /// <summary>Radians per second the reel turns while somebody drives the machine.</summary>
    const float ReelSpin = 3.4f;
    static readonly float HarvesterConfigDraw = new HarvesterConfig().DrawSeconds, HarvesterConfigChew = new HarvesterConfig().ChewSeconds;
    const float EmergeSeconds = 0.25f;
    /// <summary>The orbit's angle when the body first reaches it (in front of the axis, a little below), and how fast the body is pulled in over the first seconds.</summary>
    const float OrbitStartAngle = 1.8f, OrbitGatherSeconds = 0.45f;

    readonly ClientContext _ctx;
    readonly CorpsesClient _corpses;
    readonly BaleCorpses _bales;
    readonly DroppedBales _dropped;
    readonly CropCrushClient _crush;
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
    readonly Dictionary<ushort, double> _emerge = new();
    /// <summary>Players the harvester caught (id to when): their dead avatar stays hidden through the machine and while the body is inside its bale (the corpse entity is gone at Swallowed),.</summary>
    readonly Dictionary<ushort, double> _hidden = new();
    readonly Dictionary<ushort, double> _shakeUntil = new();

    readonly Dictionary<ushort, Node3D> _fires = new();

    public HarvesterClient(ClientContext ctx, CorpsesClient corpses, BaleCorpses bales, DroppedBales dropped, CropCrushClient crush)
    {
        _ctx = ctx; _corpses = corpses; _bales = bales; _dropped = dropped; _crush = crush;
        corpses.Despawned += CorpseGone;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.HarvesterState when HarvesterCodec.DecodeState(payload) is { } s: OnState(s); break;
            case EventKind.HarvesterFx when HarvesterCodec.DecodeFx(payload) is { } f: OnFx(f); break;
            case EventKind.HarvesterCut when SwathCodec.Decode(payload) is { } c: _dropped.OnCut(c); break;
            case EventKind.CropCrush when CrushCodec.Decode(payload) is { } cr: _crush.OnMessage(cr); break;
        }
    }

    void OnState(HarvesterCodec.StateMsg m)
    {
        if (m.Phase == HarvesterPhase.Gone && _ctx.Cars.TryGet(m.Car, out var gone))
        {
            // it drove off the field: the server hides it (a destroyed record), which must not look like a blast
            gone.Destroyed = true; gone.Exploded = true; gone.ExplodedAt = _ctx.Now;
            gone.Snd?.Release(); gone.Snd = null; gone.Engine?.Release();
            gone.Node.Visible = false;
        }
    }

    /// <summary>Per frame: the reel of every harvester somebody drives turns.</summary>
    public void Render(float dt)
    {
        if (!ShotFx.Enabled || _ctx.Vehicles == null) return;
        _dropped.Fade();
        _ctx.Cars.Sounds.Step();
        _crush.Stamp(); // after RenderCars: from the drawn poses
        _crush.Flush();
        foreach (var v in _ctx.Cars.All)
        {
            if (v.Kind >= _ctx.Vehicles.Vehicles.Count || _ctx.Vehicles.Vehicles[v.Kind].Id != HarvesterRules.VehicleId || v.Destroyed) continue;
            if (!_reels.TryGetValue(v.Id, out var reel) || !GodotObject.IsInstanceValid(reel))
            {
                reel = v.Node.FindChild(ReelNode, true, false) as Node3D;
                if (reel == null) continue; // the box without the pack has no reel
                _reels[v.Id] = reel;
            }
            if (v.Driver != 0) reel.RotateX(ReelSpin * dt); // about the model's own X (the standardized model is in the game frame; the prefab's was turned half a circle, hence the old minus): the same turn as before
        }
        AnimateCatch();
    }

    public CarView? NearestHarvester(Vector3 at)
    {
        CarView? best = null; var bd = 12f;
        if (_ctx.Vehicles == null) return null;
        foreach (var v in _ctx.Cars.All)
        {
            if (v.Destroyed || v.Kind >= _ctx.Vehicles.Vehicles.Count || _ctx.Vehicles.Vehicles[v.Kind].Id != HarvesterRules.VehicleId) continue;
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
            var t = (float)(_ctx.Now - d.Start);
            if (t > HarvesterConfigDraw * 1.6f || !_corpses.All.TryGetValue(id, out var v) || !GodotObject.IsInstanceValid(v.Node)) { if (t > HarvesterConfigDraw * 1.6f) _drawIn.Remove(id); continue; }
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
        foreach (var (id, start) in _emerge.ToArray())
        {
            var t = (float)((_ctx.Now - start) / EmergeSeconds);
            if (t >= 1f || !_corpses.All.TryGetValue(id, out var v) || !GodotObject.IsInstanceValid(v.Node)) { if (t >= 1f) _emerge.Remove(id); continue; }
            const float c1 = 1.70158f, c3 = c1 + 1f;
            var grow = t <= 0 ? 0.01f : 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f); // ease out back: a little overshoot
            v.Node.Basis = v.Node.Basis.Scaled(Vector3.One * Mathf.Max(0.01f, grow));
        }
        foreach (var (carId, until) in _shakeUntil.ToArray())
        {
            if (_ctx.Now >= until || !_ctx.Cars.TryGet(carId, out var car) || !GodotObject.IsInstanceValid(car.Node)) { if (_ctx.Now >= until) _shakeUntil.Remove(carId); continue; }
            var env = (float)Math.Min(1.0, (until - _ctx.Now) / 0.6);
            var tt = (float)_ctx.Now;
            car.Node.GlobalPosition += new Vector3(MathF.Sin(tt * 47f) * 0.02f, MathF.Sin(tt * 61f) * 0.025f, MathF.Cos(tt * 53f) * 0.02f) * env;
        }
    }

    void OnFx(HarvesterCodec.FxMsg m)
    {
        var at = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        switch (m.Kind)
        {
            case HarvesterCodec.FxKind.BaleLit when HarvesterCodec.IsDroppedBale(m.Id): _dropped.SetState(at, BaleState.Burning); break;
            case HarvesterCodec.FxKind.BaleAsh when HarvesterCodec.IsDroppedBale(m.Id): _dropped.SetState(at, BaleState.Ash); break;
            case HarvesterCodec.FxKind.BaleLit: LightBale(m.Id, at); break;
            case HarvesterCodec.FxKind.BaleAsh: BurnOutBale(m.Id, at); break;
            case HarvesterCodec.FxKind.Drawn: // the reel took somebody: it whirrs up, the engine strains, the body is lifted and tumbled in
                var catcher = NearestHarvester(at);
                if (catcher != null) _ctx.Cars.Sounds.PlayLong("harvester.reel_in", at, catcher); else if (Sfx.Enabled) Sfx.Play3D("harvester.reel_in", at); // on the machine, heard the same from any camera
                _drawIn[m.Id] = new DrawIn { Start = _ctx.Now, Car = catcher };
                if (m.Victim != 0) _hidden[m.Victim] = _ctx.Now;
                if (catcher != null) _shakeUntil[catcher.Id] = _ctx.Now + HarvesterConfigDraw + HarvesterConfigChew;
                break;
            case HarvesterCodec.FxKind.Swallowed: // gone into the machine: a muffled chewing while it works
                if (NearestHarvester(at) is { } chewer) _ctx.Cars.Sounds.PlayLong("harvester.chew", at, chewer); else if (Sfx.Enabled) Sfx.Play3D("harvester.chew", at);
                _drawIn.Remove(m.Id);
                if (m.Victim != 0) _hidden[m.Victim] = _ctx.Now;
                break;
            case HarvesterCodec.FxKind.BaleOut: // the chute throws the bale out with the body inside: it flies, lands and rolls (the server moves it)
                var chute = NearestHarvester(at);
                _ctx.Cars.Sounds.PlayOnce("harvester.eject", at, chute);
                _ctx.Cars.Sounds.PlayOnce("harvester.pop", at, chute);
                _emerge[m.Id] = _ctx.Now;
                if (m.Victim != 0) { _bales.Carry(m.Id, m.Victim); _hidden[m.Victim] = _ctx.Now; }
                break;
        }
    }

    /// <summary>Whether the dead avatar of <paramref name="id"/> stays hidden: the harvester caught the player (a few seconds from the effects, until the player is alive again).</summary>
    public bool VictimHidden(ushort id, bool dead)
    {
        if (!_hidden.TryGetValue(id, out var t)) return false;
        if (!dead && _ctx.Now - t > 2) { _hidden.Remove(id); return false; }
        return dead;
    }

    /// <summary>A flame on a bale (the corpse's node, or a node of its own in the world for a field's bale).</summary>
    void LightBale(ushort id, Vector3 at)
    {
        if (!ShotFx.Enabled || _fires.ContainsKey(id)) return;
        Node3D? parent;
        if (HarvesterCodec.IsStaticBale(id)) { parent = new Node3D { Name = $"BaleFire{id}", Position = at }; _ctx.Owned.AddChild(parent); }
        else if (!_corpses.All.TryGetValue(id, out var v)) return;
        else parent = v.Node;
        var fire = BaleBurn.Light(parent, at);
        _fires[id] = HarvesterCodec.IsStaticBale(id) ? parent : fire;
    }

    /// <summary>Burnt out: the flame goes, the bale is a dark heap (the field's own instance leaves the crop layer).</summary>
    void BurnOutBale(ushort id, Vector3 at)
    {
        if (_fires.Remove(id, out var fire) && GodotObject.IsInstanceValid(fire)) fire.QueueFree();
        if (!ShotFx.Enabled) return;
        if (HarvesterCodec.IsStaticBale(id))
        {
            _ctx.Map?.CropHideArtifact(at, 1.2f);
            var heap = new Node3D { Name = $"BaleAsh{id}", Position = at };
            heap.AddChild(BaleBurn.Ash());
            _ctx.Owned.AddChild(heap);
        }
        else if (_corpses.All.TryGetValue(id, out var v))
        {
            if (v.Node.FindChild("Model", true, false) is Node3D model) model.Visible = false;
            _bales.Forget(id); // burnt with the bale
            if (v.Motion?.Body is { } burnt && GodotObject.IsInstanceValid(burnt)) burnt.QueueFree();
            v.Node.AddChild(BaleBurn.Ash());
        }
    }

    /// <summary>A corpse is gone: what the harvester kept for it goes.</summary>
    void CorpseGone(ushort id) { _fires.Remove(id); _bales.Forget(id); _emerge.Remove(id); _drawIn.Remove(id); }

    /// <summary>A new round: the server forgot every cut and bale.</summary>
    public void RoundRestart()
    {
        _hidden.Clear(); _bales.Clear(); _drawIn.Clear(); _emerge.Clear();
        _crush.Restart(); // the masks and every vehicle's path start afresh
        _ctx.Map?.CropReset(); // plants stand again, the ground marks go
        _dropped.Restart();
    }
}
