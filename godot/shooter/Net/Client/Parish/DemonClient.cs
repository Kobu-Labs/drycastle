using Godot;
using Drycastle.Shooter.Churches;
using Drycastle.Shooter.Demon;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Shooting;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Parish;

/// <summary>
/// The demon of the Holy Mass on the client: the world entity of type <see cref="DemonRules.TypeId"/> is a <see cref="DemonView"/> placed from the interpolated
/// state (position, the way it faces, the surface normal in the owner field, the state byte, the share of hits left in the hp byte); its fireballs are events the client
/// flies itself (<see cref="EventKind.DemonFireball"/> starts the arc, <see cref="EventKind.DemonImpact"/> ends it with a burst). The server decides everything.
/// <para>
/// The pastor turning into the demon (<see cref="DemonState.Transforming"/>) shakes the scene for everyone in or near the church: the camera shake of the roars
/// (<see cref="CameraShake"/>) is fed every frame, building up to the swap (<see cref="DemonFightDef.TransformSwapS"/>), then dying down to a rumble by the end of the turning;
/// the nearer to the pastor, the stronger. The church's lights follow the same clock (<see cref="ChurchDecor.SetTransform"/>). The clock starts when the state is first seen, as the demon view's does.
/// </para>
/// </summary>
internal sealed class DemonClient
{
    readonly ClientContext _ctx;
    readonly DemonFile _data;
    readonly HolyMassFile _holyMass;
    readonly MassClient _mass;
    readonly BlastDustClient _blastDust;
    readonly PounceClient _pounce;

    public DemonClient(ClientContext ctx, DemonFile data, HolyMassFile holyMass, MassClient mass, BlastDustClient blastDust, PounceClient pounce)
    {
        _ctx = ctx; _data = data; _holyMass = holyMass; _mass = mass; _blastDust = blastDust; _pounce = pounce;
        DemonRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    /// <summary>The demon's view, or null when there is no demon.</summary>
    public DemonView? FirstView() { foreach (var d in _demons.Values) return d.View; return null; }

    /// <summary>The views of the demons in the world.</summary>
    public IEnumerable<DemonView> Views => _demons.Values.Select(d => d.View);

    sealed class DemonNode
    {
        public DemonView View = null!;
        public byte Hp = 255;
        /// <summary>The player the demon breathes fire at (the last <see cref="EventKind.DemonFlame"/>).</summary>
        public ushort FlameTarget;
    }

    sealed class BallNode
    {
        public Node3D Node = null!;
        public Vector3 Origin, Velocity;
        public double Start;
    }

    readonly Dictionary<ushort, DemonNode> _demons = new();
    readonly Dictionary<ushort, BallNode> _balls = new();

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != DemonRules.TypeId) return;
        if (_data.Hits.Base <= 0)
        {
            GD.PrintErr($"client: demon {e.Id} but holy_mass_demon.json is missing; not drawn (data files differ from the server's?)");
            return;
        }
        var view = DemonView.Make(_data, _holyMass.Mass);
        view.RoarCue = at => _ctx.Shake.At(at, Math.Max(1f, _data.Roar.ShakeRadiusM) / 5f);
        view.Bitten = _pounce.OnBite;
        _ctx.Owned.AddChild(view);
        ChurchDecor.Current?.SetDemon(true, snap: DemonRules.StateOf(e.State.State) != DemonState.Transforming); // a joiner finds the cross turned and the lights out
        _demons[e.Id] = new DemonNode { View = view, Hp = e.State.Hp };
    }

    void OnDespawned(ushort id)
    {
        if (_demons.Remove(id, out var d)) d.View.QueueFree();
        if (_demons.Count == 0) _pounce.Reset();
        if (_demons.Count == 0) ChurchDecor.Current?.SetDemon(false);
        if (_demons.Count == 0) foreach (var b in _balls.Values) RocketFx.Retire(_ctx.Owned, b.Node);
        if (_demons.Count == 0) _balls.Clear();
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.DemonFireball when DemonCodec.DecodeFireball(payload) is { } f:
                var node = DemonFx.MakeFireball(_ctx.Owned);
                var o = new Vector3(f.Origin.X, f.Origin.Y, f.Origin.Z);
                node.Visible = ChurchInterior.Visible;
                node.GlobalPosition = o;
                if (_balls.Remove(f.Id, out var old)) RocketFx.Retire(_ctx.Owned, old.Node);
                _balls[f.Id] = new BallNode { Node = node, Origin = o, Velocity = new Vector3(f.Velocity.X, f.Velocity.Y, f.Velocity.Z), Start = Time.GetTicksMsec() / 1000.0 };
                foreach (var d in _demons.Values) d.View.Fling();
                break;
            case EventKind.DemonImpact when DemonCodec.DecodeImpact(payload) is { } i:
                var at = new Vector3(i.At.X, i.At.Y, i.At.Z);
                if (_balls.Remove(i.Id, out var ball)) RocketFx.Retire(_ctx.Owned, ball.Node);
                if (i.Fizzled) break;
                var r = _data.Fireball.Splash.RadiusM;
                RocketFx.Explode(_ctx.Owned, at, r);
                _blastDust.BlastFelt(at, r, null);
                break;
            case EventKind.DemonFlame when DemonCodec.DecodeFlame(payload) is { } target:
                foreach (var d in _demons.Values) d.FlameTarget = target;
                break;
            case EventKind.DemonPounce when DemonCodec.DecodePounce(payload) is { } pm:
                _pounce.OnMessage(pm);
                break;
            case EventKind.DemonCursed:
                UiShell.Instance?.Toast(Strings.T("mass.cursed"), ToastKind.Error, null, 5.0);
                break;
        }
    }

    /// <summary>The demon's Body capsule in the physics world, where the player's prediction and the driver's sweep meet it (<see cref="DemonBlocker"/>): null until there is a demon.</summary>
    DemonBlocker? _demonBody;

    /// <summary>
    /// Once a physics tick, before the body is predicted: the capsule stands at the demon's NEWEST pose (not the interpolated, drawn one, which lags by the render delay), the nearest the client
    /// can get to where the server's capsule is when the input being predicted is simulated there. With no demon it goes.
    /// </summary>
    public void BodyTick()
    {
        foreach (var (id, _) in _demons)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.Buf.At(double.MaxValue) is not { } s) continue;
            if (_demonBody == null || !GodotObject.IsInstanceValid(_demonBody)) { _demonBody = new DemonBlocker(); _ctx.Owned.AddChild(_demonBody); }
            var n = DemonRules.UnpackNormal(e.State.Owner);
            _demonBody.Place(DemonRules.StateOf(e.State.State), s.Pos, n, _data.BodyM, _data.Scale);
            return;
        }
        if (_demonBody != null) { if (GodotObject.IsInstanceValid(_demonBody)) _demonBody.QueueFree(); _demonBody = null; }
    }

    /// <summary>Places the demon (interpolated, a little in the past like every remote body), shows what it does, and flies the fireballs.</summary>
    public void Render()
    {
        if (_ctx.LastTick == 0) return;
        var dt = _ctx.Root.GetProcessDeltaTime();
        if (_demons.Count > 0)
        {
            var rt = _ctx.RenderTick;
            foreach (var (id, d) in _demons)
            {
                if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
                var view = d.View;
                view.FloorY = _mass.State.ChantAt.Y - 1.5f; // the pastor's spot is the floor
                if (e.Buf.At(rt) is { } s) // placed before the state is shown: one that is dead from the start falls from where it hangs
                {
                    var n = DemonRules.UnpackNormal(e.State.Owner);
                    var f = DemonRules.Facing(s.Yaw, s.Pitch);
                    view.Place(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), new Vector3(n.X, n.Y, n.Z), new Vector3(f.X, f.Y, f.Z), dt);
                }
                view.PinMuzzleAt = _pounce.FacePoint();
                view.PinMine = _pounce.MarkedMe; // marked at the wind: the world state can come before the pin event
                view.Calm = GameSettings.Current.ReduceMotion;
                view.Show(DemonRules.StateOf(e.State.State));
                if (e.State.Hp < d.Hp) view.Hurt();
                d.Hp = e.State.Hp;
                var breathing = DemonRules.StateOf(e.State.State) == DemonState.Breathing;
                view.FlameAt = breathing ? PlayerPoint(d.FlameTarget, 1.0f) : null;
                view.LookAtWorld = view.FlameAt is { } fa ? fa + Vector3.Up * 0.3f : NearestPlayerEye(view.GlobalPosition);
            }
        }
        if (_balls.Count == 0) return;
        var now = Time.GetTicksMsec() / 1000.0;
        var g = _data.Fireball.GravityMps2;
        List<ushort>? gone = null;
        foreach (var (id, b) in _balls)
        {
            var t = (float)(now - b.Start);
            if (t > _data.Fireball.LifeS + 1f || !GodotObject.IsInstanceValid(b.Node)) { (gone ??= new()).Add(id); continue; }
            b.Node.GlobalPosition = b.Origin + b.Velocity * t + new Vector3(0, -0.5f * g * t * t, 0);
            b.Node.Visible = ChurchInterior.Visible;
        }
        if (gone != null) foreach (var id in gone) { if (_balls.Remove(id, out var b)) RocketFx.Retire(_ctx.Owned, b.Node); }
    }

    /// <summary>A point <paramref name="up"/> metres above the feet of a player (the local one or a remote), or null when they are not here.</summary>
    Vector3? PlayerPoint(ushort id, float up)
    {
        if (id == _ctx.MyId && _ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me)) return _ctx.Me.GlobalPosition + Vector3.Up * up;
        if (_ctx.Remotes.TryGetValue(id, out var r) && GodotObject.IsInstanceValid(r.Node)) return r.Node.GlobalPosition + Vector3.Up * up;
        return null;
    }

    /// <summary>Where the demon's head looks: the nearest player's eyes within range (everyone's, the local one included), or null.</summary>
    Vector3? NearestPlayerEye(Vector3 from)
    {
        Vector3? best = null;
        var bestD = 60f * 60f;
        void Consider(Vector3 feet)
        {
            var eye = feet + new Vector3(0, 1.5f, 0);
            var d = eye.DistanceSquaredTo(from);
            if (d < bestD) { bestD = d; best = eye; }
        }
        if (_ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me)) Consider(_ctx.Me.GlobalPosition);
        foreach (var r in _ctx.Remotes.Values) if (GodotObject.IsInstanceValid(r.Node) && r.Node.Visible) Consider(r.Node.GlobalPosition);
        return best;
    }


    /// <summary>Peak of the shake at the swap, at the pastor's feet (the roar's is 0.22), and the distances it is full and gone at.</summary>
    const double QuakePeak = 0.11, QuakeBase = 0.004;
    const float QuakeFullM = 10f, QuakeGoneM = 35f;

    readonly Dictionary<ushort, double> _quakeT = new();

    public void UpdateQuake(double delta)
    {
        if (_demons.Count == 0 && _quakeT.Count == 0) return;
        var fight = _data.Fight;
        var swap = Math.Max(0.1f, fight.TransformSwapS);
        var end = Math.Max(swap + 0.1f, fight.TransformS);
        foreach (var (id, d) in _demons)
        {
            var turning = _ctx.WorldEntities.Entities.TryGetValue(id, out var e) && DemonRules.StateOf(e.State.State) == DemonState.Transforming;
            if (!turning)
            {
                EndQuake(id);
                continue;
            }
            _quakeT.TryGetValue(id, out var t);
            t += delta;
            _quakeT[id] = t;
            ChurchDecor.Current?.SetTransform((float)(t / swap));
            if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me) || !ShotFx.Enabled || GameSettings.Current.ReduceMotion) continue;
            // builds to the swap, then eases to a rumble
            var build = t < swap ? t * t / (swap * swap) : 1.0 - 0.75 * Math.Min(1.0, (t - swap) / (end - swap));
            var dist = _ctx.Me.Camera.GlobalPosition.DistanceTo(d.View.GlobalPosition);
            var f = Mathf.Clamp((dist - QuakeFullM) / (QuakeGoneM - QuakeFullM), 0f, 1f);
            var near = 1f - f * f * (3f - 2f * f);
            if (near <= 0) continue;
            _ctx.Shake.Raise((QuakeBase + QuakePeak * build) * near);
        }
        if (_quakeT.Count > _demons.Count) foreach (var id in _quakeT.Keys.Where(k => !_demons.ContainsKey(k)).ToList()) EndQuake(id);
    }

    /// <summary>A demon's turning is over (it left the Transforming state, or its view is gone): its clock goes and the church's lights are released.</summary>
    void EndQuake(ushort id)
    {
        if (_quakeT.Remove(id)) ChurchDecor.Current?.SetTransform(-1f);
    }
}
