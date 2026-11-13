using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Water on the client: splashes when the local player, a remote player or a rocket goes into or out of the water,
/// the remote swim pose (lowered into the water, the slow walk, body leaned forward), bullets that meet the surface,
/// wake rings and wading footstep splashes, and the visual bob of boats on the waves. Runs after the remotes and rockets
/// are placed each frame. Pure rules are in <see cref="UnderwaterFx"/>; the waves are <see cref="WaterWaves"/>.
/// </summary>
public partial class NetClient
{
    bool _meInWater;
    float _meWadeAcc;
    double _meRippleDue;
    readonly Dictionary<ushort, bool> _remoteInWater = new();
    readonly Dictionary<ushort, bool> _rocketInWater = new();
    readonly List<ushort> _staleWater = new(); // reused each frame to drop the swimmers who left (#173, T3-033)
    WaterRipples? _ripples;

    /// <summary>The ring pool, under <see cref="Owned"/> (so it is freed on leave with everything else in it).</summary>
    WaterRipples Ripples
    {
        get
        {
            if (_ripples == null || !GodotObject.IsInstanceValid(_ripples)) { _ripples = new WaterRipples { Name = "WaterRipples" }; Owned.AddChild(_ripples); }
            return _ripples;
        }
    }

    void RenderWater()
    {
        if (Water.Bodies.Count == 0) return;
        var dt = (float)GetProcessDeltaTime();
        if (_me != null)
        {
            var feet = _me.GlobalPosition;
            var depth = Water.DepthAt(new NVec3(feet.X, feet.Y, feet.Z));
            var inWater = depth > 0f;
            if (UnderwaterFx.WaterCrossing(_meInWater, inWater) != UnderwaterFx.Crossing.None) SplashAt(feet);
            _meInWater = inWater;
            if (!_driving && !Dead)
            {
                var vel = _me.State.Velocity;
                WaterWalk(feet, depth, MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z), _me.State.OnFloor, ref _meRippleDue, ref _meWadeAcc, dt);
            }
        }
        foreach (var (id, r) in _remotes)
        {
            if (r.Node == null || !r.Node.Visible) { _remoteInWater.Remove(id); continue; }
            var feet = r.Node.GlobalPosition;
            var depth = Water.DepthAt(new NVec3(feet.X, feet.Y, feet.Z));
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
            foreach (var k in _remoteInWater.Keys) if (!_remotes.ContainsKey(k)) _staleWater.Add(k);
            foreach (var k in _staleWater) _remoteInWater.Remove(k);
        }
        foreach (var (id, rk) in _rockets)
        {
            if (rk.Node == null || !GodotObject.IsInstanceValid(rk.Node)) continue;
            var at = rk.Node.GlobalPosition;
            var inWater = Water.DepthAt(new NVec3(at.X, at.Y, at.Z)) > 0f;
            if (_rocketInWater.TryGetValue(id, out var was) && UnderwaterFx.WaterCrossing(was, inWater) == UnderwaterFx.Crossing.Enter)
                SplashAt(at, 0.4f);
            _rocketInWater[id] = inWater;
        }
        foreach (var id in _rocketInWater.Keys.Where(k => !_rockets.ContainsKey(k)).ToList()) _rocketInWater.Remove(id);
    }

    /// <summary>
    /// Wake rings and wading splashes for one walker at <paramref name="feet"/> with its water <paramref name="depth"/>. A
    /// ring spreads every 0.6 s while it moves through water at least ankle deep; a droplet burst lands with each footstep
    /// while the feet are in water shallower than a swim (wading). Nothing when the feet are dry.
    /// </summary>
    void WaterWalk(Vector3 feet, float depth, float speed, bool grounded, ref double rippleDue, ref float wadeAcc, float dt)
    {
        if (depth <= 0f) { wadeAcc = 0f; return; }
        var at = new Vector3(feet.X, Water.SurfaceY(feet.X, feet.Z), feet.Z);
        if (depth >= 0.3f && speed >= 1f && Now >= rippleDue)
        {
            Ripples.Emit(at, 0.8f);
            rippleDue = Now + 0.6;
        }
        if (depth < WaterConsts.SwimDepth && SoundRules.FootstepDue(ref wadeAcc, speed, dt, grounded)) WaterFx.Footstep(Owned, at);
    }

    /// <summary>
    /// A boat, or the amphibious transport afloat: its visible parts bob and tilt on the waves (<see cref="WaterWaves"/>).
    /// Only the visual moves; the root stays on the flat surface, which physics and hit detection use. Moving afloat, it
    /// leaves a wake ring.
    /// </summary>
    void WaterCraft(CarView v, Vector3 p)
    {
        var def = Vehicles?.Vehicles[v.Kind];
        if (def is not { Medium: VehicleMedium.Boat or VehicleMedium.Amphibious }) return; // land and air vehicles have no waves
        var visual = v.Visual ??= v.Node.GetNodeOrNull<Node3D>(CarFx.VisualName);
        var body = Water.BodyAt(p.X, p.Z);
        if (body == null || MathF.Abs(p.Y - body.SurfaceY) > 0.5f)
        {
            if (visual != null && visual.Position != Vector3.Zero) { visual.Position = Vector3.Zero; visual.Rotation = Vector3.Zero; }
            return;
        }
        var t = (float)WaterSurfaces.WaveClock;
        var amp = WaterPalettes.WaveAmplitude(body);
        if (visual != null)
        {
            visual.Position = new Vector3(0f, WaterWaves.Height(p.X, p.Z, t, amp), 0f);
            var (dhdx, dhdz) = WaterWaves.Slope(p.X, p.Z, t, amp);
            // the hull's normal follows the surface: pitch from the slope along z, roll from the slope along x
            visual.Rotation = new Vector3(-MathF.Atan(dhdz), 0f, MathF.Atan(dhdx));
        }
        if (!v.Destroyed && v.Kmh >= 3.6f && Now >= v.RippleDue)
        {
            Ripples.Emit(new Vector3(p.X, body.SurfaceY, p.Z), 2.2f, 1.4f);
            v.RippleDue = Now + 0.5;
        }
    }

    /// <summary>Splash at the surface directly below <paramref name="p"/> (the body's own splash sound and droplets).</summary>
    void SplashAt(Vector3 p, float size = 1f)
    {
        var surface = Water.SurfaceY(p.X, p.Z);
        if (float.IsNaN(surface)) return;
        WaterFx.Splash(Owned, new Vector3(p.X, surface, p.Z), size);
    }

    /// <summary>A bullet that crosses a lake surface on its way to the target: a small splash where it meets the water.</summary>
    void BulletSurfaceHit(Vector3 from, Vector3 to)
    {
        if (Water.Bodies.Count == 0) return;
        if (UnderwaterFx.FirstSurfaceCrossing(Water, new NVec3(from.X, from.Y, from.Z), new NVec3(to.X, to.Y, to.Z), out var hit))
            WaterFx.Splash(Owned, new Vector3(hit.X, hit.Y, hit.Z), 0.4f);
    }
}
