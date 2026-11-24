using System.Numerics;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Wildlife;

public sealed partial class Animal
{
    /// <summary>Why a step did not happen (<see cref="Step"/>).</summary>
    enum Block : byte { None, Ground, Solid, Body }

    const float WalkSteerRad = 1.0f, FleeSteerRad = 1.3f, FleeEscalateRad = 0.5f, MaxBiasRad = 2.8f, SteerSeconds = 1.2f, FleeJudgeSeconds = 0.5f;
    /// <summary>The ray looks this far ahead at least, or this many seconds of the speed; and a wall closer than the step plus the gap stops it.</summary>
    const float LookMinM = 2.5f, LookSeconds = 0.5f, GapM = 0.15f, RecheckSeconds = 0.3f;
    /// <summary>A player's footprint for contact: the player capsule's radius.</summary>
    const float PlayerRadius = MoveConsts.CapsuleRadius;

    [ThreadStatic] static List<Vector3>? s_players;

    /// <summary>
    /// Turns towards <paramref name="want"/> and, once roughly facing it, walks one step forward. Blocked (and why) when the ground refuses it (no ground, a
    /// drop or climb bigger than <see cref="AnimalsFile.MaxStepM"/>), a player or another animal stands in the way (its Body box would touch
    /// theirs: <see cref="BodyBox"/>; a player is never pushed, the animal yields) or something solid is ahead (a ray, rarely: <see cref="ClearPath"/>).
    /// Turning in place is not blocked.
    /// </summary>
    Block Step(float want, float speed, float dt)
    {
        var t = Prof.Begin(Prof.S.AnimalStep);
        var block = StepInner(want, speed, dt);
        Prof.End(Prof.S.AnimalStep, t);
        return block;
    }

    Block StepInner(float want, float speed, float dt)
    {
        var turned = TurretMath.StepAngle(Yaw, want, Def.TurnRateDegPerS * MathF.PI / 180f * dt);
        if (_recheck.Running && turned != Yaw && SolidAlong(_open ?? _world, turned)) turned = Yaw; // just blocked by a wall: it does not swing its nose into it
        Yaw = turned;
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > 1f) return Block.None;
        var f = Forward;
        var step = speed * dt;
        var x = Position.X + f.X * speed * dt; // (not f * step: the same sum, rounded the way it always was, keeps the golden trajectories)
        var z = Position.Z + f.Z * speed * dt;
        var world = _open ?? _world;
        if (world.GroundY(x, z) is not { } y || MathF.Abs(y - Position.Y) > _file.MaxStepM) return Block.Ground;
        var bodyT = Prof.Begin(Prof.S.AnimalBodyAhead);
        var body = BodyAhead(x, z);
        Prof.End(Prof.S.AnimalBodyAhead, bodyT);
        if (body) return Block.Body;
        if (SolidAhead(world, step, speed, dt)) return Block.Solid;
        Position = new Vector3(x, y, z);
        return Block.None;
    }

    /// <summary>Whether something solid lies within the reach of its body from the origin along <paramref name="yaw"/>: where its nose would swing to. One ray, asked only while it is up against a wall.</summary>
    bool SolidAlong(IAnimalWorld world, float yaw)
    {
        var (s, c) = MathF.SinCos(yaw);
        var reach = _bodyAhead + _bodyL / 2 + 0.1f;
        return world.SolidAhead(Position, new Vector3(-s, 0, -c), reach, Math.Clamp(_bodyH * 0.6f, 0.25f, 0.8f)) < reach;
    }

    /// <summary>Whether the animal, one step on, would touch a player or another animal it is not already touching (or is closing in on).</summary>
    bool BodyAhead(float x, float z)
    {
        var to = BodyBox.Of(new Vector3(x, Position.Y, z), Yaw, _bodyW, _bodyL, _bodyAhead);
        var from = Body;
        var crowdT = Prof.Begin(Prof.S.AnimalCrowd);
        var crowded = Crowd?.Blocks(this, from, to) == true;
        Prof.End(Prof.S.AnimalCrowd, crowdT);
        if (crowded) return true;
        var near = s_players ??= new List<Vector3>();
        near.Clear();
        _world.PlayersNear(Position, BodyReach + PlayerRadius + 1f, near);
        foreach (var p in near)
        {
            var d = to.DistanceTo(p.X, p.Z);
            if (d < PlayerRadius && (from.DistanceTo(p.X, p.Z) >= PlayerRadius || d < from.DistanceTo(p.X, p.Z))) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether something solid lies within a step (and the gap) of the front of its body. Two rays (the front corners) a good way ahead, kept as the <see cref="ClearPath"/>
    /// they found free: the next steps along the same heading ask nobody.
    /// </summary>
    bool SolidAhead(IAnimalWorld world, float step, float speed, float dt)
    {
        if (_recheck.Tick(dt)) _clear.Reset();
        if (_clear.Covers(Position.X, Position.Z, Yaw, step)) return false;
        if (_clear.KnownBlocked(Position.X, Position.Z, Yaw)) return true;
        var look = MathF.Max(LookMinM, speed * LookSeconds);
        var f = Forward;
        var reach = _bodyAhead + _bodyL / 2; // from the front of the body, not the origin
        var (fx, fz) = (Position.X + f.X * reach, Position.Z + f.Z * reach);
        var (rx, rz) = (-f.Z * _bodyW / 2, f.X * _bodyW / 2); // half its width to its right: the two front corners, so a wall beside the nose stops it too
        var height = Math.Clamp(_bodyH * 0.6f, 0.25f, 0.8f);
        var d = MathF.Min(world.SolidAhead(new Vector3(fx + rx, Position.Y, fz + rz), f, look, height), world.SolidAhead(new Vector3(fx - rx, Position.Y, fz - rz), f, look, height));
        if (d < step + GapM) { _clear.SetBlocked(Position.X, Position.Z, Yaw); _recheck.Start(RecheckSeconds); return true; }
        _clear.Set(Position.X, Position.Z, Yaw, MathF.Min(d, look) - GapM);
        return false;
    }
}
