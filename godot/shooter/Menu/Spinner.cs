using Godot;

namespace Drycastle.Shooter;

/// <summary>Indeterminate activity indicator: a faint ring with a bright arc turning on it. Reduce-motion draws the arc still.</summary>
public partial class Spinner : Control
{
    const float ArcSweep = Mathf.Pi * 1.5f, TurnsPerSecond = 0.9f;
    float _angle;

    public Spinner(int size = ShooterTheme.Space * 5)
    {
        CustomMinimumSize = new Vector2(size, size);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public override void _Process(double delta)
    {
        if (GameSettings.Current.ReduceMotion || !IsVisibleInTree()) return;
        _angle = Mathf.Wrap(_angle + (float)delta * TurnsPerSecond * Mathf.Tau, 0f, Mathf.Tau);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var c = Size / 2f;
        var width = ShooterTheme.BorderStrong + 1;
        var r = Mathf.Min(Size.X, Size.Y) / 2f - width;
        DrawArc(c, r, 0, Mathf.Tau, 48, ShooterTheme.Line with { A = 0.45f }, width, true);
        DrawArc(c, r, _angle, _angle + ArcSweep, 36, ShooterTheme.Accent, width, true);
    }
}
