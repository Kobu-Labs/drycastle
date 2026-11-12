using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Killstreak row in the HUD: "kills this life, skull | streak icon > streak icon > streak icon". Each icon carries the kills
/// it needs in its bottom right corner; an achieved streak turns green and grows to 110 % (shape and colour). No panel, no
/// bars, no words. Cells have fixed sizes, so values never move the layout. <see cref="RefreshStreaks"/> runs on the
/// tracker's events, not per frame, and writes only what changed.
/// </summary>
public partial class Hud
{
    const float StreakCell = 56, AchievedScale = 1.1f;

    readonly Control[] _streakCell = new Control[KillstreakService.Slots], _streakChevron = new Control[KillstreakService.Slots];
    readonly TextureRect[] _streakIcon = new TextureRect[KillstreakService.Slots];
    readonly Label[] _streakNeed = new Label[KillstreakService.Slots];
    readonly string[] _streakShownId = new string[KillstreakService.Slots];
    readonly int[] _streakShownState = new int[KillstreakService.Slots];
    Label _streakKills = null!;

    /// <summary>The streak row; <see cref="ApplyMinimapSettings"/> moves it under the minimap.</summary>
    Control _streakPanel = null!;

    void BuildStreaks()
    {
        var row = _streakPanel = Place(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0, 0, ShooterTheme.Margin, ShooterTheme.Margin + 32 + 224 + ShooterTheme.Space, 320, StreakCell + 8);
        ((HBoxContainer)row).AddThemeConstantOverride("separation", ShooterTheme.Space);
        ((HBoxContainer)row).Alignment = BoxContainer.AlignmentMode.Begin;
        _root.AddChild(row);
        _streakKills = L("0", "HudDisplay", HorizontalAlignment.Right, 40);
        _streakKills.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_streakKills);
        row.AddChild(new StreakGlyph(StreakGlyph.Shape.Skull) { CustomMinimumSize = new Vector2(32, StreakCell), MouseFilter = Control.MouseFilterEnum.Ignore });
        row.AddChild(new ColorRect { Color = ShooterTheme.Line, CustomMinimumSize = new Vector2(2, StreakCell - 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
        for (var i = 0; i < KillstreakService.Slots; i++)
        {
            if (i > 0)
            {
                _streakChevron[i] = new StreakGlyph(StreakGlyph.Shape.Chevron) { CustomMinimumSize = new Vector2(16, StreakCell), MouseFilter = Control.MouseFilterEnum.Ignore };
                row.AddChild(_streakChevron[i]);
            }
            var cell = _streakCell[i] = new Control { CustomMinimumSize = new Vector2(StreakCell, StreakCell), PivotOffset = new Vector2(StreakCell, StreakCell) / 2, MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddChild(cell);
            var icon = _streakIcon[i] = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
            icon.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            cell.AddChild(icon);
            var need = _streakNeed[i] = L("", "HudSmall", HorizontalAlignment.Right);
            need.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            need.VerticalAlignment = VerticalAlignment.Bottom;
            cell.AddChild(need);
            _streakShownState[i] = -1;
        }
        RefreshStreaks(null, false);
    }

    /// <summary>
    /// Shows the kills this life and the tracker's three slots. A null tracker (no killstreak data) shows 0 and no icons.
    /// <paramref name="alive"/> is unused by the row (the key hint is gone) and kept for the callers.
    /// </summary>
    public void RefreshStreaks(KillstreakTracker? tracker, bool alive)
    {
        Set(_streakKills, (tracker?.Kills ?? 0).ToString());
        for (var i = 0; i < KillstreakService.Slots; i++)
        {
            var view = tracker?.Slot(i) ?? new StreakSlotView("", "", StreakSlotState.Empty, 0, 0);
            var empty = view.State == StreakSlotState.Empty;
            // reserved space: an empty slot fades out rather than collapsing, so nothing shifts
            Fade(_streakCell[i], empty ? 0 : 1);
            if (_streakChevron[i] != null) Fade(_streakChevron[i], empty ? 0 : 1);
            if (empty) { _streakShownState[i] = (int)StreakSlotState.Empty; continue; }
            if (_streakShownId[i] != view.Id)
            {
                _streakShownId[i] = view.Id;
                _streakIcon[i].Texture = ResourceLoader.Load<Texture2D>(StreakIconPath(view.Id));
            }
            Set(_streakNeed[i], view.KillsRequired.ToString());
            if (_streakShownState[i] == (int)view.State) continue;
            _streakShownState[i] = (int)view.State;
            var achieved = view.State is StreakSlotState.Ready or StreakSlotState.Active;
            var spent = view.State == StreakSlotState.Spent;
            _streakCell[i].Scale = achieved ? new Vector2(AchievedScale, AchievedScale) : Vector2.One;
            var tint = achieved ? ShooterTheme.Good : Colors.White;
            _streakIcon[i].Modulate = new Color(tint.R, tint.G, tint.B, spent ? 0.4f : 1f);
            _streakNeed[i].Modulate = achieved ? ShooterTheme.Good : Colors.White;
        }
    }

    static string StreakIconPath(string id)
    {
        var path = $"{IconRegistry.Root}streaks/{id}.png";
        return ResourceLoader.Exists(path) ? path : IconRegistry.Placeholder;
    }

    /// <summary>The row's two small drawn glyphs: a skull (kills) and a chevron between streak icons. Drawn in the HUD text colour.</summary>
    sealed partial class StreakGlyph : Control
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
}
