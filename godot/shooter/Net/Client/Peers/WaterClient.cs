using Godot;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Waters;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using NVec3 = System.Numerics.Vector3;
using WaterPalettes = Drycastle.MapLoader.Waters.WaterPalettes;
using WaterSurfaces = Drycastle.MapLoader.Waters.WaterSurfaces;

namespace Drycastle.Shooter.Net.Client.Peers;

/// <summary>
/// Water on the client: splashes when the local player, a remote player or a rocket goes into or out of the water,
/// the remote swim pose (lowered into the water, the slow walk, body leaned forward), bullets that meet the surface,
/// wake rings and wading footstep splashes, and the visual bob of boats on the waves. Runs after the remotes and rockets
/// are placed each frame. Pure rules are in <see cref="UnderwaterFx"/>; the waves are <see cref="WaterWaves"/>.
/// </summary>
internal sealed class WaterClient
{
    readonly ClientContext _ctx;

    public WaterClient(ClientContext ctx) { _ctx = ctx; }

    bool _meInWater;
    float _meWadeAcc;
    double _meRippleDue;
    readonly Dictionary<ushort, bool> _remoteInWater = new();
    readonly Dictionary<ushort, bool> _rocketInWater = new();
    readonly List<ushort> _staleWater = new(); // reused each frame to drop the swimmers who left (#173, T3-033)
    WaterRipples? _ripples;

    /// <summary>The ring pool, under <see cref="ClientContext.Owned"/> (so it is freed on leave with everything else in it).</summary>
    WaterRipples Ripples
    {
        get
        {
            if (_ripples == null || !GodotObject.IsInstanceValid(_ripples)) { _ripples = new WaterRipples { Name = "WaterRipples" }; _ctx.Owned.AddChild(_ripples); }
            return _ripples;
        }
    }

    public void Render()
    {
        if (_ctx.Water.Bodies.Count == 0) return;
        var dt = (float)_ctx.Root.GetProcessDeltaTime();
        if (_ctx.Me != null)
        {
            var feet = _ctx.Me.GlobalPosition;
            var depth = _ctx.Water.DepthAt(new NVec3(feet.X, feet.Y, feet.Z));
            var inWater = !_ctx.Driving && depth > 0f; // a rider is in the vehicle, not in the water: its own splash is the vehicle's
            if (UnderwaterFx.WaterCrossing(_meInWater, inWater) != UnderwaterFx.Crossing.None) SplashAt(feet);
            _meInWater = inWater;
            if (!_ctx.Driving && !_ctx.Dead)
            {
                var vel = _ctx.Me.State.Velocity;
                WaterWalk(feet, depth, MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z), _ctx.Me.State.OnFloor, ref _meRippleDue, ref _meWadeAcc, dt);
            }
        }
        foreach (var (id, r) in _ctx.Remotes)
        {
            if (r.Node == null || !r.Node.Visible) { _remoteInWater.Remove(id); continue; }
            if (r.State == EntityState.InVehicle) { _remoteInWater.Remove(id); r.View?.SetSwim(false); continue; } // a rider neither splashes nor swims, however low the hull sits
            var feet = r.Node.GlobalPosition;
            var depth = _ctx.Water.DepthAt(new NVec3(feet.X, feet.Y, feet.Z));
            var inWater = depth > 0f;
            if (_remoteInWater.TryGetValue(id, out var was)) { if (UnderwaterFx.WaterCrossing(was, inWater) != UnderwaterFx.Crossing.None) SplashAt(feet); }
            _remoteInWater[id] = inWater;
            // swimming (deep enough to swim): floating at the surface, leaned forward, the slow walk; wading stays upright
            r.View?.SetSwim(depth >= WaterConsts.SwimDepth);
            if (r.State is not (EntityState.Dead or EntityState.InVehicle)) WaterWalk(feet, depth, r.Speed, true, ref r.RippleDue, ref r.WadeAcc, dt);
        }
        if (_remoteInWater.Count > 0)
        {
            _staleWater.Clear();
            foreach (var k in _remoteInWater.Keys) if (!_ctx.Remotes.ContainsKey(k)) _staleWater.Add(k);
            foreach (var k in _staleWater) _remoteInWater.Remove(k);
        }
        foreach (var (id, rk) in _ctx.Rockets)
        {
            if (rk.Node == null || !GodotObject.IsInstanceValid(rk.Node)) continue;
            var at = rk.Node.GlobalPosition;
            var inWater = _ctx.Water.DepthAt(new NVec3(at.X, at.Y, at.Z)) > 0f;
            if (_rocketInWater.TryGetValue(id, out var was) && UnderwaterFx.WaterCrossing(was, inWater) == UnderwaterFx.Crossing.Enter)
                SplashAt(at, 0.4f);
            _rocketInWater[id] = inWater;
        }
        foreach (var id in _rocketInWater.Keys.Where(k => !_ctx.Rockets.ContainsKey(k)).ToList()) _rocketInWater.Remove(id);
    }

    /// <summary>
    /// Wake rings and wading splashes for one walker at <paramref name="feet"/> with its water <paramref name="depth"/>. A
    /// ring spreads every 0.6 s while it moves through water at least ankle deep; a droplet burst lands with each footstep
    /// while the feet are in water shallower than a swim (wading). Nothing when the feet are dry.
    /// </summary>
    void WaterWalk(Vector3 feet, float depth, float speed, bool grounded, ref double rippleDue, ref float wadeAcc, float dt)
    {
        if (depth <= 0f) { wadeAcc = 0f; return; }
        var body = _ctx.Water.BodyAt(feet.X, feet.Z);
        if (body == null) return;
        var at = new Vector3(feet.X, body.SurfaceY + WaterPalettes.WaveOffset(body, feet.X, feet.Z), feet.Z);
        if (depth >= 0.3f && speed >= 1f && _ctx.Now >= rippleDue)
        {
            Ripples.Emit(at, 0.8f);
            rippleDue = _ctx.Now + 0.6;
        }
        if (depth < WaterConsts.SwimDepth && SoundRules.FootstepDue(ref wadeAcc, speed, dt, grounded)) WaterFx.Footstep(_ctx.Owned, at);
    }

    /// <summary>
    /// A boat, or the amphibious transport afloat: its visible parts bob and tilt on the waves (<see cref="WaterWaves"/>).
    /// Only the visual moves; the root stays on the flat surface, which physics and hit detection use. Moving afloat, it
    /// leaves a wake ring.
    /// </summary>
    public void Craft(CarView v, Vector3 p)
    {
        var def = _ctx.Vehicles?.Vehicles[v.Kind];
        if (def is not { Medium: VehicleMedium.Amphibious }) return; // land and air vehicles have no waves
        var visual = v.Visual ??= v.Node.GetNodeOrNull<Node3D>(CarFx.VisualName);
        var body = _ctx.Water.BodyAt(p.X, p.Z);
        if (body == null || MathF.Abs(p.Y - body.SurfaceY) > 0.5f)
        {
            if (visual != null && visual.Position != Vector3.Zero) { visual.Position = Vector3.Zero; visual.Rotation = Vector3.Zero; }
            return;
        }
        var t = (float)WaterSurfaces.WaveClock;
        var amp = WaterPalettes.WaveAmplitude(body) * WaterPalettes.WaveDamp(body, p.X, p.Z);
        if (visual != null)
        {
            visual.Position = new Vector3(0f, WaterWaves.Height(p.X, p.Z, t, amp), 0f);
            var (dhdx, dhdz) = WaterWaves.Slope(p.X, p.Z, t, amp);
            // the hull's normal follows the surface: pitch from the slope along z, roll from the slope along x
            visual.Rotation = new Vector3(-MathF.Atan(dhdz), 0f, MathF.Atan(dhdx));
        }
        if (!v.Destroyed && v.Kmh >= 3.6f && _ctx.Now >= v.RippleDue)
        {
            Ripples.Emit(new Vector3(p.X, body.SurfaceY + WaterWaves.Height(p.X, p.Z, t, amp), p.Z), 2.2f, 1.4f);
            v.RippleDue = _ctx.Now + 0.5;
        }
    }

    /// <summary>Splash at the surface directly below <paramref name="p"/> (the body's own splash sound and droplets).</summary>
    void SplashAt(Vector3 p, float size = 1f)
    {
        var surface = _ctx.Water.SurfaceY(p.X, p.Z);
        if (float.IsNaN(surface)) return;
        WaterFx.Splash(_ctx.Owned, new Vector3(p.X, surface, p.Z), size);
    }

    /// <summary>A bullet that crosses a lake surface on its way to the target: a small splash where it meets the water.</summary>
    public void BulletSurfaceHit(Vector3 from, Vector3 to)
    {
        if (_ctx.Water.Bodies.Count == 0) return;
        if (UnderwaterFx.FirstSurfaceCrossing(_ctx.Water, new NVec3(from.X, from.Y, from.Z), new NVec3(to.X, to.Y, to.Z), out var hit))
            WaterFx.Splash(_ctx.Owned, new Vector3(hit.X, hit.Y, hit.Z), 0.4f);
    }
}
