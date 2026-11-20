using Godot;

namespace Drycastle.Shooter;

/// <summary>A pooled tracer streak (<see cref="ShotFx"/>): flies from its start to its end in its own process, then hides and stops processing. No tween or callback per shot.</summary>
public partial class TracerPivot : Node3D
{
    Vector3 _from, _to;
    float _age, _dur;

    public override void _Ready() => SetProcess(false);

    public void Fly(Vector3 from, Vector3 to, float seconds)
    {
        _from = from; _to = to; _dur = seconds; _age = 0;
        Visible = true;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= _dur) { Position = _to; Visible = false; SetProcess(false); return; }
        Position = _from.Lerp(_to, _age / _dur);
    }
}
