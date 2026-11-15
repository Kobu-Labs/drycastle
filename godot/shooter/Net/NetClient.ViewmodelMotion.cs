using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Feeds the first-person weapon's motion (#259: walk bob, look lag, landing dip, sprint pose, strafe lean, idle breath) with what the own body
/// is doing. Cosmetic and client-only: it reads the predicted state and the mouse, and moves nothing the server or the simulation sees.
/// </summary>
public partial class NetClient
{
    WeaponViewmodel MakeViewmodel() => new() { Context = ViewmodelContext, CameraDrop = drop => { if (_me != null) _me.ViewDip = drop; } };

    /// <summary>
    /// The viewmodel's input for this frame, asked from its own <c>_Process</c>. The state is the 30 Hz predicted one, so the landing is the
    /// first frame after the tick that put the body on the ground; the fall speed is the fastest of the stretch in the air, so a slow frame cannot lose it.
    /// The motion is on for the on-foot body only: the viewmodel is also shown in an armed vehicle seat, where the body is not simulated and its state is stale.
    /// </summary>
    ViewmodelInput ViewmodelContext()
    {
        if (_me == null) return default;
        var look = _me.TakeLook(); // always taken, so a turn made while the gun was away does not play when it returns
        if (_viewmodel is not { Visible: true } || _driving || _myState == EntityState.InVehicle) return default;
        var s = _me.State;
        var (sin, cos) = MathF.SinCos(_me.Yaw);
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
            StepAcc = _sfxStepAcc, // the own footstep sound's accumulator (NetClient.Sound.cs): the bob's dips land on the sound
            HasStepAcc = Sfx.Enabled,
            ReduceMotion = GameSettings.Current.ReduceMotion,
        };
    }
}
