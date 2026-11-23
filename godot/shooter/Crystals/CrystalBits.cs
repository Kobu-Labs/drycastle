using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>Flies circling a point of a crystal (zombite, hovnovite): small dark dots on wobbling orbits, a pure function of time.</summary>
public sealed class FlySwarm
{
    readonly MeshInstance3D[] _flies;
    readonly float[] _phase, _speed, _radius;

    public FlySwarm(Node parent, Random rng, int max)
    {
        _flies = new MeshInstance3D[max];
        _phase = new float[max];
        _speed = new float[max];
        _radius = new float[max];
        var mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 6, Rings = 3, Material = new StandardMaterial3D { AlbedoColor = new Color(0.03f, 0.03f, 0.03f), Roughness = 0.4f } };
        for (var i = 0; i < max; i++)
        {
            _flies[i] = new MeshInstance3D { Name = $"Fly{i}", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            parent.AddChild(_flies[i]);
            _phase[i] = (float)rng.NextDouble() * Mathf.Tau;
            _speed[i] = (0.7f + 0.7f * (float)rng.NextDouble()) * (rng.Next(2) == 0 ? 1 : -1);
            _radius[i] = 0.5f + 0.5f * (float)rng.NextDouble();
        }
    }

    public void Update(double time, int count, float size, Vector3 c, float radius, float speed)
    {
        for (var i = 0; i < _flies.Length; i++)
        {
            _flies[i].Visible = i < count;
            if (i >= count) continue;
            var a = (float)time * speed * _speed[i] + _phase[i];
            var r = radius * _radius[i] * (0.8f + 0.2f * Mathf.Sin(a * 0.37f + i));
            var p = c + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a * 1.7f + _phase[i]) * radius * 0.25f, Mathf.Sin(a) * r * 0.8f)
                + new Vector3(Mathf.Sin(a * 9.1f), Mathf.Sin(a * 7.3f), Mathf.Sin(a * 11.7f)) * 0.004f;
            _flies[i].Transform = new Transform3D(Basis.FromScale(Vector3.One * size * 2f), p);
        }
    }
}

/// <summary>
/// Drops that hang, swell, fall and are gone at the ground, each time from another of the given points (blood, lava, venom): a pure function of time. <c>ooze</c> &gt; 0 makes them sink at that
/// steady speed instead of falling.
/// </summary>
public sealed class DripSet
{
    readonly MeshInstance3D[] _drops;

    public DripSet(Node parent, Material mat, int max)
    {
        _drops = new MeshInstance3D[max];
        var ball = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 8, Rings = 4, Material = mat };
        for (var i = 0; i < max; i++)
        {
            _drops[i] = new MeshInstance3D { Name = $"Drop{i}", Mesh = ball, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            parent.AddChild(_drops[i]);
        }
    }

    static float H(double n)
    {
        var s = Math.Sin(n * 12.9898 + 2.9) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    public void Update(double time, IReadOnlyList<Vector3> from, int count, float interval, float size, float ooze = 0f)
    {
        if (from.Count == 0) count = 0;
        for (var j = 0; j < _drops.Length; j++)
        {
            var d = _drops[j];
            d.Visible = j < count;
            if (j >= count) continue;
            var p0 = from[0];
            // the period has to hold the longest fall
            var longest = ooze > 0f ? 1.3f / ooze : 0.55f;
            var period = Math.Max(interval, 0.2f) * (0.6f + 0.8f * H(j * 3.3)) + longest;
            var x = (time + H(j * 7.9) * 50.0) / period;
            var round = Math.Floor(x);
            var t = (float)((x - round) * period);
            p0 = from[(int)(H(j * 13.1 + round * 5.7) * from.Count) % from.Count];
            var fall = ooze > 0f ? p0.Y / ooze : Mathf.Sqrt(2f * Math.Max(p0.Y, 0.01f) / 9.8f);
            var hang = period - fall;
            if (t < hang) { d.Transform = new Transform3D(Basis.FromScale(Vector3.One * size * (0.3f + 0.7f * t / Math.Max(hang, 0.01f))), p0 + Vector3.Down * size); continue; }
            var ft = t - hang;
            var y = ooze > 0f ? p0.Y - ooze * ft : p0.Y - 4.9f * ft * ft;
            d.Transform = new Transform3D(Basis.FromScale(new Vector3(size * 0.8f, size * 1.3f, size * 0.8f)), new Vector3(p0.X, Math.Max(y, 0f), p0.Z));
        }
    }
}

/// <summary>Leaves (or petals, straws) that now and then fall from one of the given points, swaying and turning, and shrink away at the ground: a pure function of time.</summary>
public sealed class FallSet
{
    readonly MeshInstance3D[] _bits;

    public FallSet(Node parent, Mesh[] meshes, int max)
    {
        _bits = new MeshInstance3D[max];
        for (var i = 0; i < max; i++)
        {
            _bits[i] = new MeshInstance3D { Name = $"Fall{i}", Mesh = meshes[i % meshes.Length], Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            parent.AddChild(_bits[i]);
        }
    }

    static float H(double n)
    {
        var s = Math.Sin(n * 12.9898 + 4.1) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    public void Update(double time, IReadOnlyList<Vector3> from, int count, float interval, float speed, float size, Vector3 center, float height)
    {
        if (from.Count == 0) count = 0;
        speed = Math.Max(speed, 0.02f);
        for (var j = 0; j < _bits.Length; j++)
        {
            var f = _bits[j];
            if (j >= count) { f.Visible = false; continue; }
            var period = interval * (0.6f + 0.8f * H(j * 2.3)) + (height + 0.1f) / speed;
            var x = (time + H(j * 9.1) * 100.0) / period;
            var round = Math.Floor(x);
            var t = (float)((x - round) * period);
            var p0 = from[(int)(H(j * 5.9 + round * 3.3) * from.Count) % from.Count];
            var dur = p0.Y / speed;
            f.Visible = t < dur;
            if (!f.Visible) continue;
            var away = new Vector3(p0.X - center.X, 0, p0.Z - center.Z).Normalized();
            var pos = p0 + away * (0.03f + t * 0.04f) + new Vector3(Mathf.Sin(t * 2.1f + j), 0, Mathf.Cos(t * 1.7f + j * 2f)) * 0.035f * Math.Min(t, 1f);
            pos.Y = Math.Max(p0.Y - speed * t, 0.004f);
            var basis = Basis.FromEuler(new Vector3(Mathf.Sin(t * 3.1f + j) * 0.9f, t * 1.4f + j, Mathf.Sin(t * 2.3f) * 0.7f));
            f.Transform = new Transform3D(basis.Scaled(Vector3.One * size * Mathf.Clamp((dur - t) / 0.4f, 0f, 1f)), pos);
        }
    }
}
