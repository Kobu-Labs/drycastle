using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Vitals;

/// <summary>Damage indicators: chevrons on a ring, rotated around the centre to the direction of the source; each fades after a short life.</summary>
internal sealed class DamageIndicators
{
    const int Count = 4;
    const double Life = 1.2;

    readonly Control[] _pivot = new Control[Count];
    readonly Polygon2D[] _tri = new Polygon2D[Count];
    readonly double[] _at = new double[Count];
    readonly object?[] _key = new object?[Count];
    readonly Func<float?>?[] _live = new Func<float?>?[Count];
    int _next;

    public DamageIndicators(Control root)
    {
        for (var i = 0; i < Count; i++)
        {
            var pivot = HudUi.Place(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) }, 0.5f, 0.5f, 0, 0, 0, 0);
            var back = new Polygon2D { Color = ShooterTheme.Outline, Polygon = new[] { new Vector2(0, -216), new Vector2(-28, -176), new Vector2(28, -176) } };
            var tri = new Polygon2D { Color = ShooterTheme.Danger, Polygon = new[] { new Vector2(0, -208), new Vector2(-20, -180), new Vector2(20, -180) } };
            pivot.AddChild(back); pivot.AddChild(tri);
            root.AddChild(pivot); _pivot[i] = pivot; _tri[i] = tri;
        }
    }

    /// <summary>Direction of a damage source relative to the view (radians, 0 = ahead, clockwise). Fixed: it does not follow anything.</summary>
    public void Add(float angle) => Show(null, angle, null);

    /// <summary>A tracked source: <paramref name="live"/> gives its current angle every frame (null = lost, the chevron keeps its last angle); a new hit from the same key refreshes its chevron (#424).</summary>
    public void Track(object key, Func<float?> live)
    {
        if (live() is { } a) Show(key, a, live);
    }

    void Show(object? key, float angle, Func<float?>? live)
    {
        var i = -1;
        if (key != null) for (var k = 0; k < Count; k++) if (_at[k] > 0 && key.Equals(_key[k])) { i = k; break; }
        if (i < 0) i = _next++ % Count;
        _key[i] = key; _live[i] = live;
        _pivot[i].Rotation = angle;
        _at[i] = HudUi.Now;
        HudUi.Fade(_pivot[i], 1);
    }

    /// <summary>The palette may have switched: the chevrons read their colour at build time otherwise.</summary>
    public void Recolor()
    {
        foreach (var tri in _tri) tri.Color = ShooterTheme.Danger;
    }

    public void Tick(double now)
    {
        for (var i = 0; i < Count; i++) TickOne(i, now);
    }

    void TickOne(int i, double now)
    {
        if (_at[i] <= 0) return;
        var age = now - _at[i];
        if (age >= Life) { Drop(i); return; }
        if (_live[i]?.Invoke() is { } a) _pivot[i].Rotation = a;
        HudUi.Fade(_pivot[i], GameSettings.Current.ReduceMotion ? 1f : (float)Math.Clamp((Life - age) / 0.6, 0, 1));
    }

    public void Clear()
    {
        for (var i = 0; i < Count; i++) Drop(i);
    }

    void Drop(int i) { _at[i] = 0; _key[i] = null; _live[i] = null; HudUi.Fade(_pivot[i], 0); }
}
