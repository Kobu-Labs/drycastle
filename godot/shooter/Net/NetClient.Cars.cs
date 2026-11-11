using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Client side of vehicles (#91): car entities are drawn interpolated about 100 ms in the past like remote players, including the
/// driver's own car (no prediction in v1). While driving the body follows the car, a chase camera replaces the first-person one,
/// and the HUD shows the speed and the exit prompt; near a free car it shows the enter prompt.
/// </summary>
public partial class NetClient
{
    sealed class CarView
    {
        public Node3D Node = null!;
        public readonly Interpolator Buf = new();
        public ushort Driver;
        public NVec3 Pos;
        public float Yaw, Kmh;
        public uint LastTick;
        public LoopVoice? Snd;
    }

    readonly Dictionary<ushort, CarView> _cars = new();
    bool _driving;
    float _carYaw;
    /// <summary>Car smoke role: "a" drives, "b" stands in the way.</summary>
    public string CarBot = "";

    void OnVehicleState(ushort id, EntityRecord rec, uint tick)
    {
        if (!_cars.TryGetValue(id, out var v)) { v = new CarView { Node = CarFx.Make(World, VehicleRecord.Colour(rec)), Pos = rec.Position, Yaw = rec.YawRad }; _cars[id] = v; }
        v.Driver = VehicleRecord.Driver(rec);
        if (tick > v.LastTick && v.LastTick != 0)
        {
            var d = NVec3.Distance(rec.Position, v.Pos) / ((tick - v.LastTick) / (float)Wire.TickRate);
            v.Kmh += (d * 3.6f - v.Kmh) * 0.3f;
        }
        v.LastTick = tick; v.Pos = rec.Position;
        v.Buf.Push(tick, rec.Position, rec.YawRad, 0);
    }

    void RenderCars()
    {
        if (_lastTick == 0) return;
        var rt = _lastTick + (Time.GetTicksMsec() / 1000.0 - _lastArrival) * Wire.TickRate - InterpDelayTicks;
        float? kmh = null;
        foreach (var v in _cars.Values)
        {
            if (v.Buf.At(rt) is not { } s) continue;
            var p = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            v.Node.GlobalPosition = p;
            v.Node.Rotation = new Vector3(0, s.Yaw, 0);
            SfxCar(v, p);
            if (v.Driver == _myId && _driving && _me != null)
            {
                _carYaw = s.Yaw;
                _me.Chase(p, s.Yaw);
                kmh = v.Kmh;
            }
        }
        if (!_driving) _me?.EndChase(_carYaw);
        _hud?.SetVehicleSpeed(_driving ? kmh ?? 0f : null);
        string? prompt = null;
        if (_driving) prompt = Strings.F("hud.vehicle.exit", InputBindings.Name("interact"));
        else if (_me != null && !Dead && NearestFreeCar(_me.GlobalPosition) != null) prompt = Strings.F("hud.vehicle.enter", InputBindings.Name("interact"));
        _hud?.SetPrompt(prompt);
    }

    CarView? NearestFreeCar(Vector3 p)
    {
        CarView? best = null; var bd = 3f;
        foreach (var v in _cars.Values)
        {
            if (v.Driver != 0) continue;
            var d = new Vector2(v.Pos.X - p.X, v.Pos.Z - p.Z).Length();
            if (d <= bd) { best = v; bd = d; }
        }
        return best;
    }
}
