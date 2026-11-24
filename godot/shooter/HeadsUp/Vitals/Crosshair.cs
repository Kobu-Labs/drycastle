using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.HeadsUp.Vitals;

/// <summary>
/// The crosshair: four arms whose gap follows the spread. The one writer of its Visible: the setting, the cover, the scope, the binoculars, the pounce and the
/// seat all go through <see cref="CrosshairRules"/>.
/// </summary>
internal sealed class Crosshair
{
    const float Thick = 3, Len = 12, MinGap = 8;

    readonly Control _node;
    readonly ColorRect[] _arms = new ColorRect[4], _armsBack = new ColorRect[4];
    readonly Func<bool> _scopeUp, _binocularsUp;
    float _gap = -1, _alpha = 1;
    bool _covered, _launcher, _unarmed, _pounce, _flung;

    public Crosshair(Control root, Func<bool> scopeUp, Func<bool> binocularsUp)
    {
        _scopeUp = scopeUp; _binocularsUp = binocularsUp;
        _node = HudUi.Place(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = GameSettings.Current.ShowCrosshair }, 0.5f, 0.5f, 0, 0, 0, 0);
        root.AddChild(_node);
        for (var i = 0; i < 4; i++)
        {
            _armsBack[i] = new ColorRect { Color = ShooterTheme.Outline, MouseFilter = Control.MouseFilterEnum.Ignore };
            _arms[i] = new ColorRect { Color = ShooterTheme.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
            _node.AddChild(_armsBack[i]); _node.AddChild(_arms[i]);
        }
    }

    /// <summary>Lying under the demon, or in the Bigfoot's fist or flying from it: no crosshair, and the sights are not read.</summary>
    public bool Pinned => _pounce || _flung;

    /// <summary>A panel stands in the middle of the screen (the corpse chooser): the crosshair would show through it.</summary>
    public void Cover(bool on) { if (_covered == on) return; _covered = on; Apply(); }

    /// <summary>The Lock-on launcher's sight is up (<see cref="LockOnOverlay"/>): it has its own aim pip.</summary>
    public void SetLauncher(bool hide) { if (_launcher == hide) return; _launcher = hide; Apply(); }

    /// <summary>The local player sits where nothing can be fired (#373).</summary>
    public void SetUnarmed(bool on) { if (_unarmed == on) return; _unarmed = on; Apply(); }

    public void SetPounce(bool hide) { if (_pounce == hide) return; _pounce = hide; Apply(); }

    public void SetFlung(bool hide) { if (_flung == hide) return; _flung = hide; Apply(); }

    public void Apply() => _node.Visible = CrosshairRules.Visible(GameSettings.Current.ShowCrosshair, _covered || _launcher, _scopeUp(), _binocularsUp(), Pinned, _unarmed);

    /// <summary>The next frame lays the arms out again (the screen or the settings changed).</summary>
    public void ResetGap() => _gap = -1;

    /// <summary>Fades with the iron-sight aim amount 0..1 and opens with the spread, degrees; the arms move only when the gap changed.</summary>
    public void Update(float aim, float spreadDeg)
    {
        var alpha = 1f - Math.Clamp(aim, 0f, 1f);
        if (Math.Abs(alpha - _alpha) > 0.004f) { _alpha = alpha; _node.Modulate = new Color(1, 1, 1, alpha); }
        var s = HudUi.Scale(_node);
        var gap = HudLogic.CrosshairGap(spreadDeg, GameSettings.Current.Fov, _node.GetViewport().GetVisibleRect().Size.Y, MinGap * s) / s;
        if (Math.Abs(gap - _gap) < 0.25f) return;
        _gap = gap;
        const float o = 1; // outline width
        var arms = new (float X, float Y, float W, float H)[]
        {
            (-Thick / 2, -gap - Len, Thick, Len), (-Thick / 2, gap, Thick, Len),
            (-gap - Len, -Thick / 2, Len, Thick), (gap, -Thick / 2, Len, Thick),
        };
        for (var i = 0; i < 4; i++)
        {
            var a = arms[i];
            _arms[i].Position = new Vector2(a.X, a.Y); _arms[i].Size = new Vector2(a.W, a.H);
            _armsBack[i].Position = new Vector2(a.X - o, a.Y - o); _armsBack[i].Size = new Vector2(a.W + 2 * o, a.H + 2 * o);
        }
    }
}
