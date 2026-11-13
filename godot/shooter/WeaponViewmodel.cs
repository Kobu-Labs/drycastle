using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>First-person stand-in for arms: the weapon placed low on the right of the view, no hands.</summary>
public sealed partial class WeaponViewmodel : Node3D
{
    readonly Dictionary<string, Node3D> _models = new();
    string _current = "";
    public Func<string, Node3D?> Factory { get; set; } = WeaponFx.Make;

    public void Show(string weaponId)
    {
        if (weaponId == _current) return;
        if (_models.TryGetValue(_current, out var old)) old.Visible = false;
        _current = weaponId;
        if (weaponId == "") return;
        if (!_models.TryGetValue(weaponId, out var n))
        {
            n = Factory(weaponId);
            if (n == null) return;
            _models[weaponId] = n;
            AddChild(n);
        }
        n.Visible = true;
    }

    public Node3D? Current => _models.GetValueOrDefault(_current);

    /// <summary>World position of the held weapon's barrel tip, or null when nothing is shown.</summary>
    public Vector3? MuzzlePosition => Current is { Visible: true } n && n.GetNodeOrNull<Node3D>("Muzzle") is { } m ? m.GlobalPosition : null;

    /// <summary>Flash the current weapon's muzzle (no-op for melee / nothing shown).</summary>
    public void Flash(float scale) { if (Current is { Visible: true } n) MuzzleFlash.Of(n)?.Flash(scale); }

    static readonly Vector3 HipPosition = new(0.24f, -0.22f, -0.5f);
    const float AdsDepth = -0.42f, AdsRate = 14f;
    /// <summary>Kick per degree of climb, recovery per second, and the largest tilt in radians.</summary>
    const float KickPerDegree = 0.04f, KickRecover = 9f, KickTilt = 0.12f;
    float _aim, _kick;

    /// <summary>Holding aim-down-sights: the gun eases to the screen centre so its iron sight sits on the crosshair point.</summary>
    public bool Aiming { get; set; }
    /// <summary>0 = hip, 1 = fully aimed (eased, so the HUD can fade the crosshair with it).</summary>
    public float AimAmount => _aim;

    /// <summary>
    /// What the body is doing this frame, asked once per frame (#259). Left unset (or inactive) the gun keeps still, as it did before the motion.
    /// The aim is filled in here; the caller fills the rest.
    /// </summary>
    public Func<ViewmodelInput>? Context { get; set; }

    /// <summary>Told each frame how far the eye is to be lowered (the camera part of the landing dip), metres.</summary>
    public Action<float>? CameraDrop { get; set; }

    /// <summary>Walk bob, look lag, landing dip, sprint pose, strafe lean and idle breath (pure, see <see cref="ViewmodelMotion"/>).</summary>
    public ViewmodelMotion Motion { get; } = new();

    public override void _Ready() { Position = HipPosition; }

    /// <summary>Visual kick for one shot: <paramref name="degrees"/> of climb (already scaled by ADS and crouch) pushes the gun up and back, then it settles.</summary>
    public void Kick(float degrees) => _kick = Mathf.Min(_kick + degrees * KickPerDegree, 1f);

    public override void _Process(double delta)
    {
        var target = Aiming ? 1f : 0f;
        if (_aim != target) _aim = Mathf.MoveToward(Mathf.Lerp(_aim, target, 1f - Mathf.Exp(-AdsRate * (float)delta)), target, 0.002f);
        _kick = Mathf.MoveToward(_kick, 0, KickRecover * (float)delta);
        var sight = Current is { } n && n.HasMeta(WeaponFx.SightHeightMeta) ? (float)n.GetMeta(WeaponFx.SightHeightMeta) : 0.1f;
        var input = Context?.Invoke() ?? default;
        input.Aim = _aim;
        Motion.Step((float)delta, input);
        CameraDrop?.Invoke(Motion.CameraDrop);
        var m = Motion.Position; var r = Motion.Rotation;
        Position = HipPosition.Lerp(new Vector3(0, -sight, AdsDepth), _aim) + new Vector3(0, 0.01f, 0.04f) * _kick + new Vector3(m.X, m.Y, m.Z);
        Rotation = new Vector3(KickTilt * _kick + r.X, r.Y, r.Z);
    }
}
