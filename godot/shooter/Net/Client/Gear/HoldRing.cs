using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.Net.Client.Gear;

/// <summary>The ring on the HUD that fills while the interact key is held at a crate, the loudspeaker, a loot bag, a mineral, a drill, the Harvest's plantation or the Delivery's drop, and drains when it is released. Each hold names its own job under the ring.</summary>
internal sealed class HoldRing
{
    /// <summary>Seconds for the ring to drain back to empty after the key is released (instant with reduce motion).</summary>
    const float DrainSeconds = 0.25f;

    readonly ClientContext _ctx;
    readonly (Func<float> Fraction, Func<string> LabelKey)[] _holds;
    /// <summary>The label of the fullest hold, kept while the ring drains after the release.</summary>
    string _label = "hud.crate.opening";
    /// <summary>The ring's shown fill (0 to 1): follows the hold up and drains down after a release.</summary>
    float _shown;

    /// <param name="holds">How far each hold is (0 to 1) and the string key of its label (asked while the hold runs); the ring shows the fullest, with its label.</param>
    public HoldRing(ClientContext ctx, params (Func<float> Fraction, Func<string> LabelKey)[] holds) { _ctx = ctx; _holds = holds; }

    /// <summary>The hold's target is gone: the ring is empty at once.</summary>
    public void Reset()
    {
        _shown = 0;
        _ctx.Hud?.SetRadial(null);
    }

    public void Show(double dt)
    {
        var target = 0f;
        foreach (var (fraction, label) in _holds)
        {
            var f = fraction();
            if (f <= target) continue;
            target = f; _label = label();
        }
        if (target >= _shown || GameSettings.Current.ReduceMotion) _shown = target;
        else _shown = Mathf.MoveToward(_shown, target, (float)dt / DrainSeconds);
        _ctx.Hud?.SetRadial(_shown > 0 ? _shown : null, _label);
    }
}
