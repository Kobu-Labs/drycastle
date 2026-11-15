using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// A herd of wild animals of one species (GLOSSARY "Herd"): its members wander about the herd's <see cref="Anchor"/> (within <see cref="Spread"/>), the anchor
/// drifts to a new spot near the herd's home now and then (so the herd grazes its way across the habitat), and when one member takes fright
/// (<see cref="Alarm"/>) the whole herd runs from the same spot. The anchor moves only while no member is running. Godot-free and deterministic for a seed;
/// the keeper calls <see cref="Update"/> each tick.
/// </summary>
public sealed class Herd
{
    readonly List<Animal> _members = new();
    readonly Random _rng;
    readonly float _moveMin, _moveMax, _range;
    Countdown _move;

    /// <param name="home">Where the herd formed: the centre of its drifting.</param>
    /// <param name="spread">How far members wander from the anchor, metres.</param>
    /// <param name="range">How far the anchor may drift from home, metres.</param>
    /// <param name="moveMinSeconds">The anchor moves every this many to <paramref name="moveMaxSeconds"/> seconds (while calm).</param>
    public Herd(Vector3 home, float spread, float range, float moveMinSeconds, float moveMaxSeconds, int seed)
    {
        Home = Anchor = home;
        Spread = spread;
        _range = range;
        _moveMin = moveMinSeconds; _moveMax = moveMaxSeconds;
        _rng = new Random(seed);
        _move.Start(Next());
    }

    public Vector3 Home { get; }
    /// <summary>Where the herd is now: the centre its members wander about.</summary>
    public Vector3 Anchor { get; private set; }
    public float Spread { get; }
    public IReadOnlyList<Animal> Members => _members;
    public int AliveCount { get { var n = 0; foreach (var m in _members) if (m.Alive) n++; return n; } }

    public void Add(Animal member)
    {
        _members.Add(member);
        member.Herd = this;
    }

    float Next() => _moveMin + (float)_rng.NextDouble() * (_moveMax - _moveMin);

    /// <summary>One member took fright at <paramref name="from"/>: every other living member runs from it too.</summary>
    public void Alarm(Animal source, Vector3 from)
    {
        foreach (var m in _members)
            if (!ReferenceEquals(m, source)) m.Startle(from);
    }

    /// <summary>Counts the drift clock; when it runs out and nobody is running, the anchor moves to a spot with ground within the range of home.</summary>
    public void Update(float dt, IAnimalWorld world)
    {
        if (!_move.Tick(dt)) return;
        if (_members.Exists(m => m.Alive && m.State == AnimalState.Flee)) { _move.Start(2); return; } // not while it runs: try again soon
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var angle = (float)_rng.NextDouble() * MathF.Tau;
            var reach = (float)Math.Sqrt(_rng.NextDouble()) * _range;
            var x = Home.X + MathF.Cos(angle) * reach;
            var z = Home.Z + MathF.Sin(angle) * reach;
            if (world.GroundY(x, z) is not { } y) continue;
            Anchor = new Vector3(x, y, z);
            break;
        }
        _move.Start(Next());
    }
}
