using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The ring on the HUD that fills while the interact key is held at a crate, the loudspeaker, a loot bag or a mineral, and drains when it is released.</summary>
internal sealed class HoldRing
{
    /// <summary>Seconds for the ring to drain back to empty after the key is released (instant with reduce motion).</summary>
    const float DrainSeconds = 0.25f;

    readonly ClientContext _ctx;
    readonly Func<float>[] _holds;
    /// <summary>The ring's shown fill (0 to 1): follows the hold up and drains down after a release.</summary>
    float _shown;

    /// <param name="holds">How far each hold is (0 to 1); the ring shows the fullest.</param>
    public HoldRing(ClientContext ctx, params Func<float>[] holds) { _ctx = ctx; _holds = holds; }

    /// <summary>The hold's target is gone: the ring is empty at once.</summary>
    public void Reset()
    {
        _shown = 0;
        _ctx.Hud?.SetRadial(null);
    }

    public void Show(double dt)
    {
        var target = 0f;
        foreach (var hold in _holds) target = Math.Max(target, hold());
        if (target >= _shown || GameSettings.Current.ReduceMotion) _shown = target;
        else _shown = Mathf.MoveToward(_shown, target, (float)dt / DrainSeconds);
        _ctx.Hud?.SetRadial(_shown > 0 ? _shown : null);
    }
}
