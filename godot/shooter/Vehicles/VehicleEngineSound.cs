using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Vehicles;

/// <summary>
/// The engine sounds of one vehicle that has a vehicles.json "sounds" set: <see cref="EngineSoundModel"/> reads its speed, <see cref="EngineMix"/> crosses the
/// loops over (background always, idle / accelerating / braking / top speed taking turns) and the lift one-shot plays off the gas. A loop holds a voice of
/// <see cref="Sfx"/> only while it is audible, so a set of five costs a few voices, not five.
/// </summary>
public sealed class VehicleEngineSound
{
    readonly EngineSoundModel _model = new();
    readonly EngineMix _mix = new();
    readonly LoopVoice?[] _voices = new LoopVoice?[EngineMix.Count];
    readonly string _name;
    EnginePhase _phase;
    // once-a-second trace of what the sound is fed (grep "engine-sound" + "trace"): a jumpy speed or frame time shows here
    float _trT, _trLastKmh, _trMaxStep, _trMaxDt, _trMinKmh, _trMaxKmh, _trMaxOff;
    int _trFrames, _trFlips;
    static readonly string[] RoleNames = { "background", "idle", "accel", "brake", "top" };

    public VehicleEngineSound(string name) => _name = name;

    /// <summary>One line in the game log: grep "engine-sound" to see what the engine sound logic decided and what the audio system did with it.</summary>
    void Log(string msg) => GD.Print($"engine-sound[{_name}] t={Time.GetTicksMsec() / 1000.0:F2} {msg}");

    public void Update(VehicleSoundsDef def, float kmh, float topKmh, Vector3 at, float dt)
    {
        _trMaxStep = Math.Max(_trMaxStep, Math.Abs(kmh - _trLastKmh)); _trLastKmh = kmh;
        _trMaxDt = Math.Max(_trMaxDt, dt); _trMaxOff = Math.Max(_trMaxOff, Sfx.ListenerDistance(at)); _trFrames++;
        if (_trFrames == 1) { _trMinKmh = _trMaxKmh = kmh; } else { _trMinKmh = Math.Min(_trMinKmh, kmh); _trMaxKmh = Math.Max(_trMaxKmh, kmh); }
        _model.Step(kmh, topKmh, dt);
        var lift = _model.TakeLift();
        if (lift) _mix.Duck();
        if (_model.Phase != _phase)
        {
            Log($"{_phase}->{_model.Phase} kmh={kmh:F0} rate={_model.Rate:F1}km/h/s{(_model.LiftNote is { } n ? " | " + n : "")}");
            _phase = _model.Phase; _trFlips++;
        }
        var frac = topKmh > 0 ? Math.Clamp(Math.Abs(kmh) / topKmh, 0f, 1.2f) : 0f;
        _mix.Step(def, _model.Phase, frac, dt);
        var loops = def.Loops;
        for (var i = 0; i < EngineMix.Count; i++)
        {
            var layer = loops[i];
            var gain = _mix.Gain[i];
            if (layer == null || gain <= 0.001f)
            {
                if (_voices[i] != null) { _voices[i]!.Release(); _voices[i] = null; Log($"{RoleNames[i]} loop off (gain {gain:F3})"); }
                continue;
            }
            if (_voices[i] == null)
            {
                _voices[i] = Sfx.Loop(layer.Sound, at, layer.StartBySpeed ? Math.Min(frac, 1f) : 0f);
                Log($"{RoleNames[i]} loop on ({layer.Sound}): {(_voices[i] != null ? "voice taken" : "NO VOICE (pool full or stream missing)")}, gain {gain:F2}, pitch {layer.PitchAt(frac):F2}");
            }
            _voices[i]?.Update(at, layer.PitchAt(frac), EngineMix.Db(gain), exact: true);
        }
        if ((_trT += dt) >= 1f)
        {
            Log($"trace {_phase} kmh {_trMinKmh:F1}..{_trMaxKmh:F1} biggest frame step {_trMaxStep:F2} km/h, rate {_model.Rate:F1} km/h/s, {_trFrames} frames, longest {_trMaxDt * 1000:F0} ms, phase changes {_trFlips}, source up to {_trMaxOff:F2} m from the listener, gains bg={_mix.Gain[0]:F2} idle={_mix.Gain[1]:F2} accel={_mix.Gain[2]:F2} top={_mix.Gain[4]:F2}, pitch bg={def.Background?.PitchAt(frac):F2} accel={def.Accel?.PitchAt(frac):F2} top={def.Top?.PitchAt(frac):F2}, loop voices in use {Sfx.LoopVoicesInUse}");
            _trT = 0; _trFrames = 0; _trFlips = 0; _trMaxStep = _trMaxDt = _trMaxOff = 0;
        }
        if (lift)
        {
            var played = def.Lift is { } l && Sfx.Play3D(l.Sound, at, exact: true);
            Log($"LIFT kmh={kmh:F0} ({_model.LiftNote}) -> {(def.Lift == null ? "this type has no lift sound" : played ? "played" : "NOT played")}: {Sfx.LastNote}; loops ducked to {EngineMix.LiftDuckGain * 100:F0}%, mix now bg={_mix.Gain[0]:F2} idle={_mix.Gain[1]:F2} accel={_mix.Gain[2]:F2} top={_mix.Gain[4]:F2}");
        }
    }

    /// <summary>Stops everything and forgets the speed history (the driver got out, the vehicle is far away or gone).</summary>
    public void Release()
    {
        if (_voices.Any(v => v != null)) Log("released (driver out, far away or gone)");
        for (var i = 0; i < _voices.Length; i++) { _voices[i]?.Release(); _voices[i] = null; }
        _mix.Clear();
        _model.Reset();
        _phase = EnginePhase.Idle;
    }
}
