using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Weapons;
using System.Diagnostics;


namespace Drycastle.Shooter.Net.Server.VillageRadio;

/// <summary>
/// The rozhlas live feed on the server: <see cref="RozhlasFeed"/> turns what is written into it (the set holder's voice, the combat sounds) into the degraded, 2 s delayed Opus stream, and this class broadcasts that
/// to every client on <see cref="Wire.ChanRozhlasFeed"/>. The feed runs exactly while the mic is live: it starts with a detour broadcast (OnAir), runs through a hijack, and drains when the loudspeaker goes Idle.
/// Optional: with no rozhlas_voice.json or no ffmpeg the feed is silent (logged), the game goes on.
/// <para>
/// The combat mix: while the mic is live every Shot, PelletFan, Launch, Explosion, VehicleExplosion and Death whose position is inside the Urad volume is mixed into the feed as the dry sample of the sound the client
/// plays for it (<see cref="RozhlasCombat"/>), whole building, no falloff. The dedicated server plays no audio, so <see cref="RozhlasSampleBank"/> decodes the .ogg files of sounds.json with ffmpeg to mono 16 kHz in the
/// background, once per file, at the first rozhlas tick; a sound that is not ready or cannot be decoded is simply not rebroadcast (one log line). The hijack's own sounds (mic thump, feedback squeal; the
/// announcer's scream is her death sound) and the click that ends it are in <see cref="OnPhase"/>. <c>/rozhlas feedtest</c> and <c>/rozhlas combattest</c> push test signals through the same paths.
/// </para>
/// </summary>
internal sealed class RozhlasFeedServer : IDisposable
{
    const double FeedTestVoiceS = 4.0, FeedTestShotS = 4.5, FeedTestEndS = 5.5;
    static readonly Stopwatch CombatClock = Stopwatch.StartNew();

    sealed class FeedTest { public readonly Stopwatch Clock = Stopwatch.StartNew(); public int Written; public bool Shot; public short[] Voice = RozhlasFeedTestSignal.Voice(FeedTestVoiceS); public string Who = ""; public bool OwnsFeed; }

    sealed class CombatTest
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public int Next;
        public bool OwnsFeed;
        public static readonly (double At, CombatKind Kind, string Weapon)[] Cues = { (0.0, CombatKind.Shot, "ak47"), (0.8, CombatKind.Explosion, "rpg"), (1.8, CombatKind.Death, "") };
        public const double EndS = 4.0;
    }

    readonly ServerContext _ctx;
    readonly RozhlasServer _owner;
    readonly UradServer _urad;
    readonly WeaponsFile? _weapons;

    LazyInit<RozhlasFeed> _feed;
    RozhlasCombat? _combat;
    RozhlasSampleBank? _combatBank;
    bool _combatTried;
    FeedTest? _feedTest;
    CombatTest? _combatTest;

    public RozhlasFeedServer(ServerContext ctx, RozhlasServer owner, UradServer urad, WeaponsFile? weapons)
    {
        _ctx = ctx; _owner = owner; _urad = urad; _weapons = weapons;
    }

    /// <summary>Whether the feed has been made (used by <see cref="Write"/> to ignore a mic frame before that).</summary>
    public RozhlasFeed? MadeFeed => _feed.Value;

    /// <summary>The live feed, made on the first call (null when rozhlas_voice.json cannot be read, logged once). It starts when the mic goes live and stops when it is dead.</summary>
    public RozhlasFeed? EnsureFeed()
    {
        if (!_feed.Begin()) return _feed.Value;
        var json = Godot.FileAccess.Open("res://data/rozhlas_voice.json", Godot.FileAccess.ModeFlags.Read)?.GetAsText();
        if (json == null) { GD.PrintErr($"server: rozhlas_voice.json: cannot open ({Godot.FileAccess.GetOpenError()}); no live rozhlas feed"); return null; }
        RozhlasVoice voice;
        try { voice = RozhlasVoice.Parse(json); }
        catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; no live rozhlas feed"); return null; }
        var feed = new RozhlasFeed(voice, Broadcast, m => GD.Print($"server: {m}"));
        _feed.Ready(feed);
        _owner.PhaseChanged += OnPhase;
        return feed;
    }

    /// <summary>ffmpeg must not outlive the server.</summary>
    public void Dispose() => _feed.Value?.Dispose();

    /// <summary>The loudspeaker changed phase. The hijack's thump and feedback and the end click are mixed in while the feed still takes input; the feed starts and stops with the mic.</summary>
    void OnPhase(RozhlasPhase from, RozhlasPhase to)
    {
        if (_feed.Value is not { } feed) return;
        if (to == RozhlasPhase.Hijacked) { feed.Mix(RozhlasSynth.Thump); feed.Mix(RozhlasSynth.Squeal); } // the announcer is dead: the mic thumps and the feedback squeals over the megaphones
        if (from == RozhlasPhase.Hijacked && to == RozhlasPhase.Idle) feed.Mix(RozhlasSynth.Click, 1f); // a normal end has no click
        if (from == RozhlasPhase.Idle) feed.Start(); // already Live under a running feedtest: the phase takes it over
        else if (to == RozhlasPhase.Idle) feed.Stop();
        if (_feedTest is { } t) t.OwnsFeed = false;
    }

    /// <summary>One feed frame to every joined player (unreliable on its own channel; bots have no connection).</summary>
    void Broadcast(byte[] packet)
    {
        foreach (var c in _ctx.Connections)
            if (c.Joined) _ctx.SendUnreliable(c, Wire.ChanRozhlasFeed, packet);
    }

    /// <summary>Every frame: the frames that are due go out, and a running /rozhlas feedtest is fed.</summary>
    public void Pump()
    {
        if (_feed.Value == null) return;
        FeedTestTick();
        _feed.Value.Pump();
    }

    /// <summary>Called once when the loudspeaker is made: reads sounds.json, starts decoding the samples in the background, hooks the workers' deaths.</summary>
    public void StartCombat()
    {
        if (_combatTried) return;
        _combatTried = true;
        var json = Godot.FileAccess.Open("res://data/sounds.json", Godot.FileAccess.ModeFlags.Read)?.GetAsText();
        if (json == null) { GD.PrintErr($"server: sounds.json: cannot open ({Godot.FileAccess.GetOpenError()}); no combat sounds on the rozhlas"); return; }
        SoundsFile table;
        try { table = SoundsFile.Parse(json); }
        catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; no combat sounds on the rozhlas"); return; }
        _combatBank = new RozhlasSampleBank(table, ModelLib.AssetPath, p => FfmpegPcm.Decode(p), m => GD.Print($"server: {m}"));
        _combatBank.Preload(RozhlasCombat.PreloadIds(table));
        _combat = new RozhlasCombat(_combatBank.Get, s => _feed.Value?.Mix(s), () => CombatClock.Elapsed.TotalSeconds);
        _urad.WorkerKilled += OnWorkerKilled;
    }

    string WeaponId(int index) => _weapons?.Weapons.ElementAtOrDefault(index)?.Id ?? "";

    /// <summary>Every broadcast event passes here: shots, launches and blasts that are inside the Urad while the mic is live are mixed in.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (_combat is not { } combat || !_owner.MicLive) return;
        switch (kind)
        {
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } s:
                combat.Event(CombatKind.Shot, WeaponId(s.Weapon), true, _urad.Contains(s.Origin.ToGodot())); break; // the muzzle, not where it hit
            case EventKind.PelletFan when WeaponCodec.DecodePelletFan(payload) is { } pf:
                combat.Event(CombatKind.Shot, WeaponId(pf.Weapon), true, _urad.Contains(pf.Origin.ToGodot())); break; // a shotgun blast is one sound, as the client plays it
            case EventKind.Launch when WeaponCodec.DecodeLaunch(payload) is { } l:
                combat.Event(CombatKind.Launch, WeaponId(l.Weapon), true, _urad.Contains(l.Origin.ToGodot())); break;
            case EventKind.Explosion when WeaponCodec.DecodeExplosion(payload) is { } ex:
                combat.Event(CombatKind.Explosion, WeaponId(ex.Weapon), true, _urad.Contains(ex.Position.ToGodot())); break;
            case EventKind.VehicleExplosion when WeaponCodec.DecodeVehicleExplosion(payload) is { } vx:
                combat.Event(CombatKind.VehicleExplosion, "", true, _urad.Contains(vx.Position.ToGodot())); break;
        }
    }

    /// <summary>A player died: the death sound, at the victim's position.</summary>
    public void OnPlayerDied(ushort victim)
    {
        if (_combat is not { } combat || !_owner.MicLive || !_ctx.Players.TryGetValue(victim, out var c) || c.Body == null) return;
        combat.Event(CombatKind.Death, "", true, _urad.Contains(c.Body.GlobalPosition));
    }

    /// <summary>A worker was killed: the death sound. The announcer is the building's own, so her scream is heard wherever the data puts her (the stand-in spots are outside the solid shell); the others count only inside the volume.</summary>
    void OnWorkerKilled(CitizenKilled k)
    {
        if (_combat is not { } combat || !_owner.MicLive) return;
        if (k.Role == CitizenRole.Announcer) combat.Play(RozhlasCombat.DeathId);
        else combat.Event(CombatKind.Death, "", true, _urad.Contains(k.Where.ToGodot()));
    }

    // ---- /rozhlas feedtest: a few seconds of synthetic voice, then a "gunshot", through the whole pipeline ----

    /// <summary>"/rozhlas feedtest": starts the feed, pushes 4 s of test voice (written in real time, like a mic) and a shot into it, stops it. Everyone hears it from the megaphones about 2 s later.</summary>
    public void ChatFeedTest(ServerClient c)
    {
        if (EnsureFeed() is not { } feed) { _ctx.ChatTo(c, ChatLineKind.Error, "The live feed has no data (rozhlas_voice.json could not be read)."); return; }
        if (_feedTest != null) { _ctx.ChatTo(c, ChatLineKind.Error, "A feed test is already running."); return; }
        var owns = !_owner.MicLive; // while the mic is live the phase owns the feed: the test only writes into it
        if (owns) feed.Start();
        if (feed.State != RozhlasFeedState.Live) { _ctx.ChatTo(c, ChatLineKind.Error, "The live feed is not running (ffmpeg missing? see the server log)."); if (owns) feed.Stop(); return; }
        _feedTest = new FeedTest { Who = c.Name, OwnsFeed = owns };
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} started the rozhlas feed test.");
    }

    void FeedTestTick()
    {
        if (_feedTest is not { } t || _feed.Value is not { } feed) return;
        if (feed.State != RozhlasFeedState.Live) { _feedTest = null; return; } // it failed (logged by the feed)
        var el = t.Clock.Elapsed.TotalSeconds;
        var want = (int)(Math.Min(el, FeedTestVoiceS) * RozhlasVoice.FeedRate);
        if (want > t.Written) { feed.Write(t.Voice.AsSpan(t.Written, want - t.Written)); t.Written = want; }
        if (!t.Shot && el >= FeedTestShotS) { feed.Mix(RozhlasFeedTestSignal.Shot()); t.Shot = true; }
        if (el >= FeedTestEndS) { if (t.OwnsFeed) feed.Stop(); _feedTest = null; }
    }

    // ---- /rozhlas combattest: one shot, one explosion, one death, regardless of position ----

    /// <summary>"/rozhlas combattest": starts the feed when the mic is not live, then an ak47 shot, an rpg blast and a death go through the combat path (position and phase filters skipped). Everyone hears them about 2 s later.</summary>
    public void ChatCombatTest(ServerClient c)
    {
        if (EnsureFeed() is not { } feed || _owner.Loudspeaker == null || _combat == null || _combatBank == null) { _ctx.ChatTo(c, ChatLineKind.Error, "The combat sounds have no data (sounds.json or rozhlas_voice.json could not be read)."); return; }
        if (_combatTest != null || _feedTest != null) { _ctx.ChatTo(c, ChatLineKind.Error, "A feed test is already running."); return; }
        var owns = !_owner.MicLive;
        if (owns) feed.Start();
        if (feed.State != RozhlasFeedState.Live) { _ctx.ChatTo(c, ChatLineKind.Error, "The live feed is not running (ffmpeg missing? see the server log)."); if (owns) feed.Stop(); return; }
        var ready = CombatTest.Cues.Count(q => _combatBank.Get(RozhlasCombat.SoundId(q.Kind, q.Weapon)) != null);
        if (ready < CombatTest.Cues.Length) _ctx.ChatTo(c, ChatLineKind.Info, $"Only {ready} of {CombatTest.Cues.Length} test sounds are decoded yet ({_combatBank.Loaded} ready, {_combatBank.Failed} failed, {_combatBank.Pending} loading; see the server log).");
        _combatTest = new CombatTest { OwnsFeed = owns };
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} started the rozhlas combat sound test.");
    }

    /// <summary>Every tick: a running combat test is fed.</summary>
    public void Tick()
    {
        if (_combatTest is not { } t || _feed.Value is not { } feed || _combat is not { } combat) return;
        if (feed.State != RozhlasFeedState.Live) { _combatTest = null; return; } // it failed (logged by the feed)
        var el = t.Clock.Elapsed.TotalSeconds;
        while (t.Next < CombatTest.Cues.Length && el >= CombatTest.Cues[t.Next].At)
        {
            var q = CombatTest.Cues[t.Next++];
            combat.Play(RozhlasCombat.SoundId(q.Kind, q.Weapon));
        }
        if (el >= CombatTest.EndS) { if (t.OwnsFeed && !_owner.MicLive) feed.Stop(); _combatTest = null; }
    }
}
