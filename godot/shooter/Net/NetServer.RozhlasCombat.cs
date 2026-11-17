using System.Diagnostics;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The combat mix of the rozhlas live feed on the server (slice 4): while the mic is live (OnAir or Hijacked) every Shot, PelletFan, Launch, Explosion, VehicleExplosion and Death whose position
/// is inside the Urad volume is mixed into <see cref="RozhlasLiveFeed"/> as the dry sample of the sound the client plays for it (<see cref="RozhlasCombat"/>), whole building, no falloff.
/// <para>
/// PCM: the dedicated server plays no audio, so <see cref="RozhlasSampleBank"/> decodes the .ogg files of sounds.json with ffmpeg (already a runtime dependency of the feed) to mono 16 kHz in the
/// background, once per file, at the first rozhlas tick; nothing on the game thread waits for it, and a sound that is not ready or cannot be decoded is simply not rebroadcast (one log line).
/// </para>
/// <para>
/// Hooks: <see cref="RozhlasCombatEvent"/> from <see cref="BroadcastEvent"/> (every shot, launch and blast event passes there), <see cref="RozhlasCombatDeath"/> from the PlayerDied handler,
/// <see cref="UradWorkerKilled"/> for the workers. The hijack's own sounds (<see cref="RozhlasHijackCut"/>: mic thump, feedback squeal; the announcer's scream is her death sound)
/// and the click that ends it are in <see cref="RozhlasCombatPhase"/>. <c>/rozhlas combattest</c> pushes one of each kind through the same path.
/// </para>
/// </summary>
public partial class NetServer
{
    RozhlasCombat? _combat;
    RozhlasSampleBank? _combatBank;
    bool _combatTried;
    CombatTest? _combatTest;
    static readonly Stopwatch CombatClock = Stopwatch.StartNew();

    /// <summary>Called once from <see cref="EnsureRozhlas"/>: reads sounds.json, starts decoding the samples in the background, hooks the workers' deaths.</summary>
    void EnsureRozhlasCombat()
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
        _combat = new RozhlasCombat(_combatBank.Get, s => _liveFeed.Value?.Mix(s), () => CombatClock.Elapsed.TotalSeconds);
        UradWorkerKilled += OnUradWorkerKilledForFeed;
    }

    string CombatWeaponId(int index) => Weapons?.Weapons.ElementAtOrDefault(index)?.Id ?? "";

    /// <summary>Every broadcast event passes here (one line in <see cref="BroadcastEvent"/>): shots, launches and blasts that are inside the Urad while the mic is live are mixed in.</summary>
    void RozhlasCombatEvent(EventKind kind, byte[] payload)
    {
        if (_combat is not { } combat || !MicLive) return;
        switch (kind)
        {
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } s:
                combat.Event(CombatKind.Shot, CombatWeaponId(s.Weapon), true, UradContains(V(s.Origin))); break; // the muzzle, not where it hit
            case EventKind.PelletFan when WeaponCodec.DecodePelletFan(payload) is { } pf:
                combat.Event(CombatKind.Shot, CombatWeaponId(pf.Weapon), true, UradContains(V(pf.Origin))); break; // a shotgun blast is one sound, as the client plays it
            case EventKind.Launch when WeaponCodec.DecodeLaunch(payload) is { } l:
                combat.Event(CombatKind.Launch, CombatWeaponId(l.Weapon), true, UradContains(V(l.Origin))); break;
            case EventKind.Explosion when WeaponCodec.DecodeExplosion(payload) is { } ex:
                combat.Event(CombatKind.Explosion, CombatWeaponId(ex.Weapon), true, UradContains(V(ex.Position))); break;
            case EventKind.VehicleExplosion when WeaponCodec.DecodeVehicleExplosion(payload) is { } vx:
                combat.Event(CombatKind.VehicleExplosion, "", true, UradContains(V(vx.Position))); break;
        }
    }

    /// <summary>A player died (the PlayerDied handler): the death sound, at the victim's position.</summary>
    void RozhlasCombatDeath(ushort victim)
    {
        if (_combat is not { } combat || !MicLive || !_byId.TryGetValue(victim, out var c) || c.Body == null) return;
        combat.Event(CombatKind.Death, "", true, UradContains(c.Body.GlobalPosition));
    }

    /// <summary>A worker was killed: the death sound. The announcer is the building's own, so her scream is heard wherever the data puts her (the stand-in spots are outside the solid shell); the others count only inside the volume.</summary>
    void OnUradWorkerKilledForFeed(CitizenKilled k)
    {
        if (_combat is not { } combat || !MicLive) return;
        if (k.Role == CitizenRole.Announcer) combat.Play(RozhlasCombat.DeathId);
        else combat.Event(CombatKind.Death, "", true, UradContains(V(k.Where)));
    }

    /// <summary>The announcer is dead (or the admin says so): the mic gets a thump and the feedback squeals over the megaphones.</summary>
    void RozhlasHijackCut()
    {
        if (_liveFeed.Value is not { } feed) return;
        feed.Mix(RozhlasSynth.Thump);
        feed.Mix(RozhlasSynth.Squeal);
    }

    /// <summary>The loudspeaker changed phase (called before the feed starts or stops, while it still takes input). OnAir to Hijacked: thump and feedback. Hijacked to Idle: a click ends the feed (nothing announces the end before it).</summary>
    void RozhlasCombatPhase(RozhlasPhase from, RozhlasPhase to)
    {
        if (to == RozhlasPhase.Hijacked) RozhlasHijackCut();
        if (from == RozhlasPhase.Hijacked && to == RozhlasPhase.Idle) _liveFeed.Value?.Mix(RozhlasSynth.Click, 1f); // a normal end has no click
    }

    /// <summary>Every tick: a running combat test is fed.</summary>
    void RozhlasCombatTick()
    {
        CombatTestTick();
    }

    // ---- /rozhlas combattest: one shot, one explosion, one death, regardless of position ----------------------------------------------------------------------------------------------------------

    sealed class CombatTest
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public int Next;
        public bool OwnsFeed;
        public static readonly (double At, CombatKind Kind, string Weapon)[] Cues = { (0.0, CombatKind.Shot, "ak47"), (0.8, CombatKind.Explosion, "rpg"), (1.8, CombatKind.Death, "") };
        public const double EndS = 4.0;
    }

    /// <summary>"/rozhlas combattest": starts the feed when the mic is not live, then an ak47 shot, an rpg blast and a death go through the combat path (position and phase filters skipped). Everyone hears them about 2 s later.</summary>
    void ChatRozhlasCombatTest(Client c)
    {
        if (EnsureLiveFeed() is not { } feed || EnsureRozhlas() == null || _combat == null || _combatBank == null) { ChatTo(c, ChatLineKind.Error, "The combat sounds have no data (sounds.json or rozhlas_voice.json could not be read)."); return; }
        if (_combatTest != null || _feedTest != null) { ChatTo(c, ChatLineKind.Error, "A feed test is already running."); return; }
        var owns = !MicLive;
        if (owns) feed.Start();
        if (feed.State != RozhlasFeedState.Live) { ChatTo(c, ChatLineKind.Error, "The live feed is not running (ffmpeg missing? see the server log)."); if (owns) feed.Stop(); return; }
        var ready = CombatTest.Cues.Count(q => _combatBank.Get(RozhlasCombat.SoundId(q.Kind, q.Weapon)) != null);
        if (ready < CombatTest.Cues.Length) ChatTo(c, ChatLineKind.Info, $"Only {ready} of {CombatTest.Cues.Length} test sounds are decoded yet ({_combatBank.Loaded} ready, {_combatBank.Failed} failed, {_combatBank.Pending} loading; see the server log).");
        _combatTest = new CombatTest { OwnsFeed = owns };
        ChatToAll(ChatLineKind.System, "", $"{c.Name} started the rozhlas combat sound test.");
    }

    void CombatTestTick()
    {
        if (_combatTest is not { } t || _liveFeed.Value is not { } feed || _combat is not { } combat) return;
        if (feed.State != RozhlasFeedState.Live) { _combatTest = null; return; } // it failed (logged by the feed)
        var el = t.Clock.Elapsed.TotalSeconds;
        while (t.Next < CombatTest.Cues.Length && el >= CombatTest.Cues[t.Next].At)
        {
            var q = CombatTest.Cues[t.Next++];
            combat.Play(RozhlasCombat.SoundId(q.Kind, q.Weapon));
        }
        if (el >= CombatTest.EndS) { if (t.OwnsFeed && !MicLive) feed.Stop(); _combatTest = null; }
    }
}
