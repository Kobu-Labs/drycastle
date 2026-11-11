using Godot;

namespace Suchohrad.Shooter;

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
    float _aim;

    /// <summary>Holding aim-down-sights: the gun eases to the screen centre so its iron sight sits on the crosshair point.</summary>
    public bool Aiming { get; set; }
    /// <summary>0 = hip, 1 = fully aimed (eased, so the HUD can fade the crosshair with it).</summary>
    public float AimAmount => _aim;

    public override void _Ready() { Position = HipPosition; }

    public override void _Process(double delta)
    {
        var target = Aiming ? 1f : 0f;
        if (Mathf.IsEqualApprox(_aim, target) && _aim == target) return;
        _aim = Mathf.MoveToward(Mathf.Lerp(_aim, target, 1f - Mathf.Exp(-AdsRate * (float)delta)), target, 0.002f);
        var sight = Current is { } n && n.HasMeta(WeaponFx.SightHeightMeta) ? (float)n.GetMeta(WeaponFx.SightHeightMeta) : 0.1f;
        Position = HipPosition.Lerp(new Vector3(0, -sight, AdsDepth), _aim);
    }
}
