using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The radar unit on the client (#371, GLOSSARY "Radar unit", "Radar sweep", ADR 0037). Everyone sees the dish turn while the unit is hitched (<see cref="SpinDish"/>: the model's <c>dish</c> node, skipped when a
/// model has none). The operator, and only the operator, is sent the sweep (<see cref="EventKind.RadarSweep"/>, about ten a second): it goes into the HUD's <see cref="RadarDisplay"/>, which the tactical map draws as the
/// radar screen (<c>TacticalMap.Radar.cs</c>). The screen comes up when the player takes the seat and goes with it; the map key and Esc close and reopen it. The unit's seat is a trailer's, so the generic vehicle
/// loop does not give it a view: <see cref="RenderRadar"/> sits the camera at the seat's eye (a sight seat, like the Scud's).
/// </summary>
public partial class NetClient
{
    /// <summary>The dish turns once a second when the unit is hitched (rad/s) and eases in and out over about a second.</summary>
    const float DishSpeed = MathF.Tau;
    const float DishResponse = 1.5f;

    bool _radarWas;
    string _radarPrompt = "";
    double _radarPromptAt = -10;

    void InitRadar() => EventReceived += OnRadarEvent;

    void OnRadarEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.RadarSweep || _hud == null) return;
        if (RadarCodec.Decode(payload) is not { } m || MyRadarSeat() is not { } seat || seat.Car.Id != m.Unit) return; // only the operator is told, and only of the unit they sit in
        _hud.Radar.Apply(m, Time.GetTicksMsec() / 1000.0, GridRef);
    }

    /// <summary>The radar unit the local player is the operator of, or null (on foot, dead, in another vehicle).</summary>
    (CarView Car, VehicleDef Def)? MyRadarSeat() => MySeat() is { Def.Radar: not null } s ? (s.Car, s.Def) : null;

    /// <summary>Once a frame after the vehicles are placed: the operator's screen and view while seated, nothing otherwise.</summary>
    void RenderRadar()
    {
        if (_hud == null) return;
        if (MyRadarSeat() is not { } seat)
        {
            if (_radarWas) EndRadar();
            return;
        }
        var radar = _hud.Radar;
        var at = seat.Car.Node.GlobalPosition;
        radar.SetUnit(seat.Car.Id, at.X, at.Z);
        if (!_radarWas)
        {
            _radarWas = true;
            radar.Clear();
            radar.Seated = true;
            _hud.OpenTacticalMap(MenuOpen?.Invoke() == true); // the screen comes up with the seat; the arbiter may refuse (a menu is open), the map key opens it then
        }
        if (_me == null || !ShotFx.Enabled) return;
        TrackView(seat.Car, 0);
        RideSeat(seat.Car, 0); // the unit is a trailer: the vehicle loop does not give its seat a camera
    }

    /// <summary>The operator got out (or died, or the unit was unhitched): the screen closes and forgets the sweep.</summary>
    void EndRadar()
    {
        _radarWas = false;
        if (_hud == null) return;
        _hud.Radar.Seated = false;
        _hud.Radar.Clear();
        _hud.CloseTacticalMap();
    }

    /// <summary>The operator's prompt line: the key that opens the screen and the one that leaves. Rebuilt once a second in case a key was rebound.</summary>
    string RadarPrompt()
    {
        if (Now - _radarPromptAt < 1.0) return _radarPrompt;
        _radarPromptAt = Now;
        return _radarPrompt = Strings.F("hud.radar.prompt", InputBindings.Name("map"), InputBindings.Name("interact"));
    }

    /// <summary>The prompt key for a vehicle that can be boarded: the radar unit's is its own.</summary>
    string EnterPromptKey(CarView free) => FlightDef(free) != null ? "hud.flight.enter" : free.Trailer != null ? "hud.radar.enter" : "hud.vehicle.enter";

    /// <summary>
    /// The dish of a radar unit turns clockwise once a second while it is hitched and eases to a stop when it is not (<paramref name="hitched"/>: drawn from a tractor this frame). A model without a <c>dish</c> node
    /// (the grey box) just does not animate it. Client only: the sweep itself is the server's, at its own pace.
    /// </summary>
    static void SpinDish(TrailerView t, bool hitched, float dt)
    {
        var target = hitched ? DishSpeed : 0f;
        t.DishSpeed += (target - t.DishSpeed) * (1f - MathF.Exp(-DishResponse * dt));
        if (t.Dish == null || !GodotObject.IsInstanceValid(t.Dish) || t.DishSpeed < 0.01f) return;
        t.Dish.RotateY(-t.DishSpeed * dt); // clockwise seen from above, as the arm on the screen
    }
}
