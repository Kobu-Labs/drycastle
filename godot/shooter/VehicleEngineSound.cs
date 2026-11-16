using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

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

    public void Update(VehicleSoundsDef def, float kmh, float topKmh, Vector3 at, float dt)
    {
        _model.Step(kmh, topKmh, dt);
        var lift = _model.TakeLift();
        if (lift) _mix.Duck();
        var frac = topKmh > 0 ? Math.Clamp(Math.Abs(kmh) / topKmh, 0f, 1.2f) : 0f;
        _mix.Step(def, _model.Phase, frac, dt);
        var loops = def.Loops;
        for (var i = 0; i < EngineMix.Count; i++)
        {
            var layer = loops[i];
            var gain = _mix.Gain[i];
            if (layer == null || gain <= 0.001f)
            {
                if (_voices[i] != null) { _voices[i]!.Release(); _voices[i] = null; }
                continue;
            }
            _voices[i] ??= Sfx.Loop(layer.Sound, at, layer.StartBySpeed ? Math.Min(frac, 1f) : 0f);
            _voices[i]?.Update(at, layer.PitchAt(frac), EngineMix.Db(gain));
        }
        if (lift && def.Lift is { } l) Sfx.Play3D(l.Sound, at);
    }

    /// <summary>Stops everything and forgets the speed history (the driver got out, the vehicle is far away or gone).</summary>
    public void Release()
    {
        for (var i = 0; i < _voices.Length; i++) { _voices[i]?.Release(); _voices[i] = null; }
        _mix.Clear();
        _model.Reset();
    }
}
