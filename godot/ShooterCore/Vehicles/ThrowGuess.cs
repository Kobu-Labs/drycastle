using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What the driver's client is doing about one animal its predicted vehicle touched (<see cref="ThrowGuess"/>).</summary>
public enum ThrowGuessPhase : byte { None = 0, Flying = 1, Settling = 2 }

/// <summary>
/// The driver's own picture of an animal the vehicle it drives has just hit. The server decides the run-over and the client hears of it a round trip and the interpolation
/// delay later, by when the predicted vehicle has driven a few metres on and the animal is seen inside its hull. So the client that drives throws the animal itself, on the arc
/// the server will give it (<see cref="CorpseRules.ThrowFromCar"/>), the moment its predicted box touches the animal as drawn. It is a picture only: hit points, the state and
/// where the animal ends up are the server's. When the server's throw shows, the picture goes over to the server's position by an offset that fades; when it never shows
/// (the server saw a miss) the animal glides back to where the server has it.
/// </summary>
public sealed class ThrowGuess
{
    /// <summary>How long the guessed arc waits for the server's throw to show before it is given up, seconds: a round trip and the interpolation delay with room.</summary>
    public const float ConfirmSeconds = 0.6f;
    /// <summary>How long after a guess no new one starts, seconds: the server's own time between two run-overs of one body.</summary>
    public const float SettleSeconds = 0.8f;
    /// <summary>How fast the offset to the server's position fades, 1/s. Slow: the server's arc is seen later than the guessed one all the way, so a fast fade would pull the body back at the vehicle.</summary>
    public const float GlideFade = 2f;

    public static readonly StateMachineSpec<ThrowGuessPhase> Spec = StateMachineSpec<ThrowGuessPhase>.Define(
            "Vehicle.ThrowGuess", "The driver's client shows an animal its predicted vehicle hit as thrown before the server says so, then hands the picture over to the server's.", ThrowGuessPhase.None)
        .Allow(ThrowGuessPhase.None, ThrowGuessPhase.Flying)
        .Allow(ThrowGuessPhase.Flying, ThrowGuessPhase.Settling)
        .Allow(ThrowGuessPhase.Settling, ThrowGuessPhase.None)
        .Note(ThrowGuessPhase.None, "Drawn where the server has it (plus what is left of the last offset). A touch of the predicted vehicle starts Flying.")
        .Note(ThrowGuessPhase.Flying, "Flies on the guessed arc (clock: ConfirmSeconds). The server's throw showing, or the clock running out, ends it: the difference to the server's position becomes the offset.")
        .Note(ThrowGuessPhase.Settling, "Drawn at the server's position plus the fading offset (clock: SettleSeconds); no new guess, as the server throws one body once in that time.")
        .Build();

    readonly StateMachine<ThrowGuessPhase> _fsm = new(Spec);
    Vector3 _pos, _vel, _offset;
    float _floorY;

    public ThrowGuessPhase Phase => _fsm.Current;
    /// <summary>The guessed arc is being drawn: the body is shown as a thrown one whatever the server's state says.</summary>
    public bool Flying => _fsm.Is(ThrowGuessPhase.Flying);
    /// <summary>A touch may start a guess now.</summary>
    public bool Ready => _fsm.Is(ThrowGuessPhase.None);

    /// <summary>The predicted vehicle touched the animal drawn at <paramref name="feet"/>: it leaves at <paramref name="velocity"/>. False while a guess runs or has just run.</summary>
    public bool Start(Vector3 feet, Vector3 velocity)
    {
        if (!Ready) return false;
        _pos = feet; _vel = velocity; _floorY = feet.Y; _offset = Vector3.Zero;
        _fsm.Enter(ThrowGuessPhase.Flying, ConfirmSeconds);
        return true;
    }

    /// <summary>
    /// One frame: where the animal is drawn. <paramref name="server"/> is where the snapshots have it at the render tick, <paramref name="serverThrown"/> whether they show it
    /// thrown by now (flying, knocked down, or dead of the hit).
    /// </summary>
    public Vector3 Step(float dt, Vector3 server, bool serverThrown)
    {
        if (_fsm.Is(ThrowGuessPhase.Flying))
        {
            _vel.Y -= MoveConsts.Gravity * dt;
            _pos += _vel * dt;
            if (_pos.Y <= _floorY) { _pos.Y = _floorY; _vel = Vector3.Zero; } // landed where it left the ground's height: it lies there until the server's picture takes over
            var expired = _fsm.Tick(dt);
            if (!serverThrown && !expired) return _pos;
            _offset = _pos - server;
            _fsm.Enter(ThrowGuessPhase.Settling, SettleSeconds);
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
