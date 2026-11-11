using System.Numerics;
using Suchohrad.ShooterCore;

namespace Suchohrad.ShooterCore;

/// <summary>One round from a gunship turret, resolved by the server (hitscan for a cannon, the rocket sim for a rocket).</summary>
public readonly record struct GunshipShot(int WeaponIndex, GunshipWeaponKind Kind, Vector3 Origin, Vector3 Direction, float Damage, float RangeM);

/// <summary>
/// The gunship's point-of-view control (a <see cref="ControlSession"/> target): the owner's view drives the turret, Aim
/// steps the zoom, Reload steps the weapon, Fire fires it. There is no ammo and no heat: the fire interval paces each weapon,
/// and the streak's time limit is the only limit. An interval shorter than the input period fires several rounds in one
/// input (up to <see cref="MaxRoundsPerInput"/>), so the effective rate is the interval's. Server side; the rules are pure so
/// they run in the tests. The aim is a ground-stabilised world point (<see cref="GunshipAim"/>, #160) that the owner's client
/// sends with each input (#161); the server fires along the view from its own muzzle to that point, so the rounds land on the reticle.
/// </summary>
public sealed class GunshipControl
{
    /// <summary>Turret height below the airframe's position, metres: rounds leave from there.</summary>
    public const float TurretDropM = 1.5f;
    /// <summary>The most rounds one input may fire for one weapon: a backlog (a stall of the input stream) does not turn into a flood.</summary>
    public const int MaxRoundsPerInput = 8;

    readonly GunshipDef _def;
    readonly Aircraft _aircraft;
    readonly Action<GunshipShot> _fire;
    readonly Action<GunshipStatus> _status;
    readonly Random _rng;
    /// <summary>Indices into <see cref="GunshipDef.Weapons"/> the owner may cycle through, in order.</summary>
    readonly int[] _weapons;
    int _slot;
    readonly double[] _nextShot;
    bool _aimWas, _reloadWas;
    /// <summary>False until the owner has sent an aim: a turret with no aim has nothing to fire at, so it does not fire.</summary>
    bool _haveAim;
    /// <summary>The hotbar key last seen (1 = the first weapon); only a change of it selects a weapon, so Reload still cycles.</summary>
    byte _key;

    /// <param name="weapons">Weapon indices the owner may use, in cycle order; null means all of the turret's weapons.</param>
    public GunshipControl(GunshipDef def, Aircraft aircraft, Action<GunshipShot> fire, Action<GunshipStatus> status, Random? rng = null, IReadOnlyList<int>? weapons = null)
    {
        _def = def;
        _aircraft = aircraft;
        _fire = fire;
        _status = status;
        _rng = rng ?? new Random();
        _maxDistance = GunshipAim.MaxDistance(def);
        _orbitReach = GunshipAim.OrbitReach(aircraft.Def);
        _weapons = (weapons ?? Enumerable.Range(0, def.Weapons.Count).ToList()).ToArray();
        if (_weapons.Length == 0) throw new ArgumentException("a gunship control needs at least one weapon", nameof(weapons));
        _nextShot = new double[def.Weapons.Count];
        Array.Fill(_nextShot, double.NegativeInfinity); // every weapon starts idle: the first press fires at once, with no backlog
    }

    /// <summary>Index into <see cref="GunshipDef.ZoomLevels"/>.</summary>
    public int Zoom { get; private set; }
    /// <summary>Index into <see cref="GunshipDef.Weapons"/> of the weapon in use.</summary>
    public int Weapon => _weapons[_slot];
    /// <summary>The turret's view, world yaw and pitch in radians: the direction from the muzzle to <see cref="AimPoint"/>.</summary>
    public float TurretYaw { get; private set; }
    public float TurretPitch { get; private set; }
    /// <summary>The world point the turret is on (ground-stabilised, #160): the owner's aim, bounded by <see cref="GunshipAim.ClampAim"/>.</summary>
    public Vector3 AimPoint { get; private set; }
    readonly float _maxDistance;
    /// <summary>How far the muzzle can be from the orbit's centre (the aim's bound, see <see cref="GunshipAim.ClampAim"/>).</summary>
    readonly float _orbitReach;
    public float Magnification => _def.ZoomLevels[Zoom];
    public GunshipDef Def => _def;
    public Aircraft Aircraft => _aircraft;
    /// <summary>True once the airframe has ended (its streak is over): the control takes no more input.</summary>
    public bool Ended => _aircraft.Ended != null;

    /// <summary>Where a round leaves the turret, in world space (under the airframe).</summary>
    public Vector3 Muzzle => _aircraft.Position - Vector3.UnitY * TurretDropM;

    /// <summary>
    /// Applies one input from the owner. <paramref name="now"/> is the match clock in seconds. An input with an aim (#161) moves
    /// <see cref="AimPoint"/> to it (bounded); one without keeps the last. The view is the direction from this muzzle to the aim
    /// point, so the rounds leave along it. The input's look angles are not used: re-intersecting them from this later muzzle is the bug.
    /// </summary>
    public void OnInput(NetInput input, double now)
    {
        if (Ended) return;
        if (input.HasAim)
        {
            AimPoint = GunshipAim.ClampAim(_aircraft.Centre, _orbitReach, input.Aim, _aircraft.GroundY, _maxDistance);
            _haveAim = true;
        }
        if (_haveAim) (TurretYaw, TurretPitch) = GunshipAim.View(Muzzle, AimPoint, _def);

        var aim = Held(input, MoveButtons.Aim);
        var reload = Held(input, MoveButtons.Reload);
        var fire = Held(input, MoveButtons.Fire);
        var changed = false;
        if (aim && !_aimWas) { Zoom = (Zoom + 1) % _def.ZoomLevels.Count; changed = true; }
        if (reload && !_reloadWas) { _slot = (_slot + 1) % _weapons.Length; changed = true; }
        _aimWas = aim; _reloadWas = reload;
        // the hotbar key picks the weapon (key 1 is the turret's first weapon, the AC-130's 1-3); a key outside the subset is ignored
        if (input.Slot != _key)
        {
            _key = input.Slot;
            var pick = input.Slot >= 1 ? Array.IndexOf(_weapons, input.Slot - 1) : -1;
            if (pick >= 0 && pick != _slot) { _slot = pick; changed = true; }
        }

        if (fire && _haveAim) FireDue(now);
        if (changed) _status(Status());
    }

    /// <summary>
    /// Fires every round that is due for the current weapon: the first at once if the weapon was idle, then one per interval
    /// that has passed since the last, at most <see cref="MaxRoundsPerInput"/>.
    /// </summary>
    void FireDue(double now)
    {
        var w = Weapon;
        var def = _def.Weapons[w];
        if (now - _nextShot[w] > MaxRoundsPerInput * def.IntervalS) _nextShot[w] = now; // a long stall or idle: no backlog from before
        for (var n = 0; n < MaxRoundsPerInput && now >= _nextShot[w]; n++)
        {
            _nextShot[w] += def.IntervalS;
            var dir = HitMath.SpreadDirection(TurretYaw, TurretPitch, def.SpreadDeg, _rng);
            _fire(new GunshipShot(w, def.Kind, Muzzle, dir, def.Damage, def.RangeM));
        }
    }

    /// <summary>The status the owner's HUD shows: the weapon in use and the zoom step.</summary>
    public GunshipStatus Status() => new((byte)Weapon, (byte)Zoom);

    /// <summary>Whether a button bit is down in this input.</summary>
    static bool Held(NetInput i, MoveButtons b) => (i.Buttons & (ushort)b) != 0;
}

/// <summary>What the gunship owner's HUD shows: the weapon index and the zoom step.</summary>
public readonly record struct GunshipStatus(byte Weapon, byte Zoom);

/// <summary>Payload of <see cref="EventKind.GunshipStatus"/> (owner only): u8 weapon, u8 zoom.</summary>
public static class GunshipCodec
{
    public const int Bytes = 2;

    public static byte[] Encode(GunshipStatus s) => new[] { s.Weapon, s.Zoom };

    public static GunshipStatus? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != Bytes) return null;
        return new GunshipStatus(d[0], d[1]);
    }
}
