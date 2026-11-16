using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What the driver's client is doing about one animal its predicted vehicle touched (<see cref="ThrowGuess"/>).</summary>
public enum ThrowGuessPhase : byte { None = 0, Flying = 1, Confirmed = 2, Settling = 3 }

/// <summary>
/// The driver's own picture of an animal the vehicle it drives has just hit. The server decides the run-over and the client hears of it a round trip and the interpolation
/// delay later, by when the predicted vehicle has driven a few metres on and the animal is seen inside its hull. So the client that drives throws the animal itself, on the arc
/// the server will give it (<see cref="CorpseRules.ThrowFromCar"/>), the moment its predicted box touches the animal as drawn. It is a picture only: hit points, the state and
/// where the animal ends up are the server's. The server's arc is the same one, seen later: so once the server's throw shows, the guessed arc is kept to its landing (going
/// over to the server's position in flight would pull the body back by that delay), and only when the server's body has landed too does the picture go over to the server's
/// position, by an offset that fades (the two landing spots are near each other). When the throw never shows (the server saw a miss) the animal glides back to where the server has it.
/// </summary>
public sealed class ThrowGuess
{
    /// <summary>How long the guessed arc waits for the server's throw to show before it is given up, seconds: a round trip and the interpolation delay with room.</summary>
    public const float ConfirmSeconds = 0.6f;
    /// <summary>How long after a guess no new one starts, seconds: the server's own time between two run-overs of one body.</summary>
    public const float SettleSeconds = 0.8f;
    /// <summary>The longest the guessed arc is kept once the server's throw shows, seconds: the server's body lands well inside it.</summary>
    public const float LandSeconds = 5f;
    /// <summary>How fast the offset to the server's position fades, 1/s.</summary>
    public const float GlideFade = 3f;

    public static readonly StateMachineSpec<ThrowGuessPhase> Spec = StateMachineSpec<ThrowGuessPhase>.Define(
            "Vehicle.ThrowGuess", "The driver's client shows an animal its predicted vehicle hit as thrown before the server says so, then hands the picture over to the server's.", ThrowGuessPhase.None)
        .Allow(ThrowGuessPhase.None, ThrowGuessPhase.Flying)
        .Allow(ThrowGuessPhase.Flying, ThrowGuessPhase.Confirmed, ThrowGuessPhase.Settling)
        .Allow(ThrowGuessPhase.Confirmed, ThrowGuessPhase.Settling)
        .Allow(ThrowGuessPhase.Settling, ThrowGuessPhase.None)
        .Note(ThrowGuessPhase.None, "Drawn where the server has it (plus what is left of the last offset). A touch of the predicted vehicle starts Flying.")
        .Note(ThrowGuessPhase.Flying, "Flies on the guessed arc (clock: ConfirmSeconds). The server's throw showing makes it Confirmed; the clock running out (a miss) makes it Settling, the difference to the server's position the offset.")
        .Note(ThrowGuessPhase.Confirmed, "The server shows the throw, later than the guess: the guessed arc is kept, and the body lies where it lands, until the server's body has landed too (clock: LandSeconds, a guard). Then Settling, the difference of the two landing spots the offset.")
        .Note(ThrowGuessPhase.Settling, "Drawn at the server's position plus the fading offset (clock: SettleSeconds); no new guess, as the server throws one body once in that time.")
        .Build();

    readonly StateMachine<ThrowGuessPhase> _fsm = new(Spec);
    Vector3 _pos, _vel, _offset;
    float _floorY;
    bool _rolling; // the guessed body has come down and rolls on (ThrownRoll), as the server's will

    public ThrowGuessPhase Phase => _fsm.Current;
    /// <summary>The guessed arc is what is drawn: the body is shown as a thrown one (<see cref="Airborne"/>, or lying where it landed) whatever the server's state says.</summary>
    public bool Active => _fsm.IsAny(ThrowGuessPhase.Flying, ThrowGuessPhase.Confirmed);
    /// <summary>The guessed body still moves, in the air or rolling on the ground (false once it lies and waits for the server's).</summary>
    public bool Moving => Active && _vel != Vector3.Zero;
    /// <summary>Where the guessed body is: the ground under it is what the next <see cref="Step"/> is given.</summary>
    public Vector3 Position => _pos;
    /// <summary>A touch may start a guess now.</summary>
    public bool Ready => _fsm.Is(ThrowGuessPhase.None);

    /// <summary>The predicted vehicle touched the animal drawn at <paramref name="feet"/>: it leaves at <paramref name="velocity"/>. False while a guess runs or has just run.</summary>
    public bool Start(Vector3 feet, Vector3 velocity)
    {
        if (!Ready) return false;
        _pos = feet; _vel = velocity; _floorY = feet.Y; _offset = Vector3.Zero; _rolling = false;
        _fsm.Enter(ThrowGuessPhase.Flying, ConfirmSeconds);
        return true;
    }

    /// <summary>
    /// One frame: where the animal is drawn. <paramref name="server"/> is where the snapshots have it at the render tick, <paramref name="serverThrown"/> whether they show it
    /// thrown by now (flying, knocked down, or dead of the hit), <paramref name="serverInAir"/> whether that body still moves (flying or rolling),
    /// <paramref name="groundY"/> the ground's height under <see cref="Position"/> (null: not known, the last one stands).
    /// </summary>
    public Vector3 Step(float dt, Vector3 server, bool serverThrown, bool serverInAir, float? groundY = null)
    {
        if (Active)
        {
            if (groundY is { } gy) _floorY = gy;
            if (_vel != Vector3.Zero)
            {
                if (_rolling)
                {
                    if (_pos.Y - _floorY > ThrownRoll.LedgeM) _rolling = false; // over an edge: it falls
                    else { _pos += _vel * dt; _pos.Y = _floorY; ThrownRoll.Roll(ref _vel, dt); } // at rest it lies there until the server's picture takes over
                }
                if (!_rolling && _vel != Vector3.Zero)
                {
                    _vel.Y -= MoveConsts.Gravity * dt;
                    _pos += _vel * dt;
                    if (_pos.Y <= _floorY && _vel.Y <= 0) { _pos.Y = _floorY; _vel = ThrownRoll.Land(_vel); _rolling = _vel != Vector3.Zero; }
                    else if (_pos.Y < _floorY) _pos.Y = _floorY; // the ground rose under it while it was still going up: it skims on
                }
            }
            var expired = _fsm.Tick(dt);
            if (_fsm.Is(ThrowGuessPhase.Flying) && serverThrown) { _fsm.Enter(ThrowGuessPhase.Confirmed, LandSeconds); expired = false; }
            var over = _fsm.Is(ThrowGuessPhase.Flying) ? expired : expired || !serverInAir && _vel == Vector3.Zero; // a miss, or both bodies are down
            if (over)
            {
                _offset = _pos - server;
                _fsm.Enter(ThrowGuessPhase.Settling, SettleSeconds);
            }
            return _pos;
        }
        if (_fsm.Is(ThrowGuessPhase.Settling) && _fsm.Tick(dt)) _fsm.Enter(ThrowGuessPhase.None);
        if (_offset == Vector3.Zero) return server;
        _offset *= MathF.Exp(-GlideFade * dt);
        if (_offset.LengthSquared() < 1e-4f) _offset = Vector3.Zero;
        return server + _offset;
    }

    /// <summary>The animal is no longer shown (dormant, despawned and reused): nothing is left of the guess.</summary>
    public void Reset()
    {
        _fsm.Reset(ThrowGuessPhase.None);
        _offset = Vector3.Zero;
    }
}
