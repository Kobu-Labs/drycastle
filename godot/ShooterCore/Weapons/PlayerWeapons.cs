namespace Suchohrad.ShooterCore;

/// <summary>Weapon related input of one tick. <see cref="Slot"/> is the wanted hotbar slot 1..4 (0 = no preference).</summary>
public readonly record struct WeaponInput(bool Fire, bool Reload, int Slot, bool Next);

/// <summary>A trigger pull that fired (hitscan, projectile) or swung (melee) this tick.</summary>
public readonly record struct FireOutcome(WeaponDef Weapon, int WeaponIndex, float SpreadDegrees);

/// <summary>
/// Weapon state of one player: hotbar, ammo per weapon, reload, switch and fire timers, spread. Godot-free and
/// deterministic: time is passed in (seconds), the host calls <see cref="Update"/> once per processed input. A projectile
/// pull (bazooka) consumes a round and returns the outcome; the host spawns the rocket.
/// <para>
/// Slots are 1-based indexes into weapons.json (the wire uses them). The hotbar is the player's loadout (primary,
/// secondary, knife) when one is given; without one it is every weapon in file order. Only hotbar weapons can be
/// selected, and "next" cycles through the hotbar.
/// </para>
/// </summary>
public sealed class PlayerWeapons
{
    /// <summary>Timers compare with this slack so 3 ticks of 1/30 s reliably equal a 0.1 s interval.</summary>
    const double Eps = 1e-4;

    /// <summary>Base time to draw a secondary (off-hand) weapon; Swift Swap shortens it. Primary switches stay instant.</summary>
    public const double SecondarySwapSeconds = 0.35;

    readonly WeaponsFile _file;
    PerkModifiers _perks;

    /// <summary>Replaces the perk values (a care package perk until death). Takes effect at the next weapon step.</summary>
    public void SetPerks(PerkModifiers perks) => _perks = perks;
    readonly int[] _hotbar; // weapons.json indexes, in hotbar order
    readonly int _startSlot; // the slot equipped at spawn
    readonly int[] _mag, _reserve;
    double _nextFire, _reloadDone, _swapReady, _lastNow;
    bool _prevFire;
    float _spreadExtra;

    /// <param name="perks">The player's perk values (run, reload and swap speed are read here); null = no perk.</param>
    /// <param name="hotbar">weapons.json indexes of the hotbar, in order (see <see cref="LoadoutHotbar"/>); null = every weapon.</param>
    public PlayerWeapons(WeaponsFile file, PerkModifiers? perks = null, int[]? hotbar = null)
    {
        _file = file;
        _perks = perks ?? PerkModifiers.None;
        _hotbar = hotbar is { Length: > 0 } h ? h.ToArray() : Enumerable.Range(0, file.Weapons.Count).ToArray();
        _startSlot = hotbar is { Length: > 0 } ? _hotbar[0] + 1 : Math.Max(1, file.Weapons.FindIndex(w => w.Id == file.StartingWeapon) + 1);
        _mag = new int[file.Weapons.Count];
        _reserve = new int[file.Weapons.Count];
        Reset(0);
    }

    /// <summary>Number of hotbar positions (primary, secondary, knife).</summary>
    public int HotbarCount => _hotbar.Length;
    /// <summary>The weapons.json index on a 0-based hotbar position.</summary>
    public int HotbarWeapon(int pos) => _hotbar[pos];
    public bool HotbarHolds(int weapon) => Array.IndexOf(_hotbar, weapon) >= 0;
    /// <summary>Magazine plus reserve of a weapons.json index.</summary>
    public int RoundsOf(int weapon) => _mag[weapon] + _reserve[weapon];

    /// <summary>
    /// Puts <paramref name="weapon"/> on a 0-based hotbar position with a fresh magazine and half its reserve (corpse pickup).
    /// Returns the weapon it replaced and that weapon's rounds (magazine plus reserve). A replaced selected weapon hands the
    /// selection to the new one and cancels a reload.
    /// </summary>
    public void ReplaceHotbar(int pos, int weapon, out int replaced, out int rounds)
    {
        replaced = _hotbar[pos];
        rounds = RoundsOf(replaced);
        _hotbar[pos] = weapon;
        _mag[weapon] = _file.Weapons[weapon].MagazineSize;
        _reserve[weapon] = _file.Weapons[weapon].ReserveAmmo / 2;
        if (Slot == replaced + 1) { Slot = weapon + 1; Reloading = false; _prevFire = false; }
        Changed = true;
    }

    /// <summary>Number of weapons in weapons.json (the ammo list on the wire has one entry per weapon).</summary>
    public int WeaponCount => _file.Weapons.Count;
    /// <summary>The hotbar weapons in hotbar order (the key order: 1, 2, 3 ...).</summary>
    public IReadOnlyList<WeaponDef> Hotbar => _hotbar.Select(i => _file.Weapons[i]).ToList();
    /// <summary>Whether a 1-based weapon slot is on the hotbar.</summary>
    public bool OnHotbar(int slot) => Array.IndexOf(_hotbar, slot - 1) >= 0;
    /// <summary>Selected hotbar slot, 1-based.</summary>
    public int Slot { get; private set; }
    public WeaponDef Current => _file.Weapons[Slot - 1];
    public bool Reloading { get; private set; }
    /// <summary>Sandbox mode (#156): shots do not use up the magazine and a reload is never needed.</summary>
    public bool Unlimited { get; set; }
    /// <summary>Set whenever ammo, slot or reload state changed; the host clears it after sending the ammo state.</summary>
    public bool Changed { get; set; }
    /// <summary>Current spread half-angle in degrees (what the next shot would use).</summary>
    public float Spread => SpreadOf(Current);

    public int Mag(int slot) => _mag[slot - 1];
    public int Reserve(int slot) => _reserve[slot - 1];
    public float ReloadRemaining(double now) => Reloading ? (float)Math.Max(0, _reloadDone - now) : 0f;

    /// <summary>Speed multiplier for the weapon in <paramref name="wantedSlot"/> (0 or invalid: the current one), times the perk's run speed.</summary>
    public float SpeedFor(int wantedSlot) =>
        (OnHotbar(wantedSlot) ? _file.Weapons[wantedSlot - 1] : Current).MoveSpeedMultiplier * _perks.RunSpeed;

    /// <summary>Spawn state: full magazines and reserves, starting weapon, no timers.</summary>
    public void Reset(double now)
    {
        for (var i = 0; i < _mag.Length; i++) { _mag[i] = _file.Weapons[i].MagazineSize; _reserve[i] = _file.Weapons[i].ReserveAmmo; }
        Slot = _startSlot;
        Reloading = false; _spreadExtra = 0; _prevFire = false;
        _nextFire = _reloadDone = _swapReady = now; _lastNow = now;
        Changed = true;
    }

    /// <summary>Sandbox (#156): every magazine and reserve back to full. Timers and the selected weapon are kept.</summary>
    public void Refill()
    {
        for (var i = 0; i < _mag.Length; i++) { _mag[i] = _file.Weapons[i].MagazineSize; _reserve[i] = _file.Weapons[i].ReserveAmmo; }
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

        if (Reloading && now + Eps >= _reloadDone) { if (Current.ShellReload) LoadShell(now); else FinishReload(); }

        // switching: wanted slot or the next-weapon edge; cancels a reload
        var want = Slot;
        if (OnHotbar(input.Slot)) want = input.Slot;
        if (input.Next && !_prevNext) want = NextOnHotbar(Slot);
        _prevNext = input.Next;
        if (want != Slot)
        {
            Slot = want; Reloading = false; _prevFire = input.Fire; Changed = true;
            // drawing the off-hand weapon takes time (Swift Swap shortens it); no fire until it is out
            _swapReady = Current.Slot == WeaponSlot.Secondary ? now + SecondarySwapSeconds / _perks.WeaponSwapSpeed : now;
        }
        var w = Current;

        // spread recovers once the fire cooldown is over (so a held full-auto weapon keeps growing)
        if (recovering > Eps && _spreadExtra > 0 && w.SpreadRecovery > 0)
            _spreadExtra = Math.Max(0, _spreadExtra - recovering * Math.Max(w.SpreadMax - w.SpreadBase, 0.0001f) / w.SpreadRecovery);

        if (input.Reload && CanReload(w)) StartReload(now, w);

        var pull = w.FullAuto ? input.Fire : input.Fire && !_prevFire;
        _prevFire = input.Fire;
        // a shell reload is interrupted by a shot the moment it can fire (a shell is loaded)
        if (pull && Reloading && w.ShellReload && _mag[Slot - 1] > 0 && now + Eps >= _nextFire) Reloading = false;
        if (!pull || Reloading || now + Eps < _nextFire || now + Eps < _swapReady) return null;

        switch (w.Kind)
        {
            case WeaponKind.Melee:
                _nextFire = now + w.FireInterval;
                return new FireOutcome(w, Slot - 1, 0);
            case WeaponKind.Hitscan:
                if (_mag[Slot - 1] <= 0 && !Unlimited) { if (CanReload(w)) StartReload(now, w); return null; }
                var spread = SpreadOf(w);
                if (!Unlimited) _mag[Slot - 1]--;
                _nextFire = now + w.FireInterval;
                _spreadExtra = Math.Min(_spreadExtra + w.SpreadPerShot, Math.Max(0, w.SpreadMax - w.SpreadBase));
                Changed = true;
                return new FireOutcome(w, Slot - 1, spread);
            default: // projectile: one rocket per magazine, the reload is started by the next pull or the reload key
                if (_mag[Slot - 1] <= 0 && !Unlimited) { if (CanReload(w)) StartReload(now, w); return null; }
                if (!Unlimited) _mag[Slot - 1]--;
                _nextFire = now + w.FireInterval;
                Changed = true;
                return new FireOutcome(w, Slot - 1, 0);
        }
    }

    bool _prevNext;

    /// <summary>The hotbar slot after <paramref name="slot"/> (wraps around); a slot that is not on the hotbar goes to the first entry.</summary>
    int NextOnHotbar(int slot)
    {
        var i = Array.IndexOf(_hotbar, slot - 1);
        return _hotbar[(i + 1) % _hotbar.Length] + 1;
    }

    bool CanReload(WeaponDef w) => !Unlimited && !Reloading && w.MagazineSize > 0 && w.ReloadTime > 0 && _mag[Slot - 1] < w.MagazineSize && _reserve[Slot - 1] > 0;

    void StartReload(double now, WeaponDef w) { Reloading = true; _reloadDone = now + w.ReloadTime / _perks.ReloadSpeed; Changed = true; }

    /// <summary>Shell-by-shell reload: one shell per reload_time; the reload ends when the magazine is full or the reserve is empty.</summary>
    void LoadShell(double now)
    {
        var i = Slot - 1;
        _mag[i]++; _reserve[i]--;
        Changed = true;
        if (_mag[i] < Current.MagazineSize && _reserve[i] > 0) _reloadDone = now + Current.ReloadTime / _perks.ReloadSpeed;
        else Reloading = false;
    }

    void FinishReload()
    {
        var i = Slot - 1;
        var take = Math.Min(Current.MagazineSize - _mag[i], _reserve[i]);
        _mag[i] += take; _reserve[i] -= take;
        Reloading = false; Changed = true;
    }
}
