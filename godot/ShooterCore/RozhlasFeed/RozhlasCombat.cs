using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.RozhlasFeed;

/// <summary>What happened in the world that the live feed may rebroadcast.</summary>
public enum CombatKind { Shot, Launch, Explosion, VehicleExplosion, Death }

/// <summary>
/// The combat mix of the rozhlas live feed (docs/events/README.md, "Live feed"): which game events become which sound, and whether they reach the feed. Godot-free.
/// The ids are exactly what the client plays for the same event (ClientSound.cs), so a gunshot on the megaphones is the gunshot the player heard:
/// <c>shot.&lt;weapon&gt;</c> for a Shot or PelletFan, <c>launch.&lt;weapon&gt;</c> for a Launch, <c>explosion.&lt;weapon&gt;</c> for an Explosion, <c>explosion.vehicle</c> for a vehicle's blast, <c>death</c>.
/// An event is mixed only while the mic is live and its position is inside the Urad volume (the caller evaluates both and passes them in); there is no distance falloff and the position is used for nothing else.
/// The sound is the dry sample of the id (<see cref="RozhlasSampleBank"/>: first file, the table's volume and pitch, never a random pitch), added on top of the feed's input.
/// Footsteps, jumps, reloads, weapon swaps and pickups are not here because they never are events on the wire.
/// <para>Flood guard: the same sound id is admitted at most every <see cref="MinGapS"/> (a crowd of rifles in one block would only add the same waveform louder), and the mixer holds at most
/// <see cref="RozhlasFeedMixer.DefaultMaxShots"/> one-shots at once (the oldest is replaced), so automatic fire stays audible at its own rate and cannot pile up.</para>
/// </summary>
public sealed class RozhlasCombat
{
    /// <summary>The sound of a player's or worker's death.</summary>
    public const string DeathId = "death";
    /// <summary>The blast of a vehicle (<c>VehicleExplosion</c>), as the client plays it.</summary>
    public const string VehicleExplosionId = "explosion.vehicle";
    /// <summary>The same sound id is admitted at most this often (seconds).</summary>
    public const double MinGapS = 0.04;

    readonly Func<string, short[]?> _sample;
    readonly Action<short[]> _mix;
    readonly Func<double> _now;
    readonly Dictionary<string, double> _last = new();

    /// <param name="sample">The dry sample of a sound id, or null when it is not (yet) available: that sound is not rebroadcast. Must not block.</param>
    /// <param name="mix">Puts a sample into the feed (game thread).</param>
    /// <param name="now">Seconds on any monotonic clock.</param>
    public RozhlasCombat(Func<string, short[]?> sample, Action<short[]> mix, Func<double> now)
    {
        _sample = sample; _mix = mix; _now = now;
    }

    /// <summary>Events mixed into the feed, and events that qualified but were not (no sample yet, or the per-id gap).</summary>
    public int Mixed { get; private set; }
    public int Dropped { get; private set; }

    /// <summary>The sound id of an event, or null for a kind with none. <paramref name="weaponId"/> is the weapon's id in weapons.json ("" for a special weapon byte, which has no per-weapon sound).</summary>
    public static string SoundId(CombatKind kind, string weaponId) => kind switch
    {
        CombatKind.Shot => SoundRules.Event("shot", weaponId),
        CombatKind.Launch => SoundRules.Event("launch", weaponId),
        CombatKind.Explosion => SoundRules.Event("explosion", weaponId),
        CombatKind.VehicleExplosion => VehicleExplosionId,
        CombatKind.Death => DeathId,
        _ => null!,
    };

    /// <summary>The filter: only while the mic is live, and only inside the Urad.</summary>
    public static bool Qualifies(bool micLive, bool inside) => micLive && inside;

    /// <summary>The ids worth decoding ahead of time: every shot, launch and explosion sound of the table, and the death sound.</summary>
    public static IEnumerable<string> PreloadIds(SoundsFile table) =>
        table.Sounds.Select(s => s.Id).Where(id => id.StartsWith("shot.") || id.StartsWith("launch.") || id.StartsWith("explosion.") || id == DeathId);

    /// <summary>An event happened. True when its sound went into the feed.</summary>
    public bool Event(CombatKind kind, string weaponId, bool micLive, bool inside) =>
        Qualifies(micLive, inside) && Play(SoundId(kind, weaponId));

    /// <summary>A sound id straight into the feed, past the live/inside filter (the admin's test, the announcer's own death).</summary>
    public bool Play(string id)
    {
        var now = _now();
        if (_last.TryGetValue(id, out var at) && now - at < MinGapS) { Dropped++; return false; }
        if (_sample(id) is not { } pcm || pcm.Length == 0) { Dropped++; return false; }
        _last[id] = now;
        _mix(pcm);
        Mixed++;
        return true;
    }
}

/// <summary>
/// The server's PCM for the sounds it rebroadcasts: mono 16 kHz, one dry sample per sound id (first file of the id, the table's <c>volume_db</c> baked in, the table's base <c>pitch</c> applied by resampling,
/// never a random pitch), decoded off the game thread (<paramref name="decode"/>: the .ogg to PCM, in production <see cref="FfmpegPcm"/>). Loading starts with <see cref="Preload"/> or on the first
/// <see cref="Get"/> of an id; <see cref="Get"/> never blocks and returns null until the sample is ready. A file that cannot be decoded is logged ONCE and its sounds are simply not rebroadcast. Thread-safe.
/// </summary>
public sealed class RozhlasSampleBank
{
    /// <summary>A sample is cut to this long (an explosion tail is not worth more).</summary>
    public const double MaxSampleS = 2.5;

    readonly SoundsFile _table;
    readonly Func<string, string> _resolve;
    readonly Func<string, short[]> _decode;
    readonly Action<string> _log;
    readonly SemaphoreSlim _gate;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<short[]?>> _files = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, short[]?> _ready = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _requested = new();
    int _pending;

    /// <param name="resolve">sounds.json file name to an absolute path.</param>
    /// <param name="decode">An audio file to mono 16 kHz PCM; throws when it cannot.</param>
    /// <param name="parallel">How many decodes (processes) run at once.</param>
    public RozhlasSampleBank(SoundsFile table, Func<string, string> resolve, Func<string, short[]> decode, Action<string> log, int parallel = 2)
    {
        _table = table; _resolve = resolve; _decode = decode; _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, parallel));
    }

    /// <summary>Samples ready to play.</summary>
    public int Loaded => _ready.Values.Count(v => v != null);
    /// <summary>Sounds that failed (not in the table, or the file did not decode).</summary>
    public int Failed => _ready.Values.Count(v => v == null);
    /// <summary>Loads still running or queued.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Starts decoding these ids in the background and returns at once.</summary>
    public void Preload(IEnumerable<string> ids) { foreach (var id in ids) Request(id); }

    /// <summary>The sample, or null when it is not ready (a load is started if this is the first ask) or cannot exist.</summary>
    public short[]? Get(string id)
    {
        if (_ready.TryGetValue(id, out var pcm)) return pcm;
        Request(id);
        return null;
    }

    void Request(string id)
    {
        if (_ready.ContainsKey(id) || !_requested.TryAdd(id, 0)) return;
        Interlocked.Increment(ref _pending);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _ready[id] = Build(id); }
            catch (Exception e) { _log($"rozhlas combat: {id}: {e.Message}"); _ready[id] = null; }
            finally { Interlocked.Decrement(ref _pending); }
        });
    }

    short[]? Build(string id)
    {
        var def = _table.Find(id);
        if (def == null || def.Files.Count == 0) { _log($"rozhlas combat: no sound '{id}' in sounds.json; not rebroadcast"); return null; }
        var file = def.Files[0]; // one dry sample per id: the first variant
        var raw = _files.GetOrAdd(file, f => new Lazy<short[]?>(() => LoadFile(f))).Value;
        if (raw == null) return null;
        var pcm = def.Pitch is > 0 and not 1f ? RozhlasFeedMixer.Resample(raw, (int)(RozhlasFeedMixer.Rate * def.Pitch)) : raw;
        var gain = MathF.Pow(10f, def.VolumeDb / 20f);
        var cut = Math.Min(pcm.Length, (int)(MaxSampleS * RozhlasFeedMixer.Rate));
        var o = new short[cut];
        for (var i = 0; i < cut; i++) o[i] = (short)Math.Clamp((int)MathF.Round(pcm[i] * gain), short.MinValue, short.MaxValue);
        return o;
    }

    short[]? LoadFile(string file)
    {
        _gate.Wait();
        try
        {
            var pcm = _decode(_resolve(file));
            if (pcm.Length == 0) throw new InvalidOperationException("decoded to nothing");
            return pcm;
        }
        catch (Exception e)
        {
            _log($"rozhlas combat: cannot decode {file}: {e.Message}. Sounds of this file are not rebroadcast.");
            return null;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Decodes an audio file to mono 16 kHz 16-bit PCM with the ffmpeg executable (already a server runtime dependency for the live feed): <c>ffmpeg -i file -ac 1 -ar 16000 -f s16le -</c>.</summary>
public static class FfmpegPcm
{
    /// <summary>The executable: $DRYCASTLE_FFMPEG, else "ffmpeg" from the PATH (as the live feed).</summary>
    public static string Executable => Environment.GetEnvironmentVariable("DRYCASTLE_FFMPEG") ?? "ffmpeg";

    /// <summary>Decodes <paramref name="path"/>; throws with ffmpeg's own words when it cannot, or after <paramref name="timeoutS"/>.</summary>
    public static short[] Decode(string path, string? exe = null, double timeoutS = 20, double maxSeconds = RozhlasSampleBank.MaxSampleS * 4)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe ?? Executable)
        {
            RedirectStandardInput = false, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", path, "-vn", "-t", maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", "1", "-ar", RozhlasFeedMixer.Rate.ToString(), "-f", "s16le", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var err = p.StandardError.ReadToEndAsync();
        var ms = new MemoryStream();
        // Read on this thread until ffmpeg closes its end: an async copy can sit unscheduled for seconds while the server loads the map (a busy thread pool),
        // and the sample then came back empty. The timer is the timeout: killing ffmpeg ends the read.
        var timedOut = false;
        using (new System.Threading.Timer(_ => { timedOut = true; try { p.Kill(true); } catch { } }, null, (int)(timeoutS * 1000), System.Threading.Timeout.Infinite))
            p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (timedOut) throw new TimeoutException($"ffmpeg took longer than {timeoutS:0} s");
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exit code {p.ExitCode}: {err.Result.Trim().Replace('\n', ' ')}");
        var bytes = ms.GetBuffer();
        var n = (int)ms.Length / 2;
        var pcm = new short[n];
        Buffer.BlockCopy(bytes, 0, pcm, 0, n * 2);
        return pcm;
    }
}

/// <summary>
/// The sounds that are made in code (no asset files): the mic thump and the feedback squeal of the announcer's death, and the click at the end of the hijack. Deterministic: the same samples every run.
/// The live chain high-passes at 500 Hz and low-passes at 2.2 kHz, so the thump carries a broadband burst and a mid body on top of its low sine, and the squeal stays inside the pass band.
/// 16 kHz mono 16-bit, peak below full scale.
/// </summary>
public static class RozhlasSynth
{
    const int Rate = RozhlasFeedMixer.Rate;

    static readonly Lazy<short[]> _thump = new(() => MakeThump()), _squeal = new(() => MakeSqueal()), _click = new(() => MakeClick());

    /// <summary>A hand knocking the microphone: ~0.4 s.</summary>
    public static short[] Thump => _thump.Value;
    /// <summary>Feedback: a sine that rises (900 to 2000 Hz) with a little wobble and ~1 s long, harmonics on top, fading out.</summary>
    public static short[] Squeal => _squeal.Value;
    /// <summary>The cut at the end of the hijack: a ~10 ms pop.</summary>
    public static short[] Click => _click.Value;

    static short[] MakeThump()
    {
        var v = new double[(int)(0.4 * Rate)];
        uint x = 0xA5A5A5A5u;
        double phase = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var t = i / (double)Rate;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            var noise = (int)x / (double)int.MaxValue;
            phase += 2 * Math.PI * (45 + 60 * Math.Exp(-t * 20)) / Rate; // 105 Hz falling to 45
            v[i] = 0.55 * Math.Sin(phase) * Math.Exp(-t * 9) // the body
                 + 0.45 * noise * Math.Exp(-t * 55) // the knock: broadband, gone in ~40 ms
                 + 0.35 * Math.Sin(2 * Math.PI * 700 * t) * Math.Exp(-t * 60); // a mid tap that survives the high-pass
        }
        return Scaled(v, 0.85);
    }

    static short[] MakeSqueal()
    {
        var v = new double[(int)(1.0 * Rate)];
        double phase = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var t = i / (double)Rate;
            var f = 900 + 1100 * (t * t * 0.6 + t * 0.4) + 25 * Math.Sin(2 * Math.PI * 7 * t); // rising, accelerating, a wobble
            phase += 2 * Math.PI * f / Rate;
            var env = Math.Min(1.0, t / 0.08) * (t > 0.8 ? 1 - (t - 0.8) / 0.2 : 1.0); // 80 ms attack, 200 ms release
            v[i] = (Math.Sin(phase) + 0.25 * Math.Sin(2 * phase)) * env;
        }
        return Scaled(v, 0.6);
    }

    static short[] MakeClick()
    {
        var v = new double[(int)(0.012 * Rate)];
        uint x = 0x1234567u;
        for (var i = 0; i < v.Length; i++)
        {
            var t = i / (double)Rate;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            v[i] = ((int)x / (double)int.MaxValue) * Math.Exp(-t * 350) + (i == 0 ? 0.8 : 0);
        }
        return Scaled(v, 0.85);
    }

    /// <summary>The samples scaled so that the loudest is <paramref name="peak"/> of full scale.</summary>
    static short[] Scaled(double[] v, double peak)
    {
        var max = v.Max(Math.Abs);
        var k = max > 0 ? peak * short.MaxValue / max : 0;
        var o = new short[v.Length];
        for (var i = 0; i < o.Length; i++) o[i] = (short)Math.Round(v[i] * k);
        return o;
    }
}
