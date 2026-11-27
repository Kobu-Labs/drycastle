using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Turrets;

namespace Drycastle.ShooterCore.Wildlife;

/// <summary>What a flock of birds is doing; the replicated state byte.</summary>
public enum FlockState : byte { Cruise = 0, Scatter = 1 }

/// <summary>Flock world entity rules: the wire type id and the owner field (flock species index in the low byte, bird count in the high byte).</summary>
public static class FlockRules
{
    /// <summary>Wire id of the flock world entity type (50 animal, 51 citizen, 52 demon, 53 poop).</summary>
    public const byte TypeId = 54;

    public static ushort Look(int species, int birds) => (ushort)(species | birds << 8);
    public static int SpeciesOf(ushort look) => look & 0xFF;
    public static int BirdsOf(ushort look) => look >> 8;

    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "flock", HasHp: false, Interest: true));
    }
}

/// <summary>
/// A flock of birds as the server sees it (GLOSSARY "Flock"): one world entity (wire type <see cref="FlockRules.TypeId"/>) flying a lazy tour of waypoints
/// about where it formed, at a height above the ground, and scattering away from a player or moving vehicle that comes too close. The birds themselves are not
/// replicated: each client flies <see cref="Count"/> boids (<see cref="BoidSwarm"/>) around this entity's position. Cosmetic: no hit points, nobody can shoot it.
/// Godot-free and deterministic for a seed.
/// </summary>
public sealed class Flock : IWorldEntity
{
    readonly FlockSpecies _cfg;
    readonly IAnimalWorld _world;
    readonly Random _rng;

    public static readonly StateMachineSpec<FlockState> Spec = StateMachineSpec<FlockState>.Define(
            "Flock.Behaviour", "What a flock of birds is doing: fly its tour of waypoints, or scatter away from a player or vehicle that came too close.", FlockState.Cruise)
        .Allow(FlockState.Cruise, FlockState.Scatter)
        .Allow(FlockState.Scatter, FlockState.Cruise)
        .Note(FlockState.Cruise, "Flies to a waypoint near home at the species' speed (no clock: a waypoint is reached by distance), then picks the next. A threat within scatter_radius_m sends it to Scatter.")
        .Note(FlockState.Scatter, "Flies away from the threat, faster and climbing (clock: scatter_seconds), then returns to Cruise and heads for its next waypoint.")
        .Build();

    readonly StateMachine<FlockState> _fsm = new(Spec);
    Vector3 _waypoint;
    Vector3 _from;
    float _altitude;
    float _targetAltitude;

    public Flock(FlockSpecies cfg, int species, int birds, Vector3 home, IAnimalWorld world, int seed)
    {
        _cfg = cfg; Species = species; Birds = birds; Home = home; _world = world;
        _rng = new Random(seed);
        Yaw = (float)_rng.NextDouble() * MathF.Tau;
        _altitude = _targetAltitude = Between(cfg.AltitudeMinM, cfg.AltitudeMaxM);
        Position = Aloft(home.X, home.Z);
        _waypoint = NextWaypoint();
        _fsm.Restart(float.PositiveInfinity);
    }

    public int Species { get; }
    public int Birds { get; }
    public Vector3 Home { get; }
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public FlockState State => _fsm.Current;

    public byte TypeId => FlockRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended => null;

    float Between(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    /// <summary>The point above (x, z): the ground there (or home's height where there is none) plus the current altitude.</summary>
    Vector3 Aloft(float x, float z) => new(x, (_world.GroundY(x, z) ?? Home.Y) + _altitude, z);

    Vector3 NextWaypoint()
    {
        var a = Between(0, MathF.Tau);
        var reach = (float)Math.Sqrt(_rng.NextDouble()) * _cfg.RangeM;
        _targetAltitude = Between(_cfg.AltitudeMinM, _cfg.AltitudeMaxM);
        return new Vector3(Home.X + MathF.Cos(a) * reach, 0, Home.Z + MathF.Sin(a) * reach);
    }

    public void Tick(float dt)
    {
        if (dt <= 0) return;
        if (State == FlockState.Cruise && _world.NearestThreat(Position, _cfg.ScatterRadiusM) is { } threat)
        {
            _from = threat;
            _fsm.Enter(FlockState.Scatter, _cfg.ScatterSeconds);
        }
        var scatter = State == FlockState.Scatter;
        Vector3 want;
        float speed;
        if (scatter)
        {
            want = Position * 2 - _from; // away from the threat
            speed = _cfg.SpeedMps * 1.8f;
            _targetAltitude = _cfg.AltitudeMaxM * 1.4f;
            if (_fsm.Tick(dt)) { _fsm.Enter(FlockState.Cruise); _waypoint = NextWaypoint(); return; }
        }
        else
        {
            want = _waypoint;
            speed = _cfg.SpeedMps;
            if (Horizontal(Position, _waypoint) < 6) _waypoint = NextWaypoint();
        }
        Yaw = TurretMath.StepAngle(Yaw, TurretMath.YawTo(Position, want), _cfg.TurnRateDegPerS * MathF.PI / 180f * dt);
        _altitude += Math.Clamp(_targetAltitude - _altitude, -3 * dt, 3 * dt); // eases to the new height
        var x = Position.X - MathF.Sin(Yaw) * speed * dt;
        var z = Position.Z - MathF.Cos(Yaw) * speed * dt;
        Position = Aloft(x, z);
    }

    static float Horizontal(Vector3 a, Vector3 b) { var dx = a.X - b.X; var dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }

    /// <summary>Nothing hurts it.</summary>
    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, FlockRules.Look(Species, Birds), (byte)State, 0);
}
