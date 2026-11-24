using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Sound for the match client: events and snapshot state become sounds through <see cref="Sfx"/>. The own player's weapon and movement sounds are non-positional; everything other
/// players do is 3D at their position with the per-event range from sounds.json. (The vehicles' own are <see cref="CarSounds"/>.)
/// </summary>
internal sealed class ClientSound
{
    readonly ClientContext _ctx;
    int _slot;
    bool _reloading, _wasAir;
    float _airVy;

    public ClientSound(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The footstep accumulator of the own body: the viewmodel's bob dips land on the sound.</summary>
    public float StepAcc => _stepAcc;
    float _stepAcc;

    /// <summary>The id of the weapon at <paramref name="index"/> in weapons.json; empty when there is none.</summary>
    public string WeaponId(int index) => _ctx.Weapons?.Weapons.ElementAtOrDefault(index)?.Id ?? "";

    public void Shot(ShotMsg sh, bool melee)
    {
        if (!Sfx.Enabled) return;
        var id = SoundRules.Event("shot", WeaponId(sh.Weapon));
        if (sh.Shooter == _ctx.MyId) Sfx.Play2D(id); else Sfx.Play3D(id, sh.Origin.ToGodot());
        if (!melee) Sfx.Play3D("impact", sh.End.ToGodot());
    }

    public void Launch(LaunchMsg l)
    {
        if (!Sfx.Enabled) return;
        var id = SoundRules.Event("launch", WeaponId(l.Weapon));
        if (l.Owner == _ctx.MyId) Sfx.Play2D(id); else Sfx.Play3D(id, l.Origin.ToGodot());
    }

    public void Hit(HitMsg h)
    {
        if (!Sfx.Enabled) return;
        var mine = h.Attacker == _ctx.MyId && h.Victim != _ctx.MyId;
        if (mine) Sfx.Play2D(h.Kill ? "kill.confirm" : "hit.tick");
        if (_ctx.Weapons?.Weapons.ElementAtOrDefault(h.Weapon) is { Kind: WeaponKind.Melee } w && _ctx.PositionOf(h.Victim) is { } at)
        {
            if (h.Attacker == _ctx.MyId) Sfx.Play2D(SoundRules.Event("hit", w.Id)); else Sfx.Play3D(SoundRules.Event("hit", w.Id), at);
        }
    }

    public void Death(DeathMsg d)
    {
        if (!Sfx.Enabled) return;
        if (d.Victim == _ctx.MyId) Sfx.Play2D("death");
        else if (_ctx.PositionOf(d.Victim) is { } at) Sfx.Play3D("death", at);
    }

    /// <summary>Own reload and weapon switch, from the ammo event (remote players' reloads are not on the wire). A reload the prediction already sounded (till <paramref name="predictedReloadUntil"/>) is not sounded again.</summary>
    public void Ammo(AmmoMsg a, double predictedReloadUntil)
    {
        if (!Sfx.Enabled) return;
        if (_slot != 0 && a.Slot != _slot) Sfx.Play2D("switch");
        _slot = a.Slot;
        var reloading = a.ReloadRemaining > 0;
        if (reloading && !_reloading && _ctx.Now > predictedReloadUntil) Sfx.Play2D(SoundRules.Event("reload", WeaponId(a.Slot - 1)));
        _reloading = reloading;
    }

    /// <summary>Own footsteps from horizontal speed and distance, and a thud on landing from a fall. One surface in v1 (ground kind is not cheap to query).</summary>
    public void OwnMovement(float dt)
    {
        if (!Sfx.Enabled || _ctx.Me == null || _ctx.Driving || _ctx.Dead) return;
        var s = _ctx.Me.State;
        var speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z);
        if (SoundRules.FootstepDue(ref _stepAcc, speed, dt, s.OnFloor)) Sfx.Play2D("footstep");
        if (!s.OnFloor) { _wasAir = true; _airVy = s.Velocity.Y; }
        else if (_wasAir)
        {
            _wasAir = false;
            if (_airVy < -4f) Sfx.Play2D("land");
        }
    }

    public void RemoteStep(RemotePlayer r, Vector3 now, float dt)
    {
        if (!Sfx.Enabled || r.State is EntityState.Dead or EntityState.InVehicle) return;
        var grounded = MathF.Abs(now.Y - r.LastPos.Y) < 0.04f * Math.Max(dt, 0.01f) * 30f;
        if (SoundRules.FootstepDue(ref r.StepAcc, r.Speed, dt, grounded)) Sfx.Play3D("footstep", now);
    }
}
