using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Vitals;

/// <summary>Hit marker: four diagonal ticks around the centre. A kill and a headshot get a larger, stronger mark (shape, not colour only).</summary>
internal sealed class HitMarker
{
    readonly Control _node;
    readonly Line2D[] _lines = new Line2D[4];
    double _until, _fade;

    public HitMarker(Control root)
    {
        _node = HudUi.Place(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) }, 0.5f, 0.5f, 0, 0, 0, 0);
        root.AddChild(_node);
        for (var i = 0; i < 4; i++)
        {
            var dir = new Vector2(i % 2 == 0 ? 1 : -1, i < 2 ? 1 : -1).Normalized();
            var l = new Line2D { Width = 3, DefaultColor = ShooterTheme.Text, Points = new[] { dir * 12, dir * 24 } };
            _node.AddChild(l); _lines[i] = l;
        }
    }

    public void Show(bool headshot, bool kill)
    {
        var reduce = GameSettings.Current.ReduceMotion;
        var big = kill ? 1.6f : headshot ? 1.25f : 1f;
        _node.Scale = new Vector2(big, big);
        foreach (var l in _lines) { l.Width = kill || headshot ? 4 : 3; l.DefaultColor = kill ? ShooterTheme.Danger : headshot ? ShooterTheme.Warn : ShooterTheme.Text; }
        _fade = reduce ? 0 : 0.25; _until = HudUi.Now + (reduce ? 0.2 : 0.15) + _fade;
        HudUi.Fade(_node, 1);
    }

    public void Tick(double now)
    {
        if (_until <= 0) return;
        if (now >= _until) { _until = 0; HudUi.Fade(_node, 0); return; }
        HudUi.Fade(_node, _fade > 0 ? (float)Math.Clamp((_until - now) / _fade, 0, 1) : 1f);
    }

    public void Clear() { _until = 0; HudUi.Fade(_node, 0); }
}
