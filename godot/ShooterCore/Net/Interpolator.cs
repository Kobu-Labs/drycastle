using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Buffer of timestamped samples of one remote entity, sampled at a render tick slightly in the past.</summary>
public sealed class Interpolator
{
    public readonly record struct Sample(uint Tick, Vector3 Pos, float Yaw, float Pitch);

    readonly List<Sample> _buf = new();
    const int MaxSamples = 32;

    public int Count => _buf.Count;
    public uint NewestTick => _buf.Count == 0 ? 0 : _buf[^1].Tick;

    public void Push(uint tick, Vector3 pos, float yaw, float pitch)
    {
        if (_buf.Count > 0 && tick < _buf[^1].Tick) return;
        if (_buf.Count > 0 && tick == _buf[^1].Tick) { _buf[^1] = new Sample(tick, pos, yaw, pitch); return; }
        _buf.Add(new Sample(tick, pos, yaw, pitch));
        if (_buf.Count > MaxSamples) _buf.RemoveAt(0);
    }

    static float LerpAngle(float a, float b, float t)
    {
        var d = (b - a) % Quant.TwoPi;
        if (d > MathF.PI) d -= Quant.TwoPi;
        else if (d < -MathF.PI) d += Quant.TwoPi;
        return a + d * t;
    }

    /// <summary>Interpolated state at a fractional tick; clamped to the buffered range (no extrapolation).</summary>
    public Sample? At(double tick)
    {
        if (_buf.Count == 0) return null;
        if (tick <= _buf[0].Tick) return _buf[0];
        if (tick >= _buf[^1].Tick) return _buf[^1];
        for (var i = _buf.Count - 1; i > 0; i--)
        {
            var a = _buf[i - 1]; var b = _buf[i];
            if (tick < a.Tick) continue;
            var t = (float)((tick - a.Tick) / (b.Tick - a.Tick));
            return new Sample((uint)tick, Vector3.Lerp(a.Pos, b.Pos, t), LerpAngle(a.Yaw, b.Yaw, t), a.Pitch + (b.Pitch - a.Pitch) * t);
        }
        return _buf[^1];
    }
}
