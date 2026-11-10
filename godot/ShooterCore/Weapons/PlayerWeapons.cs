namespace Suchohrad.ShooterCore;

/// <summary>Weapon related input of one tick. <see cref="Slot"/> is the wanted hotbar slot 1..4 (0 = no preference).</summary>
public readonly record struct WeaponInput(bool Fire, bool Reload, int Slot, bool Next);

/// <summary>A trigger pull that fired (hitscan) or swung (melee) this tick.</summary>
public readonly record struct FireOutcome(WeaponDef Weapon, int WeaponIndex, float SpreadDegrees);

/// <summary>
/// Weapon state of one player: hotbar (slots 1-4 = the order of weapons.json), ammo per weapon, reload, switch and
/// fire timers, spread. Godot-free and deterministic: time is passed in (seconds), the host calls <see cref="Update"/>
/// once per processed input. The projectile weapon (bazooka) is present but its fire does nothing yet.
/// </summary>
public sealed class PlayerWeapons
{
    /// <summary>Timers compare with this slack so 3 ticks of 1/30 s reliably equal a 0.1 s interval.</summary>
    const double Eps = 1e-4;

    readonly WeaponsFile _file;
    readonly int[] _mag, _reserve;
    double _nextFire, _switchReady, _reloadDone, _lastNow;
    bool _prevFire;
    float _spreadExtra;

    public PlayerWeapons(WeaponsFile file)
    {
        _file = file;
        _mag = new int[file.Weapons.Count];
        _reserve = new int[file.Weapons.Count];
        Reset(0);
    }

    public IReadOnlyList<WeaponDef> Hotbar => _file.Weapons;
    /// <summary>Selected hotbar slot, 1-based.</summary>
    public int Slot { get; private set; }
    public WeaponDef Current => _file.Weapons[Slot - 1];
    public bool Reloading { get; private set; }
    /// <summary>Set whenever ammo, slot or reload state changed; the host clears it after sending the ammo state.</summary>
    public bool Changed { get; set; }
    /// <summary>Current spread half-angle in degrees (what the next shot would use).</summary>
    public float Spread => SpreadOf(Current);

    public int Mag(int slot) => _mag[slot - 1];
    public int Reserve(int slot) => _reserve[slot - 1];
    public float ReloadRemaining(double now) => Reloading ? (float)Math.Max(0, _reloadDone - now) : 0f;

    /// <summary>Speed multiplier for the weapon in <paramref name="wantedSlot"/> (0 or invalid: the current one).</summary>
    public float SpeedFor(int wantedSlot) =>
        (wantedSlot >= 1 && wantedSlot <= _file.Weapons.Count ? _file.Weapons[wantedSlot - 1] : Current).MoveSpeedMultiplier;

    /// <summary>Spawn state: full magazines and reserves, starting weapon, no timers.</summary>
    public void Reset(double now)
    {
        for (var i = 0; i < _mag.Length; i++) { _mag[i] = _file.Weapons[i].MagazineSize; _reserve[i] = _file.Weapons[i].ReserveAmmo; }
        Slot = Math.Max(1, _file.Weapons.FindIndex(w => w.Id == _file.StartingWeapon) + 1);
        Reloading = false; _spreadExtra = 0; _prevFire = false;
        _nextFire = _switchReady = _reloadDone = now; _lastNow = now;
        Changed = true;
    }

    /// <summary>A kill refills the reserves of every weapon (magazines stay as they are).</summary>
    public void RefillReserves()
    {
        for (var i = 0; i < _reserve.Length; i++) _reserve[i] = _file.Weapons[i].ReserveAmmo;
        Changed = true;
    }

    float SpreadOf(WeaponDef w) => Math.Min(w.SpreadBase + _spreadExtra, Math.Max(w.SpreadBase, w.SpreadMax));

    public FireOutcome? Update(double now, in WeaponInput input)
    {
        var recovering = (float)Math.Max(0, now - Math.Max(_nextFire, _lastNow));
        _lastNow = now;

        if (Reloading && now + Eps >= _reloadDone) FinishReload();

        // switching: wanted slot or the next-weapon edge; cancels a reload
        var want = Slot;
        if (input.Slot >= 1 && input.Slot <= _file.Weapons.Count) want = input.Slot;
        if (input.Next && !_prevNext) want = Slot % _file.Weapons.Count + 1;
        _prevNext = input.Next;
        if (want != Slot)
        {
            Slot = want; Reloading = false; _switchReady = now + Current.SwitchTime; _prevFire = input.Fire; Changed = true;
        }
        var w = Current;

        // spread recovers once the fire cooldown is over (so a held full-auto weapon keeps growing)
        if (recovering > Eps && _spreadExtra > 0 && w.SpreadRecovery > 0)
            _spreadExtra = Math.Max(0, _spreadExtra - recovering * Math.Max(w.SpreadMax - w.SpreadBase, 0.0001f) / w.SpreadRecovery);

        if (input.Reload && CanReload(w)) StartReload(now, w);

        var pull = w.FullAuto ? input.Fire : input.Fire && !_prevFire;
        _prevFire = input.Fire;
        if (!pull || Reloading || now + Eps < _switchReady || now + Eps < _nextFire) return null;

        switch (w.Kind)
        {
            case WeaponKind.Melee:
                _nextFire = now + w.FireInterval;
                return new FireOutcome(w, Slot - 1, 0);
            case WeaponKind.Hitscan:
                if (_mag[Slot - 1] <= 0) { if (CanReload(w)) StartReload(now, w); return null; }
                var spread = SpreadOf(w);
                _mag[Slot - 1]--;
                _nextFire = now + w.FireInterval;
                _spreadExtra = Math.Min(_spreadExtra + w.SpreadPerShot, Math.Max(0, w.SpreadMax - w.SpreadBase));
                Changed = true;
                return new FireOutcome(w, Slot - 1, spread);
            default:
                return null; // projectile weapons: another issue
        }
    }

    bool _prevNext;

    bool CanReload(WeaponDef w) => !Reloading && w.MagazineSize > 0 && w.ReloadTime > 0 && _mag[Slot - 1] < w.MagazineSize && _reserve[Slot - 1] > 0;

    void StartReload(double now, WeaponDef w) { Reloading = true; _reloadDone = now + w.ReloadTime; Changed = true; }

    void FinishReload()
    {
        var i = Slot - 1;
        var take = Math.Min(Current.MagazineSize - _mag[i], _reserve[i]);
        _mag[i] += take; _reserve[i] -= take;
        Reloading = false; Changed = true;
    }
}
