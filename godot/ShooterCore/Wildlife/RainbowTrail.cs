using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The maths of the Unicorn's rainbow trail (client-only looks, <see cref="AnimalDef.Fx"/> "unicorn"): the history of the points the body passed, and the ribbon built from it.
/// The history keeps a point every <see cref="SampleMetres"/> (and at most every <see cref="SampleSeconds"/>) for <see cref="LifeSeconds"/>, at most <see cref="MaxPoints"/>; a jump of more than
/// <see cref="TeleportMetres"/> between two updates clears it; a dead body adds nothing and what is left fades out. The ribbon is a band <see cref="BandHeight"/> m across
/// whose <see cref="Rows"/> vertex rows go red to violet (<see cref="Stripes"/> stripes); it turns about the path to face the camera, so it reads from every side. No Godot types: the buffers
/// are the caller's and are reused.
/// </summary>
public sealed class RainbowTrail
{
    public const int MaxPoints = 64, Stripes = 7, Rows = Stripes + 1;
    public const float LifeSeconds = 8f, SampleMetres = 0.4f, SampleSeconds = 0.1f, TeleportMetres = 8f, BandHeight = 0.5f;
    /// <summary>Hue (0..1 of the colour wheel) of the violet end: red is 0.</summary>
    public const float VioletHue = 0.78f;
    /// <summary>Opacity of the freshest part of the band.</summary>
    public const float PeakAlpha = 0.85f;
    /// <summary>Most vertices and indices <see cref="Build"/> writes: the history and the live head, <see cref="Rows"/> vertices each; <see cref="Stripes"/> quads between two points.</summary>
    public const int MaxVertices = (MaxPoints + 1) * Rows, MaxIndices = MaxPoints * Stripes * 6;

    readonly Vector3[] _pos = new Vector3[MaxPoints];
    readonly float[] _age = new float[MaxPoints];
    float _sinceSample;
    Vector3 _head;
    bool _hasHead;

    /// <summary>Points in the history (the live head not counted).</summary>
    public int Count { get; private set; }
    /// <summary>True while the body is alive and its live position joins the band to the newest point.</summary>
    public bool Attached { get; private set; }
    /// <summary>Nothing left to draw: no history.</summary>
    public bool Empty => Count == 0;

    public Vector3 PointAt(int i) => _pos[i];
    public float AgeAt(int i) => _age[i];

    public void Clear() { Count = 0; _hasHead = false; Attached = false; _sinceSample = 0; }

    /// <summary>
    /// One frame: ages the points and drops the old, and while <paramref name="alive"/> follows the body to <paramref name="pos"/> (the middle of the body, the band's centre).
    /// A jump of more than <see cref="TeleportMetres"/> since the last update starts the history over.
    /// </summary>
    public void Update(float dt, Vector3 pos, bool alive)
    {
        var drop = 0;
        for (var i = 0; i < Count; i++)
        {
            _age[i] += dt;
            if (_age[i] > LifeSeconds) drop = i + 1;
        }
        if (drop > 0) Remove(drop);
        _sinceSample += dt;
        if (!alive) { Attached = false; _hasHead = false; return; }
        if (_hasHead && Vector3.Distance(_head, pos) > TeleportMetres) Clear();
        _head = pos; _hasHead = true;
        if (Count == 0 || (Vector3.Distance(_pos[Count - 1], pos) >= SampleMetres && _sinceSample >= SampleSeconds))
        {
            if (Count == MaxPoints) Remove(1);
            _pos[Count] = pos; _age[Count] = 0; Count++;
            _sinceSample = 0;
        }
        Attached = true;
    }

    void Remove(int n)
    {
        Count -= n;
        if (Count > 0) { Array.Copy(_pos, n, _pos, 0, Count); Array.Copy(_age, n, _age, 0, Count); }
    }

    /// <summary>Opacity of a point of this age: 1 fresh, 0 at <see cref="LifeSeconds"/>, easing out (a squared fall, so the tail thins early).</summary>
    public static float Fade(float age)
    {
        var t = Math.Clamp(1f - age / LifeSeconds, 0f, 1f);
        return t * t;
    }

    /// <summary>Hue of a row of the band (0 the top, red; <see cref="Rows"/> - 1 the bottom, violet), 0..<see cref="VioletHue"/>.</summary>
    public static float RowHue(int row) => Math.Clamp(row, 0, Rows - 1) / (float)(Rows - 1) * VioletHue;

    /// <summary>Linear RGB of a hue (0..1 round the wheel) at <paramref name="saturation"/> and full value.</summary>
    public static Vector3 HueRgb(float hue, float saturation = 0.9f)
    {
        var h = (hue - MathF.Floor(hue)) * 6f;
        var k = (int)h;
        var f = h - k;
        var (p, q, t) = (1f - saturation, 1f - saturation * f, 1f - saturation * (1f - f));
        return k switch { 0 => new Vector3(1, t, p), 1 => new Vector3(q, 1, p), 2 => new Vector3(p, 1, t), 3 => new Vector3(p, q, 1), 4 => new Vector3(t, p, 1), _ => new Vector3(1, p, q) };
    }

    /// <summary>The ribbon of the history (and, while attached, the live head) seen from <paramref name="camera"/>; false when there is less than a segment of it. Returns the counts written to the caller's buffers.</summary>
    public bool Build(Vector3 camera, Span<Vector3> verts, Span<Vector4> colours, Span<Vector2> uvs, Span<int> indices, out int vertexCount, out int indexCount)
    {
        vertexCount = indexCount = 0;
        var n = Count + (Attached && Count > 0 && Vector3.Distance(_pos[Count - 1], _head) > 0.01f ? 1 : 0);
        if (n < 2) return false;
        var along = 0f;
        for (var i = 0; i < n; i++)
        {
            var p = Point(i);
            var prev = Point(Math.Max(i - 1, 0));
            var next = Point(Math.Min(i + 1, n - 1));
            if (i > 0) along += Vector3.Distance(prev, p);
            var tangent = next - prev;
            var side = Vector3.Cross(tangent, camera - p);
            side = side.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(side);
            if (side.Y < 0) side = -side; // red stays on top
            var age = i < Count ? _age[i] : 0f;
            var fade = Fade(age);
            var half = BandHeight * (0.35f + 0.65f * fade) * 0.5f; // the tail narrows
            for (var r = 0; r < Rows; r++)
            {
                var v = i * Rows + r;
                var across = r / (float)(Rows - 1);
                verts[v] = p + side * ((0.5f - across) * 2f * half);
                var c = HueRgb(RowHue(r));
                colours[v] = new Vector4(c, fade * PeakAlpha);
                uvs[v] = new Vector2(along, across);
            }
        }
        var k = 0;
        for (var i = 0; i < n - 1; i++)
            for (var r = 0; r < Stripes; r++)
            {
                int a = i * Rows + r, b = a + 1, c = a + Rows, d = c + 1;
                indices[k++] = a; indices[k++] = b; indices[k++] = c;
                indices[k++] = b; indices[k++] = d; indices[k++] = c;
            }
        vertexCount = n * Rows; indexCount = k;
        return true;
    }

    Vector3 Point(int i) => i < Count ? _pos[i] : _head;
}
