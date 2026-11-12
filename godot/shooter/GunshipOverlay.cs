using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The gunship's point-of-view overlay (ticket #125): the thermal grade over the picture, the reticle, the weapon and zoom, the
/// ammo readout (the streak's time is the only limit, so it reads UNLIMITED), the control hints from the bound keys, and the
/// enemy markers (ticket #157): a red diamond on every visible enemy player and target dummy, the one nearest the reticle
/// brighter (a larger diamond; no pulse with reduce motion). Hidden except while the owner is at a gunship turret. Tokens and sizes come
/// from <see cref="ShooterTheme"/>, scaled by the screen height against 1080 p.
/// </summary>
public partial class GunshipOverlay : CanvasLayer
{
    public const string ShaderPath = "res://shaders/gunship_thermal.gdshader";

    readonly ColorRect _thermal = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    ShaderMaterial _grade = null!;
    readonly GunshipPanel _panel = new();

    public override void _Ready()
    {
        Layer = 5; // under the HUD (layer 10), over the world
        Visible = false;
        _thermal.Material = _grade = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        _thermal.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_thermal);
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_panel);
    }

    /// <summary>
    /// Shows the overlay for a turret with this status. Call again when the status or the weapon changes, not every frame.
    /// The AC-130 (<paramref name="ac130"/>) draws its black-and-white view and its weapon selector (keys 1-3, the selected one
    /// in brackets); its time left is set with <see cref="SetClock"/>.
    /// </summary>
    /// <param name="cycleWeapons">False when the turret has one weapon for this use, so the next-weapon hint is left out.</param>
    public void Present(GunshipStatus s, GunshipDef def, bool cycleWeapons = true, bool ac130 = false)
    {
        var w = def.Weapons[Math.Min(s.Weapon, def.Weapons.Count - 1)];
        var zoom = def.ZoomLevels[Math.Min(s.Zoom, def.ZoomLevels.Count - 1)];
        _grade.SetShaderParameter("mono", ac130 ? 1f : 0f);
        _panel.Set(
            title: (ac130 ? Strings.T("ac130.title") : Strings.F("gunship.title")) + "   " + w.Name,
            zoom: Strings.F("gunship.zoom", zoom.ToString("0.#")),
            ammo: Strings.T("gunship.ammo"),
            hint: ac130
                ? Strings.F("ac130.hint", InputBindings.Name("fire"), InputBindings.Name("zoom"))
                : cycleWeapons
                    ? Strings.F("gunship.hint", InputBindings.Name("fire"), InputBindings.Name("zoom"), InputBindings.Name("reload"))
                    : Strings.F("gunship.hint_single", InputBindings.Name("fire"), InputBindings.Name("zoom")),
            selector: ac130 ? WeaponSelector(def, s.Weapon) : null);
        Visible = true;
    }

    /// <summary>"1 25 mm   2 [40 mm]   3 105 mm": the keys and the names, the selected weapon in brackets (not colour alone).</summary>
    static string WeaponSelector(GunshipDef def, int selected)
    {
        var parts = new string[def.Weapons.Count];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = i == selected ? $"{i + 1} [{def.Weapons[i].Name}]" : $"{i + 1} {def.Weapons[i].Name}";
        return string.Join("    ", parts);
    }

    /// <summary>The timed streak's clock and kill count, top right (empty strings hide them). Cheap to call every frame: it redraws only on change.</summary>
    public void SetClock(string timer, string kills) => _panel.SetClock(timer, kills);

    /// <summary>
    /// The enemy markers for this frame: the camera that projects them, and the world points (the owner's list, reused; the overlay
    /// reads it on draw and keeps no copy). Empty or null hides them. Call every frame while the turret is in use.
    /// </summary>
    public void SetMarkers(Camera3D? camera, List<Vector3>? points) => _panel.SetMarkers(camera, points);

    public void Dismiss()
    {
        Visible = false;
        _panel.SetMarkers(null, null); // stop tracking the enemies while hidden
    }

    /// <summary>A gunner's hit on an enemy: the reticle's marker, the same as the HUD's (a kill is bigger and red, a headshot amber).</summary>
    public void Hit(bool headshot, bool kill) => _panel.Hit(headshot, kill);

    /// <summary>The overlay's drawing: reticle, markers and text, all in one Control so the frame costs one draw.</summary>
    sealed partial class GunshipPanel : Control
    {
        string _title = "", _zoom = "", _ammo = "", _hint = "", _timer = "", _kills = "";
        string? _selector;
        ulong _hitAtMs;
        bool _hitKill, _hitHead;
        static readonly List<Vector3> EmptyPoints = new();

        /// <summary>How long the hit marker stays, in ms: the HUD's marker is 0.15 s plus a 0.25 s fade.</summary>
        const ulong HitMarkerMs = 400;

        Camera3D? _camera;
        List<Vector3>? _points;

        /// <summary>Starts the hit marker; redraws each frame until it has faded.</summary>
        public void Hit(bool headshot, bool kill)
        {
            _hitAtMs = Time.GetTicksMsec(); _hitHead = headshot; _hitKill = kill;
            SetProcess(true);
            QueueRedraw();
        }

        public void SetMarkers(Camera3D? camera, List<Vector3>? points)
        {
            _camera = camera; _points = points;
            SetProcess(true);
            QueueRedraw();
        }

        public override void _Process(double delta)
        {
            var now = Time.GetTicksMsec();
            if (now - _hitAtMs < HitMarkerMs || (_points is { Count: > 0 } && _camera != null)) QueueRedraw(); // the hit marker fades; the markers track the view
            else SetProcess(false);
        }

        void DrawHitMarker(Vector2 centre, float s)
        {
            if (_hitAtMs == 0 || Time.GetTicksMsec() - _hitAtMs >= HitMarkerMs) return;
            var big = _hitKill ? 1.6f : _hitHead ? 1.25f : 1f;
            var colour = _hitKill ? ShooterTheme.Danger : _hitHead ? ShooterTheme.Warn : ShooterTheme.Text;
            var inner = 22f * s * big; var outer = 34f * s * big; var wd = (_hitKill || _hitHead ? 4f : 3f) * s;
            foreach (var d in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
            {
                var dir = d.Normalized();
                DrawLine(centre + dir * inner, centre + dir * outer, colour, wd);
            }
        }

        public void Set(string title, string zoom, string ammo, string hint, string? selector)
        {
            _title = title; _zoom = zoom; _ammo = ammo; _hint = hint; _selector = selector;
            QueueRedraw();
        }

        public void SetClock(string timer, string kills)
        {
            if (timer == _timer && kills == _kills) return;
            _timer = timer; _kills = kills;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var font = ThemeDB.FallbackFont;
            var size = Size;
            var s = ShooterTheme.ScreenScale(size);
            var body = (int)Math.Round(ShooterTheme.FontBody * s);
            var big = (int)Math.Round(ShooterTheme.FontBig * s);
            var m = ShooterTheme.Margin * s;
            var centre = size / 2f;

            if (_camera != null) EnemyMarkers.Draw(this, _camera, _points ?? EmptyPoints, centre, size, s);

            // reticle: a ring, four ticks with a gap at the centre
            DrawArc(centre, 14f * s, 0, Mathf.Tau, 48, ShooterTheme.Text, ShooterTheme.BorderStrong * s);
            var gap = 5f * s; var len = 12f * s; var wd = ShooterTheme.Border * 2 * s;
            DrawLine(centre + new Vector2(gap, 0), centre + new Vector2(gap + len, 0), ShooterTheme.Text, wd);
            DrawLine(centre - new Vector2(gap, 0), centre - new Vector2(gap + len, 0), ShooterTheme.Text, wd);
            DrawLine(centre + new Vector2(0, gap), centre + new Vector2(0, gap + len), ShooterTheme.Text, wd);
            DrawLine(centre - new Vector2(0, gap), centre - new Vector2(0, gap + len), ShooterTheme.Text, wd);
            DrawHitMarker(centre, s);
            DrawString(font, new Vector2(centre.X - 60f * s, centre.Y + 40f * s), _zoom, HorizontalAlignment.Center, 120f * s, big, ShooterTheme.Text);

            // top left: the turret and the weapon
            DrawString(font, new Vector2(m, m + body), _title, HorizontalAlignment.Left, -1, body, ShooterTheme.Text);
            if (_selector != null) DrawString(font, new Vector2(m, m + body * 2 + 8f * s), _selector, HorizontalAlignment.Left, -1, body, ShooterTheme.Text);

            // bottom right: the ammo readout (unlimited: the streak's time is the limit)
            var barW = 240f * s;
            DrawString(font, new Vector2(size.X - m - barW, size.Y - m - body), _ammo, HorizontalAlignment.Right, barW, body, ShooterTheme.Text);

            // top right: the timed streak's clock and kills (absent for an untimed turret)
            if (_timer != "")
            {
                DrawString(font, new Vector2(size.X - m - 240f * s, m + body), _timer, HorizontalAlignment.Right, 240f * s, big, ShooterTheme.Text);
                DrawString(font, new Vector2(size.X - m - 240f * s, m + body + big + 8f * s), _kills, HorizontalAlignment.Right, 240f * s, body, ShooterTheme.Text);
            }

            // bottom left: the keys
            DrawString(font, new Vector2(m, size.Y - m), _hint, HorizontalAlignment.Left, -1, body, ShooterTheme.Dim);
        }
    }
}
