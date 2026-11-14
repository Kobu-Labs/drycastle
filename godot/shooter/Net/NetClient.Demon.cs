using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The demon of the Holy Mass on the client: the world entity of type <see cref="DemonRules.TypeId"/> is a <see cref="DemonView"/> placed from the interpolated
/// state (position, the way it faces, the surface normal in the owner field, the state byte, the share of hits left in the hp byte); its fireballs are events the client
/// flies itself (<see cref="EventKind.DemonFireball"/> starts the arc, <see cref="EventKind.DemonImpact"/> ends it with a burst). The server decides everything.
/// </summary>
public partial class NetClient
{
    /// <summary>holy_mass_demon.json: set by the host (gravity of the fireballs, the turning's timeline).</summary>
    public DemonFile DemonData = new();

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

    void InitDemon()
    {
        DemonRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnDemonSpawned;
        WorldEntities.Despawned += OnDemonDespawned;
    }

    void OnDemonSpawned(WorldReplica.Entity e)
    {
        if (e.Type != DemonRules.TypeId) return;
        if (DemonData.Hits.Base <= 0)
        {
            GD.PrintErr($"client: demon {e.Id} but holy_mass_demon.json is missing; not drawn (data files differ from the server's?)");
            return;
        }
        var view = DemonView.Make(DemonData, HolyMassData.Mass);
        view.RoarCue = at => ShakeFor(at, Math.Max(1f, DemonData.Roar.ShakeRadiusM) / 5f);
        Owned.AddChild(view);
        ChurchDecor.Current?.SetDemon(true, snap: DemonRules.StateOf(e.State.State) != DemonState.Transforming); // a joiner finds the cross turned and the lights out
        _demons[e.Id] = new DemonNode { View = view, Hp = e.State.Hp };
    }

    void OnDemonDespawned(ushort id)
    {
        if (_demons.Remove(id, out var d)) d.View.QueueFree();
        if (_demons.Count == 0) ChurchDecor.Current?.SetDemon(false);
        if (_demons.Count == 0) foreach (var b in _balls.Values) RocketFx.Retire(Owned, b.Node);
        if (_demons.Count == 0) _balls.Clear();
    }

    void OnDemonEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.DemonFireball when DemonCodec.DecodeFireball(payload) is { } f:
                var node = DemonFx.MakeFireball(Owned);
                var o = new Vector3(f.Origin.X, f.Origin.Y, f.Origin.Z);
                node.Visible = ChurchInterior.Visible;
                node.GlobalPosition = o;
                if (_balls.Remove(f.Id, out var old)) RocketFx.Retire(Owned, old.Node);
                _balls[f.Id] = new BallNode { Node = node, Origin = o, Velocity = new Vector3(f.Velocity.X, f.Velocity.Y, f.Velocity.Z), Start = Time.GetTicksMsec() / 1000.0 };
                foreach (var d in _demons.Values) d.View.Fling();
                break;
            case EventKind.DemonImpact when DemonCodec.DecodeImpact(payload) is { } i:
                var at = new Vector3(i.At.X, i.At.Y, i.At.Z);
                if (_balls.Remove(i.Id, out var ball)) RocketFx.Retire(Owned, ball.Node);
                if (i.Fizzled) break;
                var r = DemonData.Fireball.Splash.RadiusM;
                RocketFx.Explode(Owned, at, r);
                ShakeFor(at, r);
                break;
            case EventKind.DemonFlame when DemonCodec.DecodeFlame(payload) is { } target:
                foreach (var d in _demons.Values) d.FlameTarget = target;
                break;
            case EventKind.DemonCursed:
                UiShell.Instance?.Toast(Strings.T("mass.cursed"), ToastKind.Error, null, 5.0);
                break;
        }
    }

    /// <summary>Places the demon (interpolated, a little in the past like every remote body), shows what it does, and flies the fireballs.</summary>
    void RenderDemon()
    {
        if (_lastTick == 0) return;
        var dt = GetProcessDeltaTime();
        if (_demons.Count > 0)
        {
            var rt = RenderTickNow();
            foreach (var (id, d) in _demons)
            {
                if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
                var view = d.View;
                view.FloorY = _massNow.ChantAt.Y - 1.5f; // the pastor's spot is the floor
                if (e.Buf.At(rt) is { } s) // placed before the state is shown: one that is dead from the start falls from where it hangs
                {
                    var n = DemonRules.UnpackNormal(e.State.Owner);
                    var f = DemonRules.Facing(s.Yaw, s.Pitch);
                    view.Place(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), new Vector3(n.X, n.Y, n.Z), new Vector3(f.X, f.Y, f.Z), dt);
                }
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
        var g = DemonData.Fireball.GravityMps2;
        List<ushort>? gone = null;
        foreach (var (id, b) in _balls)
        {
            var t = (float)(now - b.Start);
            if (t > DemonData.Fireball.LifeS + 1f || !GodotObject.IsInstanceValid(b.Node)) { (gone ??= new()).Add(id); continue; }
            b.Node.GlobalPosition = b.Origin + b.Velocity * t + new Vector3(0, -0.5f * g * t * t, 0);
            b.Node.Visible = ChurchInterior.Visible;
        }
        if (gone != null) foreach (var id in gone) { if (_balls.Remove(id, out var b)) RocketFx.Retire(Owned, b.Node); }
    }

    /// <summary>A point <paramref name="up"/> metres above the feet of a player (the local one or a remote), or null when they are not here.</summary>
    Vector3? PlayerPoint(ushort id, float up)
    {
        if (id == _myId && _me != null && GodotObject.IsInstanceValid(_me)) return _me.GlobalPosition + Vector3.Up * up;
        if (_remotes.TryGetValue(id, out var r) && GodotObject.IsInstanceValid(r.Node)) return r.Node.GlobalPosition + Vector3.Up * up;
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
        if (_me != null && GodotObject.IsInstanceValid(_me)) Consider(_me.GlobalPosition);
        foreach (var r in _remotes.Values) if (GodotObject.IsInstanceValid(r.Node) && r.Node.Visible) Consider(r.Node.GlobalPosition);
        return best;
    }
}
