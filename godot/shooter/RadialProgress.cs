using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// A circular progress ring with a label under it (ticket #151): the care package's open hold, and reusable for any hold
/// (the corpse pickup). The ring fills clockwise from the top as <see cref="Value"/> goes from 0 to 1. The label (for
/// example "Opening...") says what the ring is for, so the state is never colour alone. There is no numeric countdown.
/// Colours and sizes come from <see cref="ShooterTheme"/>. It only redraws when the ring moves by a visible step, so a
/// hold costs nothing per frame. Reduce motion is the caller's to honour: the fill itself does not animate.
/// </summary>
public sealed partial class RadialProgress : VBoxContainer
{
    /// <summary>Ring diameter in reference pixels (1080p).</summary>
    public const float Diameter = ShooterTheme.Space * 12;
    /// <summary>Whole control height: the ring and room for one line of label.</summary>
    public const float Height = Diameter + ShooterTheme.Space * 5;
    /// <summary>Width of the control (the ring's width).</summary>
    public const float Width = Diameter;

    /// <summary>Ring stroke width in reference pixels.</summary>
    const float Stroke = ShooterTheme.BorderStrong * 2;
    /// <summary>Redraw steps per full turn: a value change smaller than 1/Steps does not redraw.</summary>
    const int Steps = 120;
    /// <summary>Polyline segments for a full turn.</summary>
    const int Segments = 64;

    readonly RingArt _ring;
    readonly Label _label;
    float _value;
    int _shownStep = -1;

    public RadialProgress()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _ring = new RingArt(this) { CustomMinimumSize = new Vector2(Diameter, Diameter), MouseFilter = MouseFilterEnum.Ignore };
        _label = new Label { HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = "HudHead", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_ring);
        AddChild(_label);
    }

    /// <summary>The label under the ring.</summary>
    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    /// <summary>The fill, 0 (empty) to 1 (full). Values outside the range are clamped.</summary>
    public float Value
    {
        get => _value;
        set
        {
            _value = Mathf.Clamp(value, 0f, 1f);
            var step = Mathf.FloorToInt(_value * Steps);
            if (step == _shownStep) return;
            _shownStep = step;
            _ring.QueueRedraw();
        }
    }

    /// <summary>The ring itself: a dim track, a dark disc behind it and the accent arc for the fill.</summary>
    sealed partial class RingArt : Control
    {
        readonly RadialProgress _owner;

        public RingArt(RadialProgress owner) => _owner = owner;

        public override void _Draw()
        {
            var centre = Size / 2;
            var radius = Mathf.Min(Size.X, Size.Y) / 2 - Stroke / 2;
            DrawCircle(centre, radius + Stroke / 2, ShooterTheme.Scrim);
            DrawArc(centre, radius, 0, Mathf.Tau, Segments, ShooterTheme.Line, Stroke, true);
            var v = _owner._value;
            if (v <= 0) return;
            var start = -Mathf.Pi / 2;
            DrawArc(centre, radius, start, start + Mathf.Tau * v, Math.Max(2, (int)(Segments * v)), ShooterTheme.Accent, Stroke, true);
        }
    }
}
