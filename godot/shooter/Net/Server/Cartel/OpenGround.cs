using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Waters;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// What is open ground for a Rival crew's vehicle on the server (docs/cartel.md "Assaults", "Chase cars"; <see cref="OffRoadRoute"/>): a car-wide strip of terrain with nothing solid on it (a building, a tree
/// trunk, a fence, a placed solid, a closed door, a parked vehicle: the static world a ground vehicle collides with), no water and no slope the car cannot climb. The line is looked at every sample_m in three lanes
/// (the vehicle's width) with rays a little over the ground, so a trunk between two lanes still stops a car it could not pass; the ground comes from the map. One ray is a few microseconds: the callers ask a
/// handful of lines every second or two.
/// </summary>
internal sealed class OpenGround
{
    readonly Func<WorldQuery> _query;
    readonly Water _water;
    readonly Func<float, float, float> _ground;
    readonly OffRoadData _d;

    /// <param name="ground">The terrain height under (x, z); NaN where the map has none (outside it: not open).</param>
    public OpenGround(Func<WorldQuery> query, Water water, Func<float, float, float> ground, OffRoadData d)
    {
        _query = query; _water = water; _ground = ground; _d = d;
    }

    /// <summary>The numbers it goes by.</summary>
    public OffRoadData Numbers => _d;

    /// <summary>The line from <paramref name="a"/> to <paramref name="b"/> is open for a vehicle of this half width.</summary>
    public bool Line(NVec2 a, NVec2 b, float halfWidthM)
    {
        var len = NVec2.Distance(a, b);
        if (len < 0.01f) return Spot(a, halfWidthM);
        var dir = (b - a) / len;
        var side = new NVec2(-dir.Y, dir.X) * (halfWidthM + _d.MarginM);
        var n = Math.Max(1, (int)MathF.Ceiling(len / _d.SampleM));
        var q = _query();
        Span<NVec2> lane = stackalloc NVec2[3];
        Span<Vector3> prev = stackalloc Vector3[3];
        var prevH = float.NaN;
        for (var i = 0; i <= n; i++)
        {
            var c = a + dir * (len * i / n);
            var h = _ground(c.X, c.Y);
            if (!float.IsFinite(h) || _water.BodyAt(c.X, c.Y) != null) return false;
            lane[0] = c - side; lane[1] = c; lane[2] = c + side;
            if (i > 0 && MathF.Abs(h - prevH) / (len / n) > _d.MaxGrade) return false;
            for (var k = 0; k < 3; k++)
            {
                var hk = k == 1 ? h : _ground(lane[k].X, lane[k].Y);
                if (!float.IsFinite(hk)) return false;
                var p = new Vector3(lane[k].X, hk + _d.RayUpM, lane[k].Y);
                if (i > 0 && !float.IsPositiveInfinity(q.Ahead(MoverKind.GroundVehicle, prev[k], p))) return false;
                prev[k] = p;
            }
            prevH = h;
        }
        return true;
    }

    /// <summary>A vehicle of this half width can stand at <paramref name="p"/>: ground, no water and nothing solid within its reach all round.</summary>
    public bool Spot(NVec2 p, float halfWidthM)
    {
        var h = _ground(p.X, p.Y);
        if (!float.IsFinite(h) || _water.BodyAt(p.X, p.Y) != null) return false;
        var q = _query();
        var from = new Vector3(p.X, h + _d.RayUpM, p.Y);
        var reach = halfWidthM + _d.MarginM + 2f; // a car's half length and a margin
        for (var k = 0; k < 8; k++)
        {
            var a = k * MathF.PI / 4f;
            var to = from + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * reach;
            if (!float.IsPositiveInfinity(q.Ahead(MoverKind.GroundVehicle, from, to))) return false;
        }
        return true;
    }
}
