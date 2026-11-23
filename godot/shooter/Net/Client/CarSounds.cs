using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The sounds of vehicles as the local player hears them. The listener is the camera (there is no AudioListener3D), and the chase camera sits metres behind and above, so a sound at a point of the vehicle the local
/// player rides is placed at the listener with an exact level and no range cull (like its engine), and heard as loud as the engine.
/// </summary>
internal sealed class CarSounds
{
    /// <summary>A sound of a vehicle that lasts (the harvester's reel and chew, seconds long) and rides with it: <see cref="Step"/> keeps it on the moving machine, or at the listener when the local player rides it.</summary>
    sealed class Voice
    {
        public required LoopVoice Loop;
        public required CarView Car;
        /// <summary>Where on the vehicle it is, in the vehicle's own frame.</summary>
        public Vector3 Local;
    }

    readonly ClientContext _ctx;
    readonly ClientCars _cars;
    readonly List<Voice> _voices = new();

    public CarSounds(ClientContext ctx, ClientCars cars)
    {
        _ctx = ctx;
        _cars = cars;
    }

    /// <summary>A one-shot of a vehicle (<paramref name="car"/>; null when it is not known) heard at <paramref name="at"/>: when the local player rides it, at the listener with an exact level and no range cull.</summary>
    public void PlayOnce(string id, Vector3 at, CarView? car, float dbOffset = 0f)
    {
        if (!Sfx.Enabled) return;
        if (car != null && _cars.Rides(car)) Sfx.Play3D(id, Sfx.ListenerPosition(at), dbOffset, exact: true);
        else Sfx.Play3D(id, at, dbOffset);
    }

    /// <summary>Starts a long sound of <paramref name="car"/> at the world point <paramref name="at"/> on it, and keeps it there as the vehicle moves (<see cref="Step"/>).</summary>
    public void PlayLong(string id, Vector3 at, CarView car)
    {
        if (!Sfx.Enabled || !GodotObject.IsInstanceValid(car.Node)) return;
        var rides = _cars.Rides(car);
        if (Sfx.Loop(id, rides ? Sfx.ListenerPosition(at) : at) is not { } voice) { Sfx.Play3D(id, at); return; } // no loop voice free: a plain one-shot
        _voices.Add(new Voice { Loop = voice, Car = car, Local = car.Node.ToLocal(at) });
    }

    /// <summary>A vehicle hit something (<see cref="EventKind.VehicleCrash"/>, #410): the bump or the hard crash, at the vehicle; its riders hear it at full level.</summary>
    public void OnCrash(ushort carId, CrashSound sound)
    {
        if (!_cars.TryGet(carId, out var car) || !GodotObject.IsInstanceValid(car.Node)) return;
        PlayOnce(sound == CrashSound.Hard ? "vehicle.crash_hard" : "vehicle.crash", car.Node.GlobalPosition, car);
    }

    /// <summary>Per frame: moves the vehicles' long sounds with them and frees the voices whose file has ended or whose vehicle is gone.</summary>
    public void Step()
    {
        for (var i = _voices.Count - 1; i >= 0; i--)
        {
            var cv = _voices[i];
            if (!cv.Loop.Playing || cv.Car.Destroyed || !GodotObject.IsInstanceValid(cv.Car.Node)) { cv.Loop.Release(); _voices.RemoveAt(i); continue; }
            var rides = _cars.Rides(cv.Car);
            cv.Loop.Update(rides ? Sfx.ListenerPosition(cv.Car.Node.GlobalPosition) : cv.Car.Node.ToGlobal(cv.Local), exact: rides);
        }
    }

    public void Release()
    {
        foreach (var cv in _voices) cv.Loop.Release();
        _voices.Clear();
    }
}
