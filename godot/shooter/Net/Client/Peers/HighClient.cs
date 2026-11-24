using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Status;

namespace Drycastle.Shooter.Net.Client.Peers;

/// <summary>
/// The high on the client (GLOSSARY "High", plantation.json): the server says who is high with <see cref="StatusKind.High"/> in the status mask (<see cref="StatusClient.OnEvent"/>). Everyone's figure shows it
/// (<see cref="CharacterView.SetHigh"/>, called from <see cref="StatusClient.SyncFx"/>); the local player's own camera gets the nausea (<see cref="Nausea"/>): a slow sway of the real look yaw and pitch (the angles that
/// are sent, so shots go where the swayed crosshair points; the server need not know), a roll and a breathing FOV that are only drawn. It runs only while the camera is the body's own on foot: not at a cut-scene camera,
/// a turret or missile view, a seat, the Bigfoot's hand, the Mass, the demon's pin or the death view. The reduce-motion setting leaves a small share of it. Nothing is shown for the hold itself: no prompt, no ring.
/// </summary>
public sealed class HighClient
{
    readonly ClientContext _ctx;
    readonly StatusClient _status;
    readonly PlantationFile _plantation;
    readonly Func<ClientModeInputs> _mode;
    readonly Func<bool> _ownSpawned;

    public HighClient(ClientContext ctx, StatusClient status, PlantationFile plantation, Func<ClientModeInputs> mode, Func<bool> ownSpawned)
    {
        _ctx = ctx;
        _status = status;
        _plantation = plantation;
        _mode = mode;
        _ownSpawned = ownSpawned;
    }

    /// <summary>The nausea's roll of the first-person camera, composed with the view roll (zero when it is off).</summary>
    public float Roll { get; private set; }

    Nausea? _nausea;

    Node3D? _ownCigarette;      // the first-person copy on the camera (the figure itself is not drawn in the own view)
    CpuParticles3D? _ownSmoke;
    Node3D? _ownTip;

    /// <summary>
    /// The high player's own cigarette: a copy fixed to the first-person camera where the mouth is, so the tip, its ember and the smoke show at the bottom of the view (plantation.json
    /// <c>view.cigarette.first_person</c>). Only while the view is the body's own eye on foot and no scope is up; gone at once otherwise (the smoke with it).
    /// </summary>
    void UpdateOwnCigarette(bool show)
    {
        if (!show || _ctx.Me == null)
        {
            if (_ownCigarette != null && GodotObject.IsInstanceValid(_ownCigarette)) _ownCigarette.QueueFree();
            _ownCigarette = null; _ownSmoke = null; _ownTip = null;
            return;
        }
        if (_ownCigarette != null && GodotObject.IsInstanceValid(_ownCigarette)) { FollowOwnSmoke(); return; }
        var c = _plantation.View.Cigarette;
        var fp = c.FirstPerson;
        // the model points +Z, the camera looks down -Z: turned round, then tilted
        var tilt = Basis.FromEuler(new Vector3(Mathf.DegToRad((float)fp.RotationDeg[0]), Mathf.DegToRad((float)fp.RotationDeg[1]), Mathf.DegToRad((float)fp.RotationDeg[2])));
        _ownCigarette = new Node3D
        {
            Name = "OwnCigarette",
            Transform = new Transform3D(tilt * new Basis(Vector3.Up, Mathf.Pi), new Vector3((float)fp.Offset[0], (float)fp.Offset[1], (float)fp.Offset[2])),
        };
        _ownCigarette.AddChild(CharacterView.CigaretteModel(c, out var tip));
        _ownTip = tip;
        _ctx.Me.Camera.AddChild(_ownCigarette);
        if (!ShotFx.Enabled) return;
        // the same small warm light the figure has for the others (it is not drawn in the own view, so its light is not there either): it lights the weapon in the hands and what is near
        tip.AddChild(new OmniLight3D
        {
            Name = "OwnCigaretteGlow", LightColor = new Color(c.Light.Colour), LightEnergy = (float)c.Light.Energy, OmniRange = (float)c.Light.Range, ShadowEnabled = false,
        });
        // The smoke travels with the player but does not turn with the look: world-space smoke would be left behind the camera at the first step, and smoke fixed to the camera would swing round with
        // the head. So the emitter is top-level (it keeps the world's axes), follows the tip each frame, and its particles are local to it. Started only once it is in the tree.
        _ownSmoke = CharacterView.CigaretteSmoke(c.Smoke, fp.SmokeAmount, (float)fp.SmokeSizeM);
        _ownSmoke.LocalCoords = true;
        _ownSmoke.TopLevel = true;
        _ownCigarette.AddChild(_ownSmoke);
        FollowOwnSmoke();
        ParticleFx.Run(_ownSmoke, true);
    }

    void FollowOwnSmoke()
    {
        if (_ownSmoke == null || _ownTip == null || !GodotObject.IsInstanceValid(_ownSmoke) || !GodotObject.IsInstanceValid(_ownTip)) return;
        _ownSmoke.GlobalTransform = new Transform3D(Basis.Identity, _ownTip.GlobalPosition);
    }

    /// <summary>The local player is high according to the last status message.</summary>
    public bool OwnHigh => _status.Has(_ctx.MyId, StatusKind.High);

    /// <summary>Once per frame, before the roll is composed (<see cref="FadeOwnCorrection"/>): steps the nausea and applies its look change, roll and FOV to the first-person camera.</summary>
    public void Update(double delta)
    {
        if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me)) return;
        if (_ctx.Dead || !_ownSpawned())
        {
            UpdateOwnCigarette(false);
            if (_nausea != null) { _nausea = null; Roll = 0; _ctx.Me.FovWobble = 0; } // dead: the high is gone with the life (the status bit too); nothing lingers into the respawn
            return;
        }
        var high = OwnHigh;
        UpdateOwnCigarette(high && _mode().OnFoot && _ctx.Me.ZoomFov <= 0); // OnFoot is the same test the sway uses (CutCamera is always set: it is the one composed hook, not a sign of a cut-scene)
        if (!high && _nausea is not { Active: true }) { Roll = 0; return; }
        _nausea ??= new Nausea(_plantation.Nausea);
        var owned = _mode().OnFoot;
        var (dYaw, dPitch) = _nausea.Step((float)delta, high, owned, GameSettings.Current.ReduceMotion);
        _ctx.Me.NudgeLook(dYaw, dPitch);
        var (roll, fov) = _nausea.Visual;
        Roll = owned ? roll : 0f;
        _ctx.Me.FovWobble = owned ? fov : 0f;
    }
}
