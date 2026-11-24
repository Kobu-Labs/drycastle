using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Icons;
using Drycastle.ShooterCore.Killstreaks;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// Killstreak row in the HUD: "kills this life, skull | streak icon > streak icon > streak icon". Each icon carries the kills
/// it needs in its bottom right corner; an achieved streak turns green and grows to 110 % (shape and colour). No panel, no
/// bars, no words. Cells have fixed sizes, so values never move the layout. <see cref="Refresh"/> runs on the
/// tracker's events, not per frame, and writes only what changed. It sits under the minimap.
/// </summary>
internal sealed class StreakRow
{
    const float Cell = 56, AchievedScale = 1.1f;
    const int Slots = KillstreakService.Slots;

    readonly HBoxContainer _row;
    readonly Control[] _cell = new Control[Slots], _chevron = new Control[Slots];
    readonly TextureRect[] _icon = new TextureRect[Slots];
    readonly Label[] _need = new Label[Slots];
    readonly string[] _shownId = new string[Slots];
    readonly int[] _shownState = new int[Slots];
    readonly Label _kills;

    public StreakRow(Control root)
    {
        _row = HudUi.Place(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32 + 224 + ShooterTheme.Space, 320, Cell + 8);
        _row.AddThemeConstantOverride("separation", ShooterTheme.Space);
        _row.Alignment = BoxContainer.AlignmentMode.Begin;
        root.AddChild(_row);
        _kills = HudUi.L("0", "HudDisplay", HorizontalAlignment.Right, 40);
        _kills.VerticalAlignment = VerticalAlignment.Center;
        _row.AddChild(_kills);
        _row.AddChild(new StreakGlyph(StreakGlyph.Shape.Skull) { CustomMinimumSize = new Vector2(32, Cell), MouseFilter = Control.MouseFilterEnum.Ignore });
        _row.AddChild(new ColorRect { Color = ShooterTheme.Line, CustomMinimumSize = new Vector2(2, Cell - 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
        for (var i = 0; i < Slots; i++)
        {
            if (i > 0)
            {
                _chevron[i] = new StreakGlyph(StreakGlyph.Shape.Chevron) { CustomMinimumSize = new Vector2(16, Cell), MouseFilter = Control.MouseFilterEnum.Ignore };
                _row.AddChild(_chevron[i]);
            }
            var cell = _cell[i] = new Control { CustomMinimumSize = new Vector2(Cell, Cell), PivotOffset = new Vector2(Cell, Cell) / 2, MouseFilter = Control.MouseFilterEnum.Ignore };
            _row.AddChild(cell);
            var icon = _icon[i] = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
            icon.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            cell.AddChild(icon);
            var need = _need[i] = HudUi.L("", "HudSmall", HorizontalAlignment.Right);
            need.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            need.VerticalAlignment = VerticalAlignment.Bottom;
            cell.AddChild(need);
            _shownState[i] = -1;
        }
        Refresh(null);
    }

    /// <summary>As wide as the minimap: its natural width is scaled down to fit (top left stays put), under the minimap.</summary>
    public void PlaceUnder(float minimapSize)
    {
        var natural = _row.GetCombinedMinimumSize();
        HudUi.Place(_row, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32 + minimapSize + ShooterTheme.Space, natural.X, natural.Y);
        var k = Math.Min(1f, minimapSize / Math.Max(1f, natural.X));
        _row.Scale = new Vector2(k, k);
    }

    /// <summary>Shows the kills this life and the tracker's three slots. A null tracker (no killstreak data) shows 0 and no icons.</summary>
    public void Refresh(KillstreakTracker? tracker)
    {
        HudUi.Set(_kills, (tracker?.Kills ?? 0).ToString());
        for (var i = 0; i < Slots; i++)
        {
            var view = tracker?.Slot(i) ?? new StreakSlotView("", "", StreakSlotState.Empty, 0, 0);
            var empty = view.State == StreakSlotState.Empty;
            // reserved space: an empty slot fades out rather than collapsing, so nothing shifts
            HudUi.Fade(_cell[i], empty ? 0 : 1);
            if (_chevron[i] != null) HudUi.Fade(_chevron[i], empty ? 0 : 1);
            if (empty) { _shownState[i] = (int)StreakSlotState.Empty; continue; }
            if (_shownId[i] != view.Id)
            {
                _shownId[i] = view.Id;
                _icon[i].Texture = ResourceLoader.Load<Texture2D>(IconPath(view.Id));
            }
            HudUi.Set(_need[i], view.KillsRequired.ToString());
            if (_shownState[i] == (int)view.State) continue;
            _shownState[i] = (int)view.State;
            var achieved = view.State is StreakSlotState.Ready or StreakSlotState.Active;
            var spent = view.State == StreakSlotState.Spent;
            _cell[i].Scale = achieved ? new Vector2(AchievedScale, AchievedScale) : Vector2.One;
            var tint = achieved ? ShooterTheme.Good : Colors.White;
            _icon[i].Modulate = new Color(tint.R, tint.G, tint.B, spent ? 0.4f : 1f);
            _need[i].Modulate = achieved ? ShooterTheme.Good : Colors.White;
        }
    }

    static string IconPath(string id)
    {
        var path = $"{IconRegistry.Root}streaks/{id}.png";
        return ResourceLoader.Exists(path) ? path : IconRegistry.Placeholder;
    }
}

/// <summary>The row's two small drawn glyphs: a skull (kills) and a chevron between streak icons. Drawn in the HUD text colour.</summary>
internal sealed partial class StreakGlyph : Control
{
    public enum Shape { Skull, Chevron }
    readonly Shape _shape;
    public StreakGlyph(Shape shape) => _shape = shape;

    public override void _Draw()
    {
        var w = Size.X; var h = Size.Y;
        var c = new Vector2(w / 2, h / 2);
        if (_shape == Shape.Chevron)
        {
            DrawPolyline(new[] { c + new Vector2(-3, -9), c + new Vector2(3, 0), c + new Vector2(-3, 9) }, ShooterTheme.Dim, 3f, true);
            return;
        }
        var r = Math.Min(w, h) * 0.34f;
        DrawCircle(c + new Vector2(0, -r * 0.25f), r, ShooterTheme.Text);
        DrawRect(new Rect2(c + new Vector2(-r * 0.55f, r * 0.45f), new Vector2(r * 1.1f, r * 0.8f)), ShooterTheme.Text);
        var eye = new Color(0, 0, 0, 0.85f);
        DrawCircle(c + new Vector2(-r * 0.4f, -r * 0.2f), r * 0.24f, eye);
        DrawCircle(c + new Vector2(r * 0.4f, -r * 0.2f), r * 0.24f, eye);
        DrawRect(new Rect2(c + new Vector2(-r * 0.12f, r * 0.9f), new Vector2(r * 0.24f, r * 0.35f)), eye);
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
}
