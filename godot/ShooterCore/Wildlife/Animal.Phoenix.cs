using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The Phoenix half of <see cref="Animal"/> (GLOSSARY "Phoenix", <see cref="PhoenixRules"/>): an animal built with a <see cref="PhoenixSpawn"/> does not wander, flee or get thrown. Its phases are states of
/// <see cref="Animal.Spec"/> (Flying, Falling, Crashed, Burning, Dead) and the one machine is the single authority:
/// <list type="bullet">
/// <item>Flying: round a circle at its altitude; the position is the BODY CENTRE (the model's origin while it flies). Any damaging hit sets it Falling (any weapon); it never dies from the air.</item>
/// <item>Falling: a straight dive to the crash site (<see cref="PhoenixCrash"/>), <see cref="PhoenixRules.FallSeconds"/>; more hits change nothing.</item>
/// <item>Crashed: on the ground at the site, alive and still (position = the ground, as the model's Perched clip puts the origin there); stays until killed or the round ends.</item>
/// <item>Burning: the second hit, valid only here; it is dead (<see cref="Died"/>, the Hunt), burns <see cref="PhoenixRules.BurnSeconds"/>, then Dead and <see cref="Dropped"/>.</item>
/// </list>
/// </summary>
public sealed partial class Animal
{
    PhoenixSpawn? _sky;
    Vector3 _patrol;      // the circle's centre; Y is the flying height (the body centre's)
    float _patrolRadius, _patrolAngle;
    int _patrolDir = 1;
    Vector3 _diveFrom, _diveTo;
    float _diveSeconds;

    /// <summary>Whether it is a Phoenix: built with a <see cref="PhoenixSpawn"/>.</summary>
    public bool IsPhoenix => _sky != null;

    /// <summary>The pitch of the body in radians (0 level, negative nose down); only a Phoenix has one: the dive line's while Falling. The wire's pitch field.</summary>
    public float Pitch { get; private set; }

    /// <summary>The crash site while Falling and Crashed (on the ground), null otherwise.</summary>
    public Vector3? CrashSite => State is AnimalState.Falling or AnimalState.Crashed ? _diveTo : null;

    /// <summary>Raised once when a hit makes it Fall: the animal, where it was hit, the crash site and the fall's length in seconds.</summary>
    public event Action<Animal, Vector3, Vector3, float>? Fell;

    /// <summary>Raised once when the fall ends and it sits at the crash site.</summary>
    public event Action<Animal>? Landed;

    /// <summary>Raised once when the flame ends (Burning to Dead): the animal and the mineral it leaves where it burned (<see cref="PhoenixRules.Drops"/>), at <see cref="Position"/>.</summary>
    public event Action<Animal, string>? Dropped;

    void TakeOff(PhoenixSpawn sky)
    {
        _sky = sky;
        var r = sky.Rules;
        var ground = Position.Y;
        _patrol = new Vector3(sky.Centre.X, ground + r.AltitudeMinM + (float)_rng.NextDouble() * (r.AltitudeMaxM - r.AltitudeMinM), sky.Centre.Z);
        _patrolRadius = sky.RadiusM;
        _patrolAngle = (float)_rng.NextDouble() * MathF.Tau;
        _patrolDir = _rng.NextDouble() < 0.5 ? 1 : -1;
        _fsm.Enter(AnimalState.Flying);
        Fly();
    }

    /// <summary>On the circle at its angle, facing along it, level.</summary>
    void Fly()
    {
        Position = PhoenixFlight.OnCircle(_patrol, _patrolRadius, _patrolAngle);
        Yaw = PhoenixFlight.CircleYaw(_patrolAngle, _patrolDir);
        Pitch = 0;
    }

    void TickPhoenix(float dt)
    {
        var rules = _sky!.Rules;
        var clock = _fsm.Tick(dt);
        switch (State)
        {
            case AnimalState.Flying:
                _patrolAngle += _patrolDir * rules.SpeedMps / _patrolRadius * dt;
                Fly();
                break;
            case AnimalState.Falling:
                Position = PhoenixFlight.Dive(_diveFrom, _diveTo, _fsm.Elapsed / _diveSeconds);
                if (clock) Land();
                break;
            case AnimalState.Burning:
                if (clock) Burnt();
                break;
            case AnimalState.Dead:
                if (_fsm.Elapsed >= _file.CorpseSeconds) Ended = WorldDespawnReason.Expired;
                break;
        }
    }

    /// <summary>A damaging hit (<see cref="OnDamage"/> already left out a dead one): Flying falls, Falling changes nothing, Crashed burns.</summary>
    void PhoenixHit(ushort attacker)
    {
        switch (State)
        {
            case AnimalState.Flying: Fall(); break;
            case AnimalState.Crashed:
                Hp = 0;
                Killer = attacker;
                _fsm.Enter(AnimalState.Burning, _sky!.Rules.BurnSeconds);
                Died?.Invoke(this, attacker);
                break;
        }
    }

    void Fall()
    {
        var rules = _sky!.Rules;
        var (site, _) = PhoenixCrash.FindSite(_world, Position, Yaw, rules, _rng);
        _diveFrom = Position;
        _diveTo = site;
        _diveSeconds = rules.FallSeconds;
        Yaw = PhoenixFlight.YawOf(site.X - _diveFrom.X, site.Z - _diveFrom.Z);
        Pitch = PhoenixFlight.DivePitch(_diveFrom, _diveTo);
        _fsm.Enter(AnimalState.Falling, rules.FallSeconds);
        Fell?.Invoke(this, _diveFrom, _diveTo, rules.FallSeconds);
    }

    void Land()
    {
        Position = _diveTo;
        Pitch = 0;
        _fsm.Enter(AnimalState.Crashed);
        Landed?.Invoke(this);
    }

    void Burnt()
    {
        _fsm.Enter(AnimalState.Dead);
        Dropped?.Invoke(this, _sky!.Rules.Drops);
    }

    /// <summary>
    /// The dawn hour came (<see cref="PhoenixTick.Dawn"/>): a Phoenix still Flying flies off and the entity ends (<see cref="WorldDespawnReason.Expired"/>). One that has been hit stays (a Falling one lands, a Crashed one waits
    /// to be killed). True when it left.
    /// </summary>
    public bool FlyOff()
    {
        if (Ended != null || State != AnimalState.Flying) return false;
        Ended = WorldDespawnReason.Expired;
        return true;
    }
}
