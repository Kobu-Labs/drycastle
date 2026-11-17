using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>What an <see cref="AnimalPicture"/> draws the animal from: its hit shape (the cells, the fallback), its baked picture, or just a body box of this length over height.</summary>
public readonly record struct AnimalArt(AnimalShape? Shape, SilhouetteArt? Picture, float Aspect)
{
    public static readonly AnimalArt Box = new(null, null, 2f);
}

/// <summary>How an <see cref="AnimalPicture"/> is painted.</summary>
public enum PictureLook
{
    /// <summary>A recorded hit (the sniper's card and the Hunter's Log): the animal in the dim ink, the bullet's entry marked, a plain body as an outlined box.</summary>
    Hit,
    /// <summary>A species the log owner has hunted: the animal filled in the accent.</summary>
    Filled,
    /// <summary>A species not hunted yet: only its outline.</summary>
    Outline,
}

/// <summary>
/// The side view of an animal, always facing left (the nose on the left), fitted in the control with its proportions kept: the hit card after a sniper kill, a
/// recorded hit of the Hunter's Log, and a Life List tile (filled or outlined). In the <see cref="PictureLook.Hit"/> look the bullet's
/// entry is marked along the animal's length and height by a ring and a cross (shape, not colour alone). Redraws only when set or resized.
/// </summary>
public sealed partial class AnimalPicture : Control
{
    /// <summary>The sniper's hit card's size, in pixels of the HUD's reference screen.</summary>
    public const float HitCardW = 320, HitCardH = 208;
    const float MarkRadius = 9, MarkArm = 16, MarkReferenceWidth = 320, MarkMinScale = 0.6f;

    AnimalArt _art = AnimalArt.Box;
    PictureLook _look = PictureLook.Hit;
    bool _mark;
    float _along = 0.5f, _height = 0.5f;

    public AnimalPicture(Vector2 size)
    {
        CustomMinimumSize = size;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps; // the baked pictures are drawn at a third of their size
    }

    public void SetHit(AnimalArt art, bool mark, float along, float height)
    {
        _art = art; _look = PictureLook.Hit; _mark = mark; _along = Math.Clamp(along, 0f, 1f); _height = Math.Clamp(height, 0f, 1f);
        QueueRedraw();
    }

    public void SetSpecies(AnimalArt art, bool filled)
    {
        _art = art; _look = filled ? PictureLook.Filled : PictureLook.Outline; _mark = false;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // the body's rectangle: the silhouette's proportions (a plain 2:1 body without one), as large as fits
        var aspect = _art.Shape != null ? _art.Shape.Length / _art.Shape.Height : _art.Aspect;
        var rect = Fit(aspect);
        var ink = _look switch { PictureLook.Filled => ShooterTheme.Accent, PictureLook.Outline => ShooterTheme.Line, _ => ShooterTheme.Dim };
        if (_art.Picture is { } pic)
            // the picture is baked with the nose on the right: a negative width mirrors it in place (the rectangle keeps its corner)
            DrawTextureRect(_look == PictureLook.Outline ? pic.Outline : pic.Fill, new Rect2(rect.Position, new Vector2(-rect.Size.X, rect.Size.Y)), false, ink);
        else if (_art.Shape is { } shape && _look != PictureLook.Outline)
            DrawCells(shape, rect, ink);
        else if (_look == PictureLook.Filled)
            DrawStyleBox(ShooterTheme.Box(ink, 0, null, 0), rect);
        else
            DrawRect(rect, ink, false, ShooterTheme.BorderStrong);
        if (_mark) DrawMark(rect);
    }

    Rect2 Fit(float aspect)
    {
        aspect = aspect > 0 ? aspect : 2f;
        var size = Size.X / Size.Y > aspect ? new Vector2(Size.Y * aspect, Size.Y) : new Vector2(Size.X, Size.X / aspect);
        return new Rect2((Size - size) / 2, size);
    }

    /// <summary>A shape with no picture: the hit shape itself, cell by cell (each row's covered runs as one rectangle), the front of the animal on the left.</summary>
    void DrawCells(AnimalShape shape, Rect2 rect, Color ink)
    {
        var cw = rect.Size.X / shape.Cols; var ch = rect.Size.Y / shape.Rows;
        for (var row = 0; row < shape.Rows; row++)
        {
            var y = rect.Position.Y + (shape.Rows - 1 - row) * ch;
            for (var col = 0; col < shape.Cols; col++)
            {
                if (!shape.Covered(col, row)) continue;
                var start = col;
                while (col + 1 < shape.Cols && shape.Covered(col + 1, row)) col++;
                // columns run from the rear: column c is drawn at Cols - 1 - c from the left
                DrawRect(new Rect2(rect.Position.X + (shape.Cols - 1 - col) * cw, y, (col - start + 1) * cw + 0.5f, ch + 0.5f), ink);
            }
        }
    }

    /// <summary>The entry: a dark-edged ring and a cross through it, so it reads on the light body and on the panel alike.</summary>
    void DrawMark(Rect2 rect)
    {
        var s = Math.Max(MarkMinScale, Size.X / MarkReferenceWidth);
        s = Math.Min(s, 1f);
        float radius = MarkRadius * s, arm = MarkArm * s, w = ShooterTheme.BorderStrong;
        var x = 1f - _along; // the nose is on the left
        var at = rect.Position + new Vector2(x * rect.Size.X, (1f - _height) * rect.Size.Y);
        DrawArc(at, radius, 0, Mathf.Tau, 32, ShooterTheme.Outline, w + 2f);
        DrawLine(at - new Vector2(arm, 0), at + new Vector2(arm, 0), ShooterTheme.Outline, w + 2f);
        DrawLine(at - new Vector2(0, arm), at + new Vector2(0, arm), ShooterTheme.Outline, w + 2f);
        DrawArc(at, radius, 0, Mathf.Tau, 32, ShooterTheme.Danger, w);
        DrawLine(at - new Vector2(arm, 0), at + new Vector2(arm, 0), ShooterTheme.Danger, w - 1f);
        DrawLine(at - new Vector2(0, arm), at + new Vector2(0, arm), ShooterTheme.Danger, w - 1f);
        DrawCircle(at, 2.5f * s, ShooterTheme.Danger);
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
}
