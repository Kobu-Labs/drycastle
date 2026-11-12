using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What the helicopter helper needs from the server: spawning the helicopter, who it may shoot, the line of sight, and the
/// warning and burst it sends. The server implements it; the tests fake it.</summary>
public interface IHelperWorld
{
    /// <summary>Spawns the helicopter over the map (owned by nobody: the owner is not at its controls). Null when the world is full.</summary>
    ushort? SpawnHelicopter();

    /// <summary>Removes the helicopter. Does nothing when it is already gone.</summary>
    void Despawn(ushort entityId);

    /// <summary>Where the helicopter is now, or null once it has expired or been removed.</summary>
    Vector3? Helicopter(ushort entityId);

    /// <summary>Live enemies of the owner (never the owner).</summary>
    IReadOnlyList<HelperCandidate> Enemies(ushort owner);

    /// <summary>True when no world surface lies on the line between the two points.</summary>
    bool Clear(Vector3 from, Vector3 to);

    /// <summary>Tells the targeted player (only them) that the helicopter is about to fire on them.</summary>
    void Warn(ushort helicopter, ushort target, float seconds);

    /// <summary>One round of a burst: a tracer from the helicopter to <paramref name="end"/>, and the damage when it hits.</summary>
    void Round(ushort owner, ushort target, Vector3 from, Vector3 end, bool hit, float damage);
}

/// <summary>
/// The helicopter helper killstreak (killstreaks.json id "helicopter_helper", 15 kills). Using it spawns an AI helicopter that
/// circles the map for the streak's duration and shoots enemies of its owner (<see cref="HelicopterGunner"/>). The owner keeps
/// playing normally: no control session. The helicopter goes when the streak ends (duration, or the owner dies or leaves).
/// Kills by its rounds are the streak's kills (<see cref="WeaponId"/>).
/// </summary>
public sealed class HelicopterHelperStreak : IKillstreak
{
    /// <summary>killstreaks.json id.</summary>
    public const string StreakId = "helicopter_helper";
    /// <summary>The weapon id of the helicopter's rounds (its kills pay the streak award and do not advance the counter).</summary>
    public const string WeaponId = "helicopter_gun";

    static readonly IReadOnlySet<string> Weapons = new HashSet<string> { WeaponId };

    sealed class Run
    {
        public required ushort Owner { get; init; }
        public required ushort Entity { get; init; }
        public required HelicopterGunner Gunner { get; init; }
    }

    readonly HelicopterHelperTuning _tuning;
    readonly IHelperWorld _world;
    readonly Func<double> _roll;
    readonly List<Run> _runs = new();
    readonly List<HelperAction> _actions = new();

    /// <param name="tuning">The helper's tunables from killstreaks.json.</param>
    /// <param name="world">The server's view of the helicopter and its targets.</param>
    /// <param name="roll">Uniform random numbers in [0, 1). The server passes its own random source.</param>
    public HelicopterHelperStreak(HelicopterHelperTuning tuning, IHelperWorld world, Func<double> roll)
    {
        _tuning = tuning;
        _world = world;
        _roll = roll;
    }

    public string Id => StreakId;
    public IReadOnlySet<string> OwnedWeapons => Weapons;

    /// <summary>Helicopters currently flying for their owners.</summary>
    public int Flying => _runs.Count;

    public bool Activate(KillstreakUse use)
    {
        if (_world.SpawnHelicopter() is not { } entity)
        {
            use.Log("refused: no room for a helicopter");
            return false;
        }
        var run = new Run { Owner = use.Owner, Entity = entity, Gunner = new HelicopterGunner(_tuning, _roll) };
        _runs.Add(run);
        use.BindEntity(entity); // shot down: the use ends with its helicopter
        use.OnEnded(_ =>
        {
            _runs.Remove(run);
            _world.Despawn(entity);
        });
        return true;
    }

    /// <summary>Advances every helicopter by <paramref name="dt"/> seconds and carries out what its gunner decided.</summary>
    public void Tick(float dt)
    {
        foreach (var run in _runs.ToList())
        {
            if (_world.Helicopter(run.Entity) is not { } at) continue; // gone: its use ends with its duration
            _actions.Clear();
            run.Gunner.Tick(dt, at, _world.Enemies(run.Owner), (from, to) => _world.Clear(from, to), _actions);
            foreach (var action in _actions)
            {
                switch (action)
                {
                    case HelperWarnAction w:
                        _world.Warn(run.Entity, w.Target, w.Seconds);
                        break;
                    case HelperRoundAction r:
                        _world.Round(run.Owner, r.Target, r.From, r.End, r.Hit, r.Damage);
                        break;
                }
            }
        }
    }
}
