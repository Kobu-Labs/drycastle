using Godot;
using Drycastle.ShooterCore.RozhlasFeed;

namespace Drycastle.Shooter;

/// <summary>
/// The set holder's microphone (slice 5), client only. The OS default input device is read the usual Godot 4 way: an <see cref="AudioStreamMicrophone"/> played on a bus that is muted
/// (so nothing is heard locally: no monitoring) and carries an <see cref="AudioEffectCapture"/>, which keeps the raw frames for us to read. Needs <c>audio/driver/enable_input</c> in
/// project.godot. No device picker, no settings UI: whatever the OS calls the default.
/// <para>
/// <see cref="Open"/> while the local player holds the set, <see cref="Close"/> otherwise: the device is not open for anyone who does not hold it. While open, <see cref="Pump"/> drains the
/// capture buffer every frame; the frames are encoded and sent (<see cref="RozhlasMicCapture"/>) only on frames where the caller says the player transmits, and discarded otherwise,
/// so a mic key pressed late never sends what was said before it.
/// </para>
/// </summary>
public partial class RozhlasMicInput : Node
{
    const string BusName = "RozhlasMic";

    readonly Action<byte[]> _send;
    AudioEffectCapture? _capture;
    AudioStreamPlayer? _player;
    RozhlasMicCapture? _enc;
    float[] _mono = new float[1024];
    bool _wasTransmitting;
    /// <summary>The capture bus could not be set up: said once, not tried again (the caller asks every frame).</summary>
    bool _broken;

    /// <param name="send">Gets each finished upstream packet (<see cref="RozhlasMicWire"/>), already framed for the wire.</param>
    public RozhlasMicInput(Action<byte[]> send) => _send = send;

    public bool IsOpen => _player != null;

    /// <summary>Starts reading the default input device. Does nothing when audio is off (headless) or it is already open.</summary>
    public void Open()
    {
        if (IsOpen || _broken || !Sfx.Enabled) return;
        var bus = AudioServer.GetBusIndex(BusName);
        if (bus < 0)
        {
            AudioServer.AddBus();
            bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, BusName);
            AudioServer.SetBusMute(bus, true); // the capture effect still sees the frames; nothing reaches the speakers (the mic_record demo of Godot does the same)
            _capture = new AudioEffectCapture { BufferLength = 0.25f };
            AudioServer.AddBusEffect(bus, _capture);
        }
        else _capture = AudioServer.GetBusEffect(bus, 0) as AudioEffectCapture;
        if (_capture == null) { _broken = true; GD.PrintErr("client: rozhlas mic: the capture bus is broken; no mic until the game restarts"); return; }
        _capture.ClearBuffer();
        _enc = new RozhlasMicCapture((int)Mathf.Round(AudioServer.GetMixRate()), _send);
        _player = new AudioStreamPlayer { Stream = new AudioStreamMicrophone(), Bus = BusName };
        AddChild(_player);
        _player.Play();
        _wasTransmitting = false;
    }

    /// <summary>Stops reading the device and forgets everything captured.</summary>
    public void Close()
    {
        if (_player != null) { _player.Stop(); _player.QueueFree(); _player = null; }
        _capture?.ClearBuffer();
        _enc?.Reset();
        _enc = null; _wasTransmitting = false;
    }

    /// <summary>Once a frame while open: drains the capture buffer, and sends it when <paramref name="transmit"/>, drops it otherwise.</summary>
    public void Pump(bool transmit)
    {
        if (_capture == null || _enc == null || _player == null) return;
        var n = _capture.GetFramesAvailable();
        if (n <= 0) return;
        var frames = _capture.GetBuffer(n);
        if (!transmit)
        {
            if (_wasTransmitting) { _enc.Reset(); _wasTransmitting = false; } // the key was let go: the partial 20 ms frame is dropped
            return;
        }
        _wasTransmitting = true;
        if (_mono.Length < frames.Length) _mono = new float[frames.Length];
        for (var i = 0; i < frames.Length; i++) _mono[i] = (frames[i].X + frames[i].Y) * 0.5f;
        _enc.Push(_mono.AsSpan(0, frames.Length));
    }

    public override void _ExitTree() => Close();
}
