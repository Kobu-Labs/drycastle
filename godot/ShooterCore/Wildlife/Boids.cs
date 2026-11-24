using System.Numerics;

namespace Drycastle.ShooterCore.Wildlife;

/// <summary>The numbers of a boid swarm (wildlife.json, "flocks"): Reynolds' three rules plus a pull to the flock's goal. Metres and seconds.</summary>
public sealed class BoidRules
{
    /// <summary>Birds within this distance count as neighbours for alignment and cohesion.</summary>
    public float NeighbourM { get; set; } = 9;
    /// <summary>Birds closer than this push each other apart.</summary>
    public float SeparationM { get; set; } = 2.5f;
    public float SeparationWeight { get; set; } = 1.8f;
    public float AlignmentWeight { get; set; } = 1.0f;
    public float CohesionWeight { get; set; } = 0.7f;
    /// <summary>The pull to the goal (the flock's centre, which the server moves).</summary>
    public float GoalWeight { get; set; } = 1.0f;
    /// <summary>Beyond this distance from the goal the pull is tripled: a bird never loses its flock.</summary>
    public float LeashM { get; set; } = 18;
    public float MinSpeed { get; set; } = 6;
    public float MaxSpeed { get; set; } = 11;
    /// <summary>The most a bird can change its velocity, m/s per second.</summary>
    public float MaxAccel { get; set; } = 14;
    /// <summary>How far above or below the goal's height a bird may fly before it is pulled back, metres.</summary>
    public float BandM { get; set; } = 5;
    /// <summary>While scattering a bird may fly this many times faster and ignores cohesion.</summary>
    public float PanicSpeedFactor { get; set; } = 1.7f;
    /// <summary>A bird starts to sheer off an obstacle (an aircraft) this far outside its radius, metres. A fast aircraft needs room: the birds see it coming.</summary>
    public float AvoidMarginM { get; set; } = 22;
    /// <summary>How hard a bird dodges: a multiple of <see cref="MaxAccel"/> at the obstacle's edge.</summary>
    public float AvoidAccelFactor { get; set; } = 3;
    /// <summary>How far ahead (seconds) a bird looks for the closest approach to an obstacle's path.</summary>
    public float AvoidHorizonS { get; set; } = 2.5f;
}

/// <summary>Something the birds fly round and never through (an aircraft): a sphere that may be moving.</summary>
public readonly record struct BoidObstacle(Vector3 Position, Vector3 Velocity, float Radius);

/// <summary>
/// A swarm of boids (GLOSSARY "Flock"): <c>Count</c> birds that keep apart, line up, stay together and follow a moving goal. Godot-free and deterministic for a
/// seed. The server does not send the birds, only the flock's centre; every client runs a swarm of its own around that centre, so the birds are cosmetic (nobody can
/// shoot one) and two clients see the same flock in slightly different shapes.
/// </summary>
public sealed class BoidSwarm
{
    readonly BoidRules _r;
    readonly Vector3[] _pos, _vel, _acc;

    public BoidSwarm(int count, Vector3 centre, BoidRules rules, int seed)
    {
        _r = rules;
        _pos = new Vector3[count]; _vel = new Vector3[count]; _acc = new Vector3[count];
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var a = (float)rng.NextDouble() * MathF.Tau;
            var reach = (float)Math.Sqrt(rng.NextDouble()) * rules.LeashM * 0.5f;
            _pos[i] = centre + new Vector3(MathF.Cos(a) * reach, ((float)rng.NextDouble() - 0.5f) * rules.BandM, MathF.Sin(a) * reach);
            var h = (float)rng.NextDouble() * MathF.Tau;
            _vel[i] = new Vector3(MathF.Cos(h), 0, MathF.Sin(h)) * (rules.MinSpeed + rules.MaxSpeed) * 0.5f;
        }
    }

    public int Count => _pos.Length;
    public IReadOnlyList<Vector3> Positions => _pos;
    public IReadOnlyList<Vector3> Velocities => _vel;

    /// <summary>The mean of the birds' positions.</summary>
    public Vector3 Centre
    {
        get
        {
            var c = Vector3.Zero;
            foreach (var p in _pos) c += p;
            return _pos.Length == 0 ? c : c / _pos.Length;
        }
    }

    /// <summary>
    /// Advances every bird by <paramref name="dt"/> seconds towards <paramref name="goal"/>. <paramref name="panic"/>: the flock is scattering (faster, no cohesion).
    /// <paramref name="obstacles"/>: spheres (aircraft) the birds flow round: each bird looks for where its path comes closest to the obstacle's and sheers off
    /// sideways, so a flock the aircraft flies into splits and closes again behind it; and no bird is ever inside one.
    /// </summary>
    public void Step(float dt, Vector3 goal, bool panic = false, IReadOnlyList<BoidObstacle>? obstacles = null)
    {
        if (dt <= 0) return;
        var n = _pos.Length;
        var maxSpeed = _r.MaxSpeed * (panic ? _r.PanicSpeedFactor : 1);
        var minSpeed = _r.MinSpeed * (panic ? _r.PanicSpeedFactor : 1);
        for (var i = 0; i < n; i++)
        {
            Vector3 sep = Vector3.Zero, vSum = Vector3.Zero, pSum = Vector3.Zero;
            var near = 0;
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                var d = _pos[i] - _pos[j];
                var dist = d.Length();
                if (dist >= _r.NeighbourM) continue;
                near++;
                vSum += _vel[j]; pSum += _pos[j];
                if (dist < _r.SeparationM && dist > 1e-4f) sep += d / (dist * dist) * _r.SeparationM; // stronger the closer
            }
            var steer = sep * _r.SeparationWeight;
            if (near > 0)
            {
                steer += (vSum / near - _vel[i]) * 0.2f * _r.AlignmentWeight;
                if (!panic) steer += Unit(pSum / near - _pos[i]) * _r.CohesionWeight;
            }
            var toGoal = goal - _pos[i];
            var pull = toGoal.Length() > _r.LeashM ? 3f : 1f;
            steer += Unit(new Vector3(toGoal.X, 0, toGoal.Z)) * _r.GoalWeight * pull;
            var dy = _pos[i].Y - goal.Y; // a soft band of height around the goal
            if (MathF.Abs(dy) > _r.BandM) steer.Y -= MathF.Sign(dy) * (MathF.Abs(dy) - _r.BandM) * 0.6f;
            else steer.Y -= dy * 0.05f;
            _acc[i] = LimitLength(steer * _r.MaxAccel, _r.MaxAccel);
            if (obstacles != null) _acc[i] += Avoidance(i, obstacles);
        }
        for (var i = 0; i < n; i++)
        {
            var v = _vel[i] + _acc[i] * dt;
            var speed = v.Length();
            if (speed < 1e-4f) v = new Vector3(minSpeed, 0, 0); // never hover
            else if (speed > maxSpeed) v *= maxSpeed / speed;
            else if (speed < minSpeed) v *= minSpeed / speed;
            _vel[i] = v;
            _pos[i] += v * dt;
            if (obstacles != null) KeepOut(i, obstacles);
        }
    }

    /// <summary>The push away from the closest approach of every obstacle that will come near: sideways, harder the closer and the sooner.</summary>
    Vector3 Avoidance(int i, IReadOnlyList<BoidObstacle> obstacles)
    {
        var total = Vector3.Zero;
        foreach (var o in obstacles)
        {
            var r = _pos[i] - o.Position;
            var vrel = _vel[i] - o.Velocity;
            var vv = vrel.LengthSquared();
            var tca = vv > 1e-4f ? Math.Clamp(-Vector3.Dot(r, vrel) / vv, 0, _r.AvoidHorizonS) : 0f; // when it comes closest
            var d = r + vrel * tca;
            var dist = d.Length();
            var influence = o.Radius + _r.AvoidMarginM;
            if (dist >= influence) continue;
            var away = dist > 1e-3f ? d / dist : Unit(Vector3.Cross(vrel, Vector3.UnitY)); // dead ahead: break to one side
            if (away == Vector3.Zero) away = Vector3.UnitX;
            total += away * ((influence - dist) / influence) * (1f / (0.4f + tca)) * _r.AvoidAccelFactor * _r.MaxAccel;
        }
        return LimitLength(total, _r.AvoidAccelFactor * _r.MaxAccel);
    }

    /// <summary>A bird the aircraft overtook is carried out to the sphere's surface (and loses the part of its speed that pointed in): it slides round the body.</summary>
    void KeepOut(int i, IReadOnlyList<BoidObstacle> obstacles)
    {
        foreach (var o in obstacles)
        {
            var d = _pos[i] - o.Position;
            var dist = d.Length();
            if (dist >= o.Radius) continue;
            var n = dist > 1e-3f ? d / dist : Vector3.UnitY;
            _pos[i] = o.Position + n * o.Radius;
            var inward = Vector3.Dot(_vel[i] - o.Velocity, n);
            if (inward < 0) _vel[i] -= n * inward;
        }
    }

    static Vector3 Unit(Vector3 v) { var l = v.Length(); return l > 1e-4f ? v / l : Vector3.Zero; }

    static Vector3 LimitLength(Vector3 v, float max) { var l = v.Length(); return l > max ? v * (max / l) : v; }
}
