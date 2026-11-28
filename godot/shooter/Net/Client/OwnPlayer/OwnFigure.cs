using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Movement;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The local player's own body on foot, as a figure the camera does not draw (<see cref="CharacterView.ShadowOnly"/>): only its shadow is seen, so the first-person
/// player has a body on the ground and walls in the sun (#319). Posed from the predicted state: stance, swim, walk and run, the aim's pitch and the weapon in hand.
/// Cosmetic and client-only; none for a bot or a headless client. Seated, the rider's own figure (<c>RiderCamera.SelfView</c>) is the body instead. While the camera is pulled back (<see cref="Seen"/>, the Job-done
/// view) a second figure of the same pose that the camera does draw stands in its place; a shadow-only figure cannot be made visible again, so that one is made once, on the first pull-back, and hidden after.
/// </summary>
internal sealed class OwnFigure
{
    readonly ClientContext _ctx;
    readonly Action<CharacterView> _dress;
    Player? _on;
    /// <summary>The figure the camera draws (made on the first <see cref="Seen"/>).</summary>
    CharacterView? _seen;

    /// <summary>The camera is pulled back from the body: the player sees their own figure.</summary>
    public bool Seen { get; set; }

    public OwnFigure(ClientContext ctx, Action<CharacterView> dress) { _ctx = ctx; _dress = dress; }

    /// <summary>The figure, or null before the first frame it was shown.</summary>
    public CharacterView? View { get; private set; }

    /// <summary>Once per frame: <paramref name="show"/> false hides it (dead, seated, flung, at a turret); <paramref name="weaponId"/> is what the hand holds; <paramref name="filling"/> the player fills a Duffel (the shadow bends and reaches).</summary>
    public void Update(bool show, string weaponId, bool filling = false)
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
            _seen = null; // went with the old body
            _dress(View);
            View.ShadowOnly = true;
        }
        View.Visible = show;
        if (_seen != null && GodotObject.IsInstanceValid(_seen)) _seen.Visible = show && Seen;
        if (!show) return;
        Pose(View, me, weaponId, filling);
        if (!Seen) return;
        if (_seen == null || !GodotObject.IsInstanceValid(_seen))
        {
            _seen = CharacterView.Make();
            _seen.TopLevel = true;
            me.AddChild(_seen);
            _dress(_seen);
        }
        _seen.Visible = true;
        Pose(_seen, me, weaponId, filling);
    }

    static void Pose(CharacterView view, Player me, string weaponId, bool filling)
    {
        var s = me.State;
        view.GlobalTransform = new Transform3D(new Basis(Vector3.Up, me.Yaw), me.GlobalPosition + me.ViewOffset); // with the eye over a reconcile
        view.SetStance(s.Stance);
        view.SetSwim(MovementStep.IsSwimming(s));
        view.SetAimPitch(me.Pitch);
        view.SetFilling(filling);
        view.Apply(MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z), false, false, weaponId);
    }
}
