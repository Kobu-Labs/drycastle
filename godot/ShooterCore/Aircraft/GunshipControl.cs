using System.Numerics;
using Suchohrad.ShooterCore;

namespace Suchohrad.ShooterCore;

/// <summary>One round from a gunship turret, resolved by the server (hitscan for a cannon, the rocket sim for a rocket).</summary>
public readonly record struct GunshipShot(int WeaponIndex, GunshipWeaponKind Kind, Vector3 Origin, Vector3 Direction, float Damage, float RangeM);

/// <summary>
/// The gunship's point-of-view control (a <see cref="ControlSession"/> target): the owner's view drives the turret, Aim
/// steps the zoom, Reload steps the weapon, Fire fires it. Each weapon has its own ammo and heat. Server side; the rules are
/// pure so they run in the tests. Aim is clamped with <see cref="GunshipAim"/>; the server then resolves each shot.
/// </summary>
public sealed class GunshipControl
{
    /// <summary>Turret height below the airframe's position, metres: rounds leave from there.</summary>
    public const float TurretDropM = 1.5f;

    readonly GunshipDef _def;
    readonly Aircraft _aircraft;
    readonly Action<GunshipShot> _fire;
    readonly Action<GunshipStatus> _status;
    readonly Random _rng;
    /// <summary>Indices into <see cref="GunshipDef.Weapons"/> the owner may cycle through, in order.</summary>
    readonly int[] _weapons;
    int _slot;
    readonly int[] _ammo;
    readonly float[] _heat;
    readonly bool[] _locked;
    readonly double[] _nextShot;
    double _coolAt = double.NaN;
    bool _aimWas, _reloadWas;

    /// <param name="weapons">Weapon indices the owner may use, in cycle order; null means all of the turret's weapons.</param>
    public GunshipControl(GunshipDef def, Aircraft aircraft, Action<GunshipShot> fire, Action<GunshipStatus> status, Random? rng = null, IReadOnlyList<int>? weapons = null)
    {
        _def = def;
        _aircraft = aircraft;
        _fire = fire;
        _status = status;
        _rng = rng ?? new Random();
        _weapons = (weapons ?? Enumerable.Range(0, def.Weapons.Count).ToList()).ToArray();
        if (_weapons.Length == 0) throw new ArgumentException("a gunship control needs at least one weapon", nameof(weapons));
        var n = def.Weapons.Count;
        _ammo = def.Weapons.Select(w => w.Ammo).ToArray();
        _heat = new float[n];
        _locked = new bool[n];
        _nextShot = new double[n];
    }

    /// <summary>Index into <see cref="GunshipDef.ZoomLevels"/>.</summary>
    public int Zoom { get; private set; }
    /// <summary>Index into <see cref="GunshipDef.Weapons"/> of the weapon in use.</summary>
    public int Weapon => _weapons[_slot];
    /// <summary>The turret's aim after clamping, world yaw and pitch in radians.</summary>
    public float TurretYaw { get; private set; }
    public float TurretPitch { get; private set; }
    public float Magnification => _def.ZoomLevels[Zoom];
    public GunshipDef Def => _def;
    public Aircraft Aircraft => _aircraft;
    /// <summary>True once the airframe has ended (its streak is over): the control takes no more input.</summary>
    public bool Ended => _aircraft.Ended != null;

    public int Ammo(int weapon) => _ammo[weapon];
    public float HeatOf(int weapon) => _heat[weapon];
    public bool Overheated(int weapon) => _locked[weapon];

    /// <summary>Where a round leaves the turret, in world space (under the airframe).</summary>
    public Vector3 Muzzle => _aircraft.Position - Vector3.UnitY * TurretDropM;

    /// <summary>Applies one input from the owner. <paramref name="now"/> is the match clock in seconds.</summary>
    public void OnInput(NetInput input, double now)
    {
        if (Ended) return;
        var yaw = Quant.Yaw(input.Yaw);
        var pitch = Quant.Pitch(input.Pitch);
        (TurretYaw, TurretPitch) = GunshipAim.Clamp(_aircraft.YawRad, yaw, pitch, _def);
        Cool(now);

        var aim = Held(input, MoveButtons.Aim);
        var reload = Held(input, MoveButtons.Reload);
        var fire = Held(input, MoveButtons.Fire);
        var changed = false;
        if (aim && !_aimWas) { Zoom = (Zoom + 1) % _def.ZoomLevels.Count; changed = true; }
        if (reload && !_reloadWas) { _slot = (_slot + 1) % _weapons.Length; changed = true; }
        _aimWas = aim; _reloadWas = reload;

        if (fire) changed |= TryFire(now);
        if (changed) _status(Status());
    }

    /// <summary>Whether a round fires now for the current weapon: trigger held, not locked, interval passed, a round left.</summary>
    bool TryFire(double now)
    {
        var w = Weapon;
        var def = _def.Weapons[w];
        if (_locked[w] || _ammo[w] == 0 || now < _nextShot[w]) return false;
        _ammo[w]--;
        _nextShot[w] = now + def.IntervalS;
        _heat[w] = Math.Min(def.HeatMax, _heat[w] + def.HeatPerShot);
        if (_heat[w] >= def.HeatMax) _locked[w] = true;
        var dir = HitMath.SpreadDirection(TurretYaw, TurretPitch, def.SpreadDeg, _rng);
        _fire(new GunshipShot(w, def.Kind, Muzzle, dir, def.Damage, def.RangeM));
        return true;
    }

    /// <summary>Heat drains at each weapon's rate; a locked weapon unlocks at half its heat maximum.</summary>
    void Cool(double now)
    {
        if (double.IsNaN(_coolAt)) { _coolAt = now; return; }
        var dt = (float)Math.Max(0, now - _coolAt);
        _coolAt = now;
        if (dt <= 0) return;
        for (var i = 0; i < _heat.Length; i++)
        {
            var def = _def.Weapons[i];
            _heat[i] = Math.Max(0, _heat[i] - def.HeatCoolPerS * dt);
            if (_locked[i] && _heat[i] <= def.HeatMax * 0.5f) _locked[i] = false;
        }
    }

    /// <summary>The status the owner's HUD shows for the current weapon.</summary>
    public GunshipStatus Status()
    {
        var def = _def.Weapons[Weapon];
        var flags = (_locked[Weapon] ? GunshipStatus.FlagOverheated : 0) | (_ammo[Weapon] == 0 ? GunshipStatus.FlagEmpty : 0);
        return new GunshipStatus(
            (byte)Weapon,
            (ushort)Math.Clamp(_ammo[Weapon], 0, ushort.MaxValue),
            (byte)Math.Clamp((int)MathF.Round(100f * _heat[Weapon] / def.HeatMax), 0, 100),
            (byte)Zoom,
            (byte)flags);
    }

    /// <summary>Whether a button bit is down in this input.</summary>
    static bool Held(NetInput i, MoveButtons b) => (i.Buttons & (ushort)b) != 0;
}

/// <summary>What the gunship owner's HUD shows: weapon index, ammo, heat percent, zoom step and flags.</summary>
public readonly record struct GunshipStatus(byte Weapon, ushort Ammo, byte HeatPercent, byte Zoom, byte Flags)
{
    public const byte FlagOverheated = 1, FlagEmpty = 2;

    public bool Overheated => (Flags & FlagOverheated) != 0;
    public bool Empty => (Flags & FlagEmpty) != 0;
}

/// <summary>Payload of <see cref="EventKind.GunshipStatus"/> (owner only): u8 weapon, u16 ammo, u8 heat %, u8 zoom, u8 flags.</summary>
public static class GunshipCodec
{
    public const int Bytes = 6;

    public static byte[] Encode(GunshipStatus s)
    {
        var b = new byte[Bytes];
        b[0] = s.Weapon;
        b[1] = (byte)(s.Ammo & 0xFF);
        b[2] = (byte)(s.Ammo >> 8);
        b[3] = s.HeatPercent;
        b[4] = s.Zoom;
        b[5] = s.Flags;
        return b;
    }

    public static GunshipStatus? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != Bytes) return null;
        return new GunshipStatus(d[0], (ushort)(d[1] | (d[2] << 8)), d[3], d[4], d[5]);
    }
}
