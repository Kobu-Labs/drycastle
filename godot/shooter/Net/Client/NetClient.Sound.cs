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

    /// <summary>True while the local player rides this vehicle in any seat. The listener is the camera (there is no AudioListener3D), and the chase camera sits metres behind and above, so a sound at a point of this vehicle changes with the camera's distance and orbit: its sounds go through <see cref="PlayCarSound"/>, never to a point on it (docs/standards/car-design.md section 9).</summary>
    bool RidesCar(CarView v) => Ctx.Driving && (v.Driver == _myId || (_seats is { Mine: >= 0 } st && st.CarId == v.Id));

    /// <summary>A one-shot of a vehicle (<paramref name="car"/>; null when it is not known) heard at <paramref name="at"/>: when the local player rides it, at the listener with an exact level and no range cull (like its engine), else a plain positional sound.</summary>
    void PlayCarSound(string id, Vector3 at, CarView? car, float dbOffset = 0f)
    {
        if (!Sfx.Enabled) return;
        if (car != null && RidesCar(car)) Sfx.Play3D(id, Sfx.ListenerPosition(at), dbOffset, exact: true);
        else Sfx.Play3D(id, at, dbOffset);
    }

    /// <summary>A vehicle hit something (<see cref="EventKind.VehicleCrash"/>, #410): the bump or the hard crash, at the vehicle; its riders hear it at full level.</summary>
    void OnVehicleCrash(ushort carId, CrashSound sound)
    {
        if (!_cars.TryGetValue(carId, out var car) || !GodotObject.IsInstanceValid(car.Node)) return;
        PlayCarSound(sound == CrashSound.Hard ? "vehicle.crash_hard" : "vehicle.crash", car.Node.GlobalPosition, car);
    }

    /// <summary>A sound of a vehicle that lasts (the harvester's reel and chew, seconds long) and rides with it: <see cref="StepCarVoices"/> keeps it on the moving machine, or at the listener when the local player rides it. It ends with the file.</summary>
    sealed class CarVoice
    {
        public required LoopVoice Voice;
        public required CarView Car;
        /// <summary>Where on the vehicle it is, in the vehicle's own frame.</summary>
        public Vector3 Local;
    }
    readonly List<CarVoice> _carVoices = new();

    /// <summary>Starts a long sound of <paramref name="car"/> at the world point <paramref name="at"/> on it, and keeps it there as the vehicle moves (<see cref="StepCarVoices"/>).</summary>
    void PlayCarVoice(string id, Vector3 at, CarView car)
    {
        if (!Sfx.Enabled || !GodotObject.IsInstanceValid(car.Node)) return;
        var rides = RidesCar(car);
        if (Sfx.Loop(id, rides ? Sfx.ListenerPosition(at) : at) is not { } voice) { Sfx.Play3D(id, at); return; } // no loop voice free: a plain one-shot
        _carVoices.Add(new CarVoice { Voice = voice, Car = car, Local = car.Node.ToLocal(at) });
    }

    /// <summary>Per frame: moves the vehicles' long sounds with them and frees the voices whose file has ended or whose vehicle is gone.</summary>
    void StepCarVoices()
    {
        for (var i = _carVoices.Count - 1; i >= 0; i--)
        {
            var cv = _carVoices[i];
            if (!cv.Voice.Playing || cv.Car.Destroyed || !GodotObject.IsInstanceValid(cv.Car.Node)) { cv.Voice.Release(); _carVoices.RemoveAt(i); continue; }
            var rides = RidesCar(cv.Car);
            cv.Voice.Update(rides ? Sfx.ListenerPosition(cv.Car.Node.GlobalPosition) : cv.Car.Node.ToGlobal(cv.Local), exact: rides);
        }
    }

    void SfxCar(CarView v, Vector3 at, float dt)
    {
        if (!Sfx.Enabled) return;
        var def = Vehicles != null && v.Kind < Vehicles.Vehicles.Count ? Vehicles.Vehicles[v.Kind] : null;
        // the vehicle this player rides (any seat) is heard from the inside: placed at the listener, so the chase camera's distance and the car's turning never pan or fade it
        var mine = RidesCar(v);
        var world = at;
        if (mine) at = Sfx.ListenerPosition(at);
        if (def is { ReverseBeep: true })
        {
            // the backup alarm: from the drawn signed speed, for any driver (a bot's too); the rider hears it as loud as the engine (PlayCarSound puts it at the listener)
            var active = v.Driver != 0 && !v.Destroyed && (mine || Sfx.ListenerDistance(at) <= 60f);
            if (v.Beep.Step(DriveOf(v)?.Speed ?? 0f, dt, active)) PlayCarSound("vehicle.reverse_beep", world, v);
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
        foreach (var cv in _carVoices) cv.Voice.Release();
        _carVoices.Clear();
        foreach (var c in _cars.Values) { c.Snd?.Release(); c.Engine?.Release(); }
    }
}
