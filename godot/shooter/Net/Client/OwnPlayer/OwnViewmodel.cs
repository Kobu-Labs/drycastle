using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The first-person weapon of the local player (a child of the camera; none for a bot or a headless client): which weapon is shown, whether it is up, its flash and kick, and the
/// own body's motion it is fed (#259: walk bob, look lag, landing dip, sprint pose, strafe lean, idle breath). Cosmetic and client-only: it reads the predicted state and the mouse,
/// and moves nothing the server or the simulation sees. Every member does nothing before the model exists, so callers need not ask.
/// </summary>
internal sealed class OwnViewmodel
{
    readonly ClientContext _ctx;
    WeaponViewmodel? _model;

    public OwnViewmodel(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The own footstep sound's accumulator (set by the host): the bob's dips land on the sound.</summary>
    public Func<float> StepAcc = () => 0f;

    /// <summary>The welcome made the body: the weapon goes on its camera (not for a bot or a headless client).</summary>
    public void Create()
    {
        if (_ctx.Bot || !ShotFx.Enabled) return;
        _model = new WeaponViewmodel { Context = Motion, CameraDrop = drop => { if (_ctx.Me != null) _ctx.Me.ViewDip = drop; } };
        _ctx.Me!.Camera.AddChild(_model);
    }

    /// <summary>Shows the weapon <paramref name="id"/> (the rocket head and the like are posed on <see cref="Held"/>).</summary>
    public void Show(string id) => _model?.Show(id);

    /// <summary>The node of the weapon in hand; null when none is shown.</summary>
    public Node3D? Held => _model?.Current;

    /// <summary>Where the muzzle is in the world while the weapon is shown; null otherwise.</summary>
    public Vector3? MuzzlePosition => _model?.MuzzlePosition;

    public bool Visible
    {
        get => _model?.Visible ?? false;
        set { if (_model != null) _model.Visible = value; }
    }

    /// <summary>Iron sights: the weapon is brought up to the eye.</summary>
    public bool Aiming
    {
        get => _model?.Aiming ?? false;
        set { if (_model != null) _model.Aiming = value; }
    }

    /// <summary>How far the weapon is up to the eye, 0 to 1.</summary>
    public float AimAmount => _model?.AimAmount ?? 0f;

    public void Flash(float scale) => _model?.Flash(scale);
    public void Kick(float degrees) => _model?.Kick(degrees);

    /// <summary>
    /// The viewmodel's input for this frame, asked from its own <c>_Process</c>. The state is the 30 Hz predicted one, so the landing is the
    /// first frame after the tick that put the body on the ground; the fall speed is the fastest of the stretch in the air, so a slow frame cannot lose it.
    /// The motion is on for the on-foot body only: the viewmodel is also shown in an armed vehicle seat, where the body is not simulated and its state is stale.
    /// </summary>
    ViewmodelInput Motion()
    {
        if (_ctx.Me == null) return default;
        var look = _ctx.Me.TakeLook(); // always taken, so a turn made while the gun was away does not play when it returns
        if (_model is not { Visible: true } || _ctx.Driving || _ctx.View.MyState == EntityState.InVehicle) return default;
        var s = _ctx.Me.State;
        var (sin, cos) = MathF.SinCos(_ctx.Me.Yaw);
        var swimming = MovementStep.IsSwimming(s);
        var grounded = s.OnFloor && !s.OnLadder && !swimming;
        return new ViewmodelInput
        {
            Active = true,
            Grounded = grounded,
            Falling = !s.OnFloor && !s.OnLadder && !swimming,
            Speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z),
            StrafeSpeed = s.Velocity.X * cos - s.Velocity.Z * sin, // along the view's right, (cos, 0, -sin)
            VerticalSpeed = s.Velocity.Y,
            Stance = s.Stance,
            Sprinting = s.WasSprinting,
            LookYaw = look.X,
            LookPitch = look.Y,
            StepAcc = StepAcc(),
            HasStepAcc = Sfx.Enabled,
            ReduceMotion = GameSettings.Current.ReduceMotion,
        };
    }
}
