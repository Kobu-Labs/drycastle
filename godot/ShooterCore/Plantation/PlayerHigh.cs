using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Status;

namespace Drycastle.ShooterCore.Plantation;

/// <summary>Whether a player is high (GLOSSARY "High"): the plantation's effect, server-owned.</summary>
public enum HighState : byte { Sober, High }

/// <summary>
/// The high of one player (<c>Player.High</c>), server side, Godot-free. A completed hold of the interact key inside a plantation (<see cref="PlantationRules"/>, counted by
/// <see cref="InteractHold"/>) makes the player <see cref="HighState.High"/> for <c>high_seconds</c>; another completed hold while high starts the time over. The clock runs out, or the
/// player dies, and they are sober again. The replicated bit is <see cref="StatusKind.High"/> of <see cref="PlayerStatuses"/>, which the host keeps equal to <see cref="IsHigh"/>.
/// </summary>
public sealed class PlayerHigh
{
    public static readonly StateMachineSpec<HighState> Spec = StateMachineSpec<HighState>
        .Define("Player.High", "Whether a player is high from the plantation: sober, or high for a while with a clock that a new hold restarts.", HighState.Sober)
        .Allow(HighState.Sober, HighState.High)
        .Allow(HighState.High, HighState.Sober, HighState.High)
        .Note(HighState.Sober, "The default, and what a death, a round reset or the clock running out leaves.")
        .Note(HighState.High, "A completed hold in a plantation started it: the cigarette, the eyes and (for the player themselves) the nausea show. High to High is the restart: another completed hold starts the clock over.")
        .Build();

    readonly StateMachine<HighState> _fsm = new(Spec);

    public bool IsHigh => _fsm.Is(HighState.High);

    public HighState State => _fsm.Current;

    /// <summary>Seconds of the high left; 0 when sober.</summary>
    public float SecondsLeft => IsHigh ? MathF.Max(0f, _fsm.Left) : 0f;

    /// <summary>
    /// A hold completed: high for <paramref name="seconds"/> from now, or, when already high, the clock starts over. True when this made a sober player high (the host then sets the status bit).
    /// </summary>
    public bool Complete(float seconds)
    {
        if (IsHigh) { _fsm.Restart(seconds); return false; }
        _fsm.Enter(HighState.High, seconds);
        return true;
    }

    /// <summary>Advances the clock. True on the tick the high runs out (the player is sober then).</summary>
    public bool Tick(float dt)
    {
        if (!IsHigh || !_fsm.Tick(dt)) return false;
        _fsm.Enter(HighState.Sober);
        return true;
    }

    /// <summary>Death, a round reset or an admin clear: sober at once. True when the player was high.</summary>
    public bool Clear()
    {
        if (!IsHigh) return false;
        _fsm.Enter(HighState.Sober);
        return true;
    }
}

/// <summary>What else may own the interact key (E) of a player on foot, as far as the server can tell: whatever is in reach of the key and is not the plantation.</summary>
[Flags]
public enum KeyCompetitor
{
    None = 0,
    Corpse = 1,
    GroundItem = 2,
    Crate = 4,
    /// <summary>The bus garage's doors (a press at the doorway).</summary>
    Doors = 8,
    Ladder = 16,
    Vehicle = 32,
    /// <summary>The Holy Mass seat.</summary>
    MassSeat = 64,
    /// <summary>The rozhlas set at the dictation table.</summary>
    RozhlasSet = 128,
    /// <summary>Lying Loot (a hold sends it back).</summary>
    Loot = 256,
    /// <summary>A rocket cart beside an empty Scud truck (a hold loads it).</summary>
    ScudCart = 512,
    /// <summary>A lying Duffel the player may pick up (a press takes it).</summary>
    Duffel = 1024,
}

/// <summary>The rules of the plantation hold, in one place so the server and the tests agree.</summary>
public static class PlantationRules
{
    public const string Kind = "plantation";

    /// <summary>
    /// Whether a hold of the interact key counts towards getting high: the player is alive, on foot (no seat, not held, not seated at the Mass, not pinned, not steering a streak), inside a
    /// plantation, and nothing else owns the key. The plantation has the LOWEST priority of the key: every <see cref="KeyCompetitor"/> in reach wins.
    /// </summary>
    public static bool HoldCounts(bool alive, bool onFoot, bool inside, KeyCompetitor competitors) =>
        alive && onFoot && inside && competitors == KeyCompetitor.None;
}

/// <summary>
/// The nausea of a high player's own camera (plantation.json <c>nausea</c>), Godot-free. Each frame <see cref="Step"/> gives the change of the look angles to add to the real yaw and pitch
/// (so what the player aims at really moves and the shots follow), and the roll and the FOV change to draw. The sway is a sum of sines, followed with a lag, faded in over <c>fade_s</c> and out
/// again at the end; with the reduce-motion setting only a small share of it is left. Not running (the camera is owned by something else) it pauses and gives nothing.
/// </summary>
public sealed class Nausea
{
    readonly NauseaData _d;
    double _t;
    float _on;
    float _yaw, _pitch, _roll, _fov;

    public Nausea(NauseaData data) => _d = data;

    /// <summary>The sway is still on screen (fading in, running or fading out).</summary>
    public bool Active => _on > 0.0005f || _yaw != 0 || _pitch != 0 || _roll != 0 || _fov != 0;

    /// <summary>The roll in radians to draw now, and the FOV change in degrees.</summary>
    public (float RollRad, float FovDeg) Visual => (_roll * Rad, _fov);

    /// <summary>
    /// One frame. <paramref name="high"/>: the player is high (the sway comes on, else it fades out); <paramref name="owned"/>: this view is the player's own on-foot eye (false while a cut-scene, a
    /// turret or missile view, a seat or the death view owns the camera: everything pauses). Returns the yaw and pitch change in radians to add to the look this frame.
    /// </summary>
    public (float YawRad, float PitchRad) Step(float dt, bool high, bool owned, bool reduceMotion)
    {
        if (!owned || dt <= 0) return (0, 0);
        _on = Ramp.MoveToward(_on, high ? 1f : 0f, dt / (float)_d.FadeS);
        if (_on <= 0f && !high) { var gone = (-_yaw * Rad, -_pitch * Rad); _yaw = _pitch = _roll = _fov = 0; _t = 0; return gone; } // faded out: the (tiny) rest of the sway goes
        _t += dt;
        var k = _on * (reduceMotion ? (float)_d.ReducedScale : 1f);
        var follow = _d.LagS <= 0 ? 1f : 1f - MathF.Exp(-dt / (float)_d.LagS);
        var yaw = Sum(_d.Yaw, _t) * k; var pitch = Sum(_d.Pitch, _t) * k;
        var nYaw = _yaw + (yaw - _yaw) * follow;
        var nPitch = _pitch + (pitch - _pitch) * follow;
        var dYaw = nYaw - _yaw; var dPitch = nPitch - _pitch;
        _yaw = nYaw; _pitch = nPitch;
        _roll += (Sum(_d.Roll, _t) * k - _roll) * follow;
        _fov += (Sum(_d.Fov, _t) * k - _fov) * follow;
        return (dYaw * Rad, dPitch * Rad);
    }

    const float Rad = MathF.PI / 180f;

    static float Sum(List<SwayWave> waves, double t)
    {
        var s = 0.0;
        foreach (var w in waves) s += w.Deg * Math.Sin(2 * Math.PI * t / w.PeriodS);
        return (float)s;
    }
}
