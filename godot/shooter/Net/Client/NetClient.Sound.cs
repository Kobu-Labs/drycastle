using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Sound for the match client: events and snapshot state become sounds through <see cref="Sfx"/>. The own player's
/// weapon and movement sounds are non-positional; everything other players do is 3D at their position with the
/// per-event range from sounds.json.
/// </summary>
public partial class NetClient
{
    int _sfxSlot;
    bool _sfxReloading, _sfxWasAir;
    double _sfxDryAt;
    float _sfxStepAcc, _sfxAirVy;

    static Vector3 G(NVec3 v) => new(v.X, v.Y, v.Z);

    string WeaponId(int index) => Weapons?.Weapons.ElementAtOrDefault(index)?.Id ?? "";


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

    void SfxHit(HitMsg h)
    {
        if (!Sfx.Enabled) return;
        var mine = h.Attacker == _myId && h.Victim != _myId;
        if (mine) Sfx.Play2D(h.Kill ? "kill.confirm" : "hit.tick");
        if (Weapons?.Weapons.ElementAtOrDefault(h.Weapon) is { Kind: WeaponKind.Melee } w && Ctx.PositionOf(h.Victim) is { } at)
        {
            if (h.Attacker == _myId) Sfx.Play2D(SoundRules.Event("hit", w.Id)); else Sfx.Play3D(SoundRules.Event("hit", w.Id), at);
        }
    }

    void SfxDeath(DeathMsg d)
    {
        if (!Sfx.Enabled) return;
        if (d.Victim == _myId) Sfx.Play2D("death");
        else if (Ctx.PositionOf(d.Victim) is { } at) Sfx.Play3D("death", at);
    }

    /// <summary>Own reload and weapon switch, from the ammo event (remote players' reloads are not on the wire).</summary>
    void SfxAmmo(AmmoMsg a)
    {
        if (!Sfx.Enabled) return;
        if (_sfxSlot != 0 && a.Slot != _sfxSlot) Sfx.Play2D("switch");
        _sfxSlot = a.Slot;
        var reloading = a.ReloadRemaining > 0;
        if (reloading && !_sfxReloading && Now > _pReloadUntil) Sfx.Play2D(SoundRules.Event("reload", WeaponId(a.Slot - 1)));
        _sfxReloading = reloading;
    }

    /// <summary>The round start and end sounds, on a phase change (<see cref="ClientMatchState.PhaseChanged"/>; the first state of a connection is no round start).</summary>
    void SfxPhase(MatchPhase? prev, MatchPhase p)
    {
        if (p is MatchPhase.Running or MatchPhase.Ending) _streaks.Reset(); // a round reset drops earned streaks too, as on the server
        if (p == MatchPhase.Running) ScudRoundStart(); // the burnt ground and the missiles in the air of the last round
        if (prev == null || !Sfx.Enabled) return;
        if (p == MatchPhase.Running) Sfx.Play2D("round.start");
        else if (p == MatchPhase.Ending) Sfx.Play2D("round.end");
    }

    /// <summary>Own footsteps from horizontal speed and distance, and a thud on landing from a fall. One surface in v1 (ground kind is not cheap to query).</summary>
    void SfxOwnMovement(float dt)
    {
        if (!Sfx.Enabled || _me == null || Ctx.Driving || Dead) return;
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

    void SfxRemoteStep(RemotePlayer r, Vector3 now, float dt)
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

    void SfxCar(CarView v, Vector3 at, float dt)
    {
        if (!Sfx.Enabled) return;
        var def = Vehicles != null && v.Kind < Vehicles.Vehicles.Count ? Vehicles.Vehicles[v.Kind] : null;
        // the vehicle this player rides (any seat) is heard from the inside: placed at the listener, so the chase camera's distance and the car's turning never pan or fade it
        var mine = Ctx.Cars.Rides(v);
        var world = at;
        if (mine) at = Sfx.ListenerPosition(at);
        if (def is { ReverseBeep: true })
        {
            // the backup alarm: from the drawn signed speed, for any driver (a bot's too); the rider hears it as loud as the engine (PlayCarSound puts it at the listener)
            var active = v.Driver != 0 && !v.Destroyed && (mine || Sfx.ListenerDistance(at) <= 60f);
            if (v.Beep.Step(DriveOf(v)?.Speed ?? 0f, dt, active)) Ctx.Cars.Sounds.PlayOnce("vehicle.reverse_beep", world, v);
        }
        if (def?.Sounds is { } set)
        {
            // a type with its own set: layered engine sounds, kept only while it is occupied and near
            if (v.Driver == 0 || v.Destroyed || (!mine && Sfx.ListenerDistance(at) > 160f)) { v.Engine?.Release(); return; }
            var kmh = DriveOf(v) is { } ds ? ds.Speed * 3.6f : v.Kmh; // the drawn speed, signed (negative reversing): smooth every frame, not once a snapshot
            (v.Engine ??= new VehicleEngineSound($"{def.Id}#{v.Id}")).Update(set, kmh, def.TopSpeed * 3.6f, at, dt);
            return;
        }
        if (v.Driver == 0 || (!mine && Sfx.ListenerDistance(at) > 80f)) { v.Snd?.Release(); v.Snd = null; return; }
        v.Snd ??= Sfx.Loop("vehicle.engine", at);
        v.Snd?.Update(at, SoundRules.EnginePitch(v.Kmh), 0f, exact: true);
    }

    void SfxTeardown()
    {
        foreach (var r in _rockets.Values) r.Snd?.Release();
        Ctx.Cars.ReleaseSounds();
    }
}
