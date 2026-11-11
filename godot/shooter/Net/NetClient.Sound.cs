using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Sound for the match client: events and snapshot state become sounds through <see cref="Sfx"/>. The own player's
/// weapon and movement sounds are non-positional; everything other players do is 3D at their position with the
/// per-event range from sounds.json.
/// </summary>
public partial class NetClient
{
    MatchPhase? _sfxPhase;
    int _sfxSlot;
    bool _sfxReloading, _sfxWasAir;
    double _sfxDryAt;
    float _sfxStepAcc, _sfxAirVy;

    static Vector3 G(NVec3 v) => new(v.X, v.Y, v.Z);

    string WeaponId(int index) => Weapons?.Weapons.ElementAtOrDefault(index)?.Id ?? "";

    Vector3? PlayerPos(ushort id) => id == _myId ? _me?.GlobalPosition : _remotes.TryGetValue(id, out var r) ? r.Node.GlobalPosition : null;

    void SfxShot(ShotMsg sh, bool melee)
    {
        if (!Sfx.Enabled) return;
        var id = SoundRules.Event("shot", WeaponId(sh.Weapon));
        if (sh.Shooter == _myId) Sfx.Play2D(id); else Sfx.Play3D(id, G(sh.Origin));
        if (!melee) Sfx.Play3D("impact", G(sh.End));
    }

    void SfxLaunch(LaunchMsg l)
    {
        if (!Sfx.Enabled) return;
        var id = SoundRules.Event("launch", WeaponId(l.Weapon));
        if (l.Owner == _myId) Sfx.Play2D(id); else Sfx.Play3D(id, G(l.Origin));
    }

    void SfxExplosion(ExplosionMsg ex)
    {
        if (Sfx.Enabled) Sfx.Play3D(SoundRules.Event("explosion", WeaponId(ex.Weapon)), G(ex.Position));
    }

    void SfxHit(HitMsg h)
    {
        if (!Sfx.Enabled) return;
        var mine = h.Attacker == _myId && h.Victim != _myId;
        if (mine) Sfx.Play2D(h.Kill ? "kill.confirm" : "hit.tick");
        if (Weapons?.Weapons.ElementAtOrDefault(h.Weapon) is { Kind: WeaponKind.Melee } w && PlayerPos(h.Victim) is { } at)
        {
            if (h.Attacker == _myId) Sfx.Play2D(SoundRules.Event("hit", w.Id)); else Sfx.Play3D(SoundRules.Event("hit", w.Id), at);
        }
    }

    void SfxDeath(DeathMsg d)
    {
        if (!Sfx.Enabled) return;
        if (d.Victim == _myId) Sfx.Play2D("death");
        else if (PlayerPos(d.Victim) is { } at) Sfx.Play3D("death", at);
    }

    /// <summary>Own reload and weapon switch, from the ammo event (remote players' reloads are not on the wire).</summary>
    void SfxAmmo(AmmoMsg a)
    {
        if (!Sfx.Enabled) return;
        if (_sfxSlot != 0 && a.Slot != _sfxSlot) Sfx.Play2D("switch");
        _sfxSlot = a.Slot;
        var reloading = a.ReloadRemaining > 0;
        if (reloading && !_sfxReloading) Sfx.Play2D(SoundRules.Event("reload", WeaponId(a.Slot - 1)));
        _sfxReloading = reloading;
    }

    /// <summary>Click when the trigger is held on an empty magazine (at most every 0.45 s).</summary>
    void SfxDry(MoveInput mi)
    {
        if (!Sfx.Enabled || !mi.Has(MoveButtons.Fire) || Dead || _driving) return;
        if (CurrentWeapon is { Kind: not WeaponKind.Melee } w && Ammo is { } a && a.ReloadRemaining <= 0 && a.Slots[a.Slot - 1].Mag == 0 && Now - _sfxDryAt > 0.45)
        {
            _sfxDryAt = Now;
            Sfx.Play2D(SoundRules.Event("dry", w.Id));
        }
    }

    void SfxPhase(MatchPhase p)
    {
        var prev = _sfxPhase;
        _sfxPhase = p;
        if (prev == null || prev == p || !Sfx.Enabled) return;
        if (p == MatchPhase.Running) Sfx.Play2D("round.start");
        else if (p == MatchPhase.Ending) Sfx.Play2D("round.end");
    }

    /// <summary>Own footsteps from horizontal speed and distance, and a thud on landing from a fall. One surface in v1 (ground kind is not cheap to query).</summary>
    void SfxOwnMovement(float dt)
    {
        if (!Sfx.Enabled || _me == null || _driving || Dead) return;
        var s = _me.State;
        var speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z);
        if (SoundRules.FootstepDue(ref _sfxStepAcc, speed, dt, s.OnFloor)) Sfx.Play2D("footstep");
        if (!s.OnFloor) { _sfxWasAir = true; _sfxAirVy = s.Velocity.Y; }
        else if (_sfxWasAir)
        {
            _sfxWasAir = false;
            if (_sfxAirVy < -4f) Sfx.Play2D("land");
        }
    }

    void SfxRemoteStep(Remote r, Vector3 now, float dt)
    {
        if (!Sfx.Enabled || r.State is EntityState.Dead or EntityState.InVehicle) return;
        var grounded = MathF.Abs(now.Y - r.LastPos.Y) < 0.04f * Math.Max(dt, 0.01f) * 30f;
        if (SoundRules.FootstepDue(ref r.StepAcc, r.Speed, dt, grounded)) Sfx.Play3D("footstep", now);
    }

    /// <summary>Rocket flight loop follows the drawn rocket; a car engine runs while a driver sits in it, pitched by speed, and is only kept while the car is within its range.</summary>
    void SfxRocket(Rocket r, Vector3 at)
    {
        if (!Sfx.Enabled) return;
        r.Snd ??= Sfx.Loop("rocket.flight", at);
        r.Snd?.Update(at);
    }

    void SfxCar(CarView v, Vector3 at)
    {
        if (!Sfx.Enabled) return;
        if (v.Driver == 0 || Sfx.ListenerDistance(at) > 80f) { v.Snd?.Release(); v.Snd = null; return; }
        v.Snd ??= Sfx.Loop("vehicle.engine", at);
        v.Snd?.Update(at, SoundRules.EnginePitch(v.Kmh));
    }

    void SfxTeardown()
    {
        foreach (var r in _rockets.Values) r.Snd?.Release();
        foreach (var c in _cars.Values) c.Snd?.Release();
    }
}
