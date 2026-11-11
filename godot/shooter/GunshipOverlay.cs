using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The gunship's point-of-view overlay (ticket #125): the thermal grade over the picture, the reticle, the weapon and zoom,
/// ammo, heat (a bar with its percent, and the word OVERHEATED or OUT OF AMMO, so no state is colour alone), and the
/// control hints from the bound keys. Hidden except while the owner is at a gunship turret. Tokens and sizes come from
/// <see cref="ShooterTheme"/>, scaled by the screen height against 1080 p.
/// </summary>
public partial class GunshipOverlay : CanvasLayer
{
    const string ShaderPath = "res://shaders/gunship_thermal.gdshader";

    readonly ColorRect _thermal = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly GunshipPanel _panel = new();

    public override void _Ready()
    {
        Layer = 5; // under the HUD (layer 10), over the world
        Visible = false;
        _thermal.Material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        _thermal.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_thermal);
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_panel);
    }

    /// <summary>Shows the overlay for a turret with this status. Call again when the status or the weapon changes, not every frame.</summary>
    public void Present(GunshipStatus s, GunshipDef def)
    {
        var w = def.Weapons[Math.Min(s.Weapon, def.Weapons.Count - 1)];
        var zoom = def.ZoomLevels[Math.Min(s.Zoom, def.ZoomLevels.Count - 1)];
        _panel.Set(
            title: Strings.F("gunship.title") + "   " + w.Name,
            zoom: Strings.F("gunship.zoom", zoom.ToString("0.#")),
            ammo: Strings.F("gunship.ammo", s.Ammo, w.Ammo),
            heat: Strings.F("gunship.heat", s.HeatPercent),
            heatFraction: s.HeatPercent / 100f,
            state: s.Overheated ? Strings.T("gunship.overheat") : s.Empty ? Strings.T("gunship.empty") : "",
            hint: Strings.F("gunship.hint", InputBindings.Name("fire"), InputBindings.Name("zoom"), InputBindings.Name("reload")));
        Visible = true;
    }

    public void Dismiss() => Visible = false;

    /// <summary>The overlay's drawing: reticle and text, all in one Control so the frame costs one draw.</summary>
    sealed partial class GunshipPanel : Control
    {
        string _title = "", _zoom = "", _ammo = "", _heat = "", _state = "", _hint = "";
        float _heatFraction;

        public void Set(string title, string zoom, string ammo, string heat, float heatFraction, string state, string hint)
        {
            _title = title; _zoom = zoom; _ammo = ammo; _heat = heat; _heatFraction = Math.Clamp(heatFraction, 0f, 1f);
            _state = state; _hint = hint;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var font = ThemeDB.FallbackFont;
            var size = Size;
            var s = Math.Clamp(size.Y / 1080f, 0.5f, 3f);
            var body = (int)Math.Round(ShooterTheme.FontBody * s);
            var big = (int)Math.Round(ShooterTheme.FontBig * s);
            var m = ShooterTheme.Margin * s;
            var centre = size / 2f;

            // reticle: a ring, four ticks with a gap at the centre
            DrawArc(centre, 14f * s, 0, Mathf.Tau, 48, ShooterTheme.Text, ShooterTheme.BorderStrong * s);
            var gap = 5f * s; var len = 12f * s; var wd = ShooterTheme.Border * 2 * s;
            DrawLine(centre + new Vector2(gap, 0), centre + new Vector2(gap + len, 0), ShooterTheme.Text, wd);
            DrawLine(centre - new Vector2(gap, 0), centre - new Vector2(gap + len, 0), ShooterTheme.Text, wd);
            DrawLine(centre + new Vector2(0, gap), centre + new Vector2(0, gap + len), ShooterTheme.Text, wd);
            DrawLine(centre - new Vector2(0, gap), centre - new Vector2(0, gap + len), ShooterTheme.Text, wd);
            DrawString(font, new Vector2(centre.X - 60f * s, centre.Y + 40f * s), _zoom, HorizontalAlignment.Center, 120f * s, big, ShooterTheme.Text);

            // top left: the turret and the weapon
            DrawString(font, new Vector2(m, m + body), _title, HorizontalAlignment.Left, -1, body, ShooterTheme.Text);

            // bottom right: state, ammo, heat bar with its percent
            var barW = 240f * s; var barH = 14f * s;
            var barX = size.X - m - barW; var barY = size.Y - m - barH;
            DrawString(font, new Vector2(barX, barY - 8f * s), _heat, HorizontalAlignment.Left, barW, body, ShooterTheme.Text);
            DrawRect(new Rect2(barX, barY, barW, barH), ShooterTheme.Raised);
            DrawRect(new Rect2(barX, barY, barW * _heatFraction, barH), ShooterTheme.Accent);
            DrawRect(new Rect2(barX, barY, barW, barH), ShooterTheme.Line, false, ShooterTheme.Border * s);
            var ammoY = barY - 8f * s - body - 8f * s;
            DrawString(font, new Vector2(size.X - m - barW, ammoY), _ammo, HorizontalAlignment.Left, barW, body, ShooterTheme.Text);
            if (_state != "")
                DrawString(font, new Vector2(size.X - m - barW, ammoY - body - 8f * s), _state, HorizontalAlignment.Left, barW, body, ShooterTheme.Text);

            // bottom left: the keys
            DrawString(font, new Vector2(m, size.Y - m), _hint, HorizontalAlignment.Left, -1, body, ShooterTheme.Dim);
        }
    }
}
