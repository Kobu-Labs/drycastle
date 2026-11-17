using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The high on the client (GLOSSARY "High", plantation.json): the server says who is high with <see cref="StatusKind.High"/> in the status mask (<see cref="NetClient.OnStatusEvent"/>). Everyone's figure shows it
/// (<see cref="CharacterView.SetHigh"/>, called from <see cref="NetClient.SyncStatusFx"/>); the local player's own camera gets the nausea (<see cref="Nausea"/>): a slow sway of the real look yaw and pitch (the angles that
/// are sent, so shots go where the swayed crosshair points; the server need not know), a roll and a breathing FOV that are only drawn. It runs only while the camera is the body's own on foot: not at a cut-scene camera,
/// a turret or missile view, a seat, the Bigfoot's hand, the Mass, the demon's pin or the death view. The reduce-motion setting leaves a small share of it. Nothing is shown for the hold itself: no prompt, no ring.
/// </summary>
public partial class NetClient
{
    /// <summary>plantation.json (the host sets it from the data directory; the shipped defaults otherwise).</summary>
    public PlantationFile Plantation = PlantationFile.Default;

    Nausea? _nausea;
    float _nauseaRoll;
    Node3D? _ownCigarette;      // the first-person copy on the camera (the figure itself is not drawn in the own view)
    CpuParticles3D? _ownSmoke;
    Node3D? _ownTip;

    /// <summary>
    /// The high player's own cigarette: a copy fixed to the first-person camera where the mouth is, so the tip, its ember and the smoke show at the bottom of the view (plantation.json
    /// <c>view.cigarette.first_person</c>). Only while the view is the body's own eye on foot and no scope is up; gone at once otherwise (the smoke with it).
    /// </summary>
    void UpdateOwnCigarette(bool show)
    {
        if (!show || _me == null)
        {
            if (_ownCigarette != null && GodotObject.IsInstanceValid(_ownCigarette)) _ownCigarette.QueueFree();
            _ownCigarette = null; _ownSmoke = null; _ownTip = null;
            return;
        }
        if (_ownCigarette != null && GodotObject.IsInstanceValid(_ownCigarette)) { FollowOwnSmoke(); return; }
        var c = Plantation.View.Cigarette;
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
        _me.Camera.AddChild(_ownCigarette);
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
    public bool OwnHigh => HasStatus(_myId, StatusKind.High);

    /// <summary>Once per frame, before the roll is composed (<see cref="FadeOwnCorrection"/>): steps the nausea and applies its look change, roll and FOV to the first-person camera.</summary>
    void UpdateNausea(double delta)
    {
        if (_me == null || !GodotObject.IsInstanceValid(_me)) return;
        if (Dead || !OwnSpawned)
        {
            UpdateOwnCigarette(false);
            if (_nausea != null) { _nausea = null; _nauseaRoll = 0; _me.FovWobble = 0; } // dead: the high is gone with the life (the status bit too); nothing lingers into the respawn
            return;
        }
        var high = OwnHigh;
        UpdateOwnCigarette(high && Mode.OnFoot && _me.ZoomFov <= 0); // OnFoot is the same test the sway uses (CutCamera is always set: it is the one composed hook, not a sign of a cut-scene)
        if (!high && _nausea is not { Active: true }) { _nauseaRoll = 0; return; }
        _nausea ??= new Nausea(Plantation.Nausea);
        var owned = Mode.OnFoot;
        var (dYaw, dPitch) = _nausea.Step((float)delta, high, owned, GameSettings.Current.ReduceMotion);
        _me.NudgeLook(dYaw, dPitch);
        var (roll, fov) = _nausea.Visual;
        _nauseaRoll = owned ? roll : 0f;
        _me.FovWobble = owned ? fov : 0f;
    }
}
