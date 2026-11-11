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

    public override void _Ready() { Position = new Vector3(0.24f, -0.22f, -0.5f); }
}
