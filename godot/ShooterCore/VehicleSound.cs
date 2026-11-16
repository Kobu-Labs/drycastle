namespace Drycastle.ShooterCore;

/// <summary>
/// One sound of a vehicle's engine set (vehicles.json "sounds"): the id of a sounds.json entry plus how it follows the speed and how fast it
/// fades in and out. For a one-shot (<see cref="VehicleSoundsDef.Lift"/>) only <see cref="Sound"/> counts.
/// </summary>
public class VehicleSoundLayerDef
{
    /// <summary>sounds.json id. A looping sound for every layer except the lift one-shot.</summary>
    public string Sound { get; set; } = "";
    /// <summary>[pitch at standstill, pitch at top speed]; the pitch follows the speed linearly between them.</summary>
    public float[] Pitch { get; set; } = { 1f, 1f };
    /// <summary>Seconds to fade up when the layer takes over, and down when it gives way.</summary>
    public float FadeIn { get; set; } = 0.15f;
    public float FadeOut { get; set; } = 0.4f;

    public float PitchAt(float speedFraction) => Pitch[0] + (Pitch[1] - Pitch[0]) * Math.Clamp(speedFraction, 0f, 1f);
}

/// <summary>
/// The engine sound set of one vehicle type (vehicles.json "sounds"; every part optional, a type with none keeps the shared vehicle.engine loop).
/// <see cref="Background"/> runs all the time a driver sits in; <see cref="Idle"/>, <see cref="Accel"/>, <see cref="Brake"/> and <see cref="Top"/> take
/// turns over it (<see cref="EnginePhase"/>); <see cref="Lift"/> is a one-shot (turbo blow-off) played when the driver comes off the gas.
/// </summary>
public class VehicleSoundsDef
{
    public VehicleSoundLayerDef? Background { get; set; }
    public VehicleSoundLayerDef? Idle { get; set; }
    public VehicleSoundLayerDef? Accel { get; set; }
    public VehicleSoundLayerDef? Lift { get; set; }
    public VehicleSoundLayerDef? Brake { get; set; }
    public VehicleSoundLayerDef? Top { get; set; }

    /// <summary>The looping layers in <see cref="EngineLayer"/> order (null where the type has none).</summary>
    public VehicleSoundLayerDef?[] Loops => new[] { Background, Idle, Accel, Brake, Top };

    public IEnumerable<(string Role, VehicleSoundLayerDef Layer)> All()
    {
        if (Background != null) yield return ("background", Background);
        if (Idle != null) yield return ("idle", Idle);
        if (Accel != null) yield return ("accel", Accel);
        if (Lift != null) yield return ("lift", Lift);
        if (Brake != null) yield return ("brake", Brake);
        if (Top != null) yield return ("top", Top);
    }

    public static void Validate(string vehicleId, VehicleSoundsDef s)
    {
        foreach (var (role, l) in s.All())
        {
            if (string.IsNullOrWhiteSpace(l.Sound)) throw new DataFileException($"vehicles.json: {vehicleId}: sounds.{role} needs a sound id");
            if (l.Pitch is not { Length: 2 } || !(l.Pitch[0] > 0) || !(l.Pitch[1] > 0)) throw new DataFileException($"vehicles.json: {vehicleId}: sounds.{role}.pitch is [at standstill, at top speed], both positive");
            if (l.FadeIn < 0 || l.FadeOut < 0) throw new DataFileException($"vehicles.json: {vehicleId}: sounds.{role}: fades are not negative");
        }
    }
}

/// <summary>The looping layers of a vehicle's engine, the index into <see cref="EngineMix"/>.</summary>
public enum EngineLayer { Background, Idle, Accel, Brake, Top }

/// <summary>What the engine is doing, as far as the sound can tell from the speed alone (the snapshot has no pedal bits).</summary>
public enum EnginePhase
{
    /// <summary>Standing, engine ticking over.</summary>
    Idle,
    /// <summary>Speed rising.</summary>
    Accelerating,
    /// <summary>Rolling with no push: off the gas.</summary>
    Coasting,
    /// <summary>Speed falling hard.</summary>
    Braking,
    /// <summary>Holding the top speed.</summary>
    TopSpeed,
}

/// <summary>The numbers behind <see cref="EngineSoundModel"/>: speeds in km/h, rates in km/h per second.</summary>
public sealed class EngineSoundTuning
{
    /// <summary>Under this a standing vehicle idles.</summary>
    public float IdleKmh = 4f;
    /// <summary>Speed gain above this reads as accelerating; speed loss under -<see cref="BrakeRate"/> as braking.</summary>
    public float AccelRate = 1.5f;
    public float BrakeRate = 14f;
    /// <summary>From this share of the top speed, with no braking, the vehicle is "at top speed".</summary>
    public float TopFraction = 0.96f;
    /// <summary>A phase lasts at least this long, so a ragged speed does not flutter between them.</summary>
    public float MinDwell = 0.2f;
    /// <summary>Time constant of the rate filter, seconds.</summary>
    public float RateSmoothing = 0.25f;
    /// <summary>Coming off the gas plays the lift sound only after this long on it, from this share of the top speed, and not twice within the cooldown.</summary>
    public float LiftMinPushSeconds = 0.8f, LiftMinFraction = 0.2f, LiftCooldown = 1.5f;
}

/// <summary>
/// Which of a vehicle's engine sounds plays: <see cref="EnginePhase"/> from the speed history, and the lift event when the driver comes off the gas.
/// Godot-free; the client feeds it the drawn speed each frame and plays what <see cref="EngineMix"/> asks for.
/// </summary>
public sealed class EngineSoundModel
{
    public static readonly StateMachineSpec<EnginePhase> Spec = StateMachineSpec<EnginePhase>.Define("Engine.Sound", "Which engine sound a vehicle makes (idle, accelerating, off the gas, braking, top speed), read from its speed.", EnginePhase.Idle)
        .Allow(EnginePhase.Idle, EnginePhase.Accelerating, EnginePhase.Coasting, EnginePhase.Braking)
        .Allow(EnginePhase.Accelerating, EnginePhase.Idle, EnginePhase.Coasting, EnginePhase.Braking, EnginePhase.TopSpeed)
        .Allow(EnginePhase.Coasting, EnginePhase.Idle, EnginePhase.Accelerating, EnginePhase.Braking, EnginePhase.TopSpeed)
        .Allow(EnginePhase.Braking, EnginePhase.Idle, EnginePhase.Accelerating, EnginePhase.Coasting, EnginePhase.TopSpeed)
        .Allow(EnginePhase.TopSpeed, EnginePhase.Idle, EnginePhase.Accelerating, EnginePhase.Coasting, EnginePhase.Braking)
        .Note(EnginePhase.Idle, "standing: the idle loop")
        .Note(EnginePhase.Coasting, "off the gas: only the background loop (the lift sound plays on the way in)")
        .Build();

    readonly EngineSoundTuning _t;
    readonly StateMachine<EnginePhase> _fsm = new(Spec);
    float _lastSpeed = float.NaN, _rate, _push, _sinceLift = 1e9f;
    bool _lift;

    public EngineSoundModel(EngineSoundTuning? tuning = null) => _t = tuning ?? new EngineSoundTuning();

    public EnginePhase Phase => _fsm.Current;
    /// <summary>Smoothed speed change, km/h per second (negative when slowing).</summary>
    public float Rate => _rate;

    /// <summary>True once after the driver comes off the gas (the turbo sound is due); reading it clears it.</summary>
    public bool TakeLift() { var l = _lift; _lift = false; return l; }

    public void Reset()
    {
        _fsm.Reset(EnginePhase.Idle);
        _lastSpeed = float.NaN; _rate = 0; _push = 0; _sinceLift = 1e9f; _lift = false;
    }

    /// <summary>One frame: the drawn speed (km/h, either sign), the type's top speed in km/h and the frame time.</summary>
    public void Step(float kmh, float topKmh, float dt)
    {
        if (dt <= 0) return;
        var speed = Math.Abs(kmh);
        var raw = float.IsNaN(_lastSpeed) ? 0f : (speed - _lastSpeed) / dt;
        _lastSpeed = speed;
        _rate += (raw - _rate) * (1f - MathF.Exp(-dt / _t.RateSmoothing));
        _fsm.Tick(dt);
        _sinceLift += dt;

        var pushing = _fsm.IsAny(EnginePhase.Accelerating, EnginePhase.TopSpeed);
        if (pushing) _push += dt;

        var frac = topKmh > 0 ? speed / topKmh : 0f;
        EnginePhase want;
        if (speed < _t.IdleKmh && _rate < _t.AccelRate) want = EnginePhase.Idle;
        else if (_rate < -_t.BrakeRate) want = EnginePhase.Braking;
        else if (frac >= _t.TopFraction) want = EnginePhase.TopSpeed;
        else if (_rate > _t.AccelRate) want = EnginePhase.Accelerating;
        else want = EnginePhase.Coasting;

        if (_fsm.Is(want) || _fsm.Elapsed < _t.MinDwell) return;
        _fsm.Enter(want);
        if (pushing && want is EnginePhase.Coasting or EnginePhase.Braking && _push >= _t.LiftMinPushSeconds && frac >= _t.LiftMinFraction && _sinceLift >= _t.LiftCooldown)
        {
            _lift = true; _sinceLift = 0;
        }
        if (want is not (EnginePhase.Accelerating or EnginePhase.TopSpeed)) _push = 0;
    }

    /// <summary>The layer that <paramref name="phase"/> asks for on top of the background (null: the background alone).</summary>
    public static EngineLayer? LayerOf(EnginePhase phase) => phase switch
    {
        EnginePhase.Idle => EngineLayer.Idle,
        EnginePhase.Accelerating => EngineLayer.Accel,
        EnginePhase.Braking => EngineLayer.Brake,
        EnginePhase.TopSpeed => EngineLayer.Top,
        _ => null,
    };
}

/// <summary>The loudness of each looping layer, 0 to 1, eased towards what the phase asks so one sound crosses over into the next.</summary>
public sealed class EngineMix
{
    public const int Count = 5;
    public readonly float[] Gain = new float[Count];

    /// <summary>
    /// One frame. The background asks for full volume always, the phase's layer for full volume (the brake layer by the speed: a squeal at a walking
    /// pace is no squeal), every other layer for none. A layer missing from <paramref name="def"/> stays at 0.
    /// </summary>
    public void Step(VehicleSoundsDef def, EnginePhase phase, float speedFraction, float dt)
    {
        var loops = def.Loops;
        var active = EngineSoundModel.LayerOf(phase);
        for (var i = 0; i < Count; i++)
        {
            var l = loops[i];
            if (l == null) { Gain[i] = 0; continue; }
            var target = i == (int)EngineLayer.Background ? 1f : active == (EngineLayer)i ? 1f : 0f;
            if (target > 0 && i == (int)EngineLayer.Brake) target = Math.Clamp(speedFraction * 2f, 0f, 1f);
            var rate = target > Gain[i] ? l.FadeIn : l.FadeOut;
            Gain[i] = rate <= 0 ? target : target > Gain[i] ? Math.Min(target, Gain[i] + dt / rate) : Math.Max(target, Gain[i] - dt / rate);
        }
    }

    /// <summary>Gain as a volume offset in dB (silent: -80).</summary>
    public static float Db(float gain) => gain <= 0.001f ? -80f : 20f * MathF.Log10(gain);

    public void Clear() => Array.Clear(Gain);
}
