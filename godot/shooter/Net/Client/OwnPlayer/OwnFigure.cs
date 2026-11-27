using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Movement;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The local player's own body on foot, as a figure the camera does not draw (<see cref="CharacterView.ShadowOnly"/>): only its shadow is seen, so the first-person
/// player has a body on the ground and walls in the sun (#319). Posed from the predicted state: stance, swim, walk and run, the aim's pitch and the weapon in hand.
/// Cosmetic and client-only; none for a bot or a headless client. Seated, the rider's own figure (<c>RiderCamera.SelfView</c>) is the body instead.
/// </summary>
internal sealed class OwnFigure
{
    readonly ClientContext _ctx;
    readonly Action<CharacterView> _dress;
    Player? _on;

    public OwnFigure(ClientContext ctx, Action<CharacterView> dress) { _ctx = ctx; _dress = dress; }

    /// <summary>The figure, or null before the first frame it was shown.</summary>
    public CharacterView? View { get; private set; }

    /// <summary>Once per frame: <paramref name="show"/> false hides it (dead, seated, flung, at a turret); <paramref name="weaponId"/> is what the hand holds.</summary>
    public void Update(bool show, string weaponId)
    {
        var me = _ctx.Me;
        if (me == null || _ctx.Bot || !ShotFx.Enabled) return;
        if (View == null || _on != me || !GodotObject.IsInstanceValid(View))
        {
            if (!show) return;
            View = CharacterView.Make();
            View.TopLevel = true; // placed in the world each frame; freed with the body
            me.AddChild(View);
            _on = me;
            _dress(View);
            View.ShadowOnly = true;
        }
        View.Visible = show;
        if (!show) return;
        var s = me.State;
        View.GlobalTransform = new Transform3D(new Basis(Vector3.Up, me.Yaw), me.GlobalPosition + me.ViewOffset); // with the eye over a reconcile
        View.SetStance(s.Stance);
        View.SetSwim(MovementStep.IsSwimming(s));
        View.SetAimPitch(me.Pitch);
        View.Apply(MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z), false, false, weaponId);
    }
}
