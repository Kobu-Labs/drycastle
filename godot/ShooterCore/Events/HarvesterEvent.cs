using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Ids and the player-facing words of the harvester event (#331).</summary>
public static class HarvesterRules
{
    /// <summary>The corpse owner id of a bale (a corpse of this owner is a hay bale, drawn as one: no body, no weapon, only the Items inside). 60000 is the Bigfoot's, 60001 the Gang boss's.</summary>
    public const ushort BaleOwner = 60002;

    /// <summary>The attacker id of the harvester's kills when nobody sits at its wheel any more (a reel that still turns).</summary>
    public const ushort NobodyAttacker = 60003;

    /// <summary>The vehicle type of the harvester in vehicles.json.</summary>
    public const string VehicleId = "harvester";

    public static bool IsBale(ushort corpseOwner) => corpseOwner == BaleOwner;

    /// <summary>The chat line for a started harvester: the field and where it appeared, in map metres (x east, y up, z south).</summary>
    public static string SpawnedText(string fieldId, Vector3 at) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Harvester spawned on field {fieldId} at x={at.X:0}, y={at.Y:0}, z={at.Z:0}.");

    /// <summary>The line for a refused command; <paramref name="fieldId"/> and <paramref name="at"/> say where the running one is.</summary>
    public static string RefusedRunning(string fieldId, Vector3 at) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A harvester is already out on field {fieldId} (x={at.X:0}, y={at.Y:0}, z={at.Z:0}). One at a time: /event current end or cancel removes it.");

    public const string RefusedNoField = "The map has no crop field big enough for a harvester.";
    public const string RefusedInUse = "The last harvester is still out there with a player at its wheel.";
}

/// <summary>
/// When a harvester comes (#331): one at a time map-wide, and after one is done no new one for <see cref="HarvesterConfig.CooldownS"/> (12 minutes). The automatic spawn is rare: once
/// the cooldown is over, every <see cref="HarvesterConfig.AutoRollS"/> a roll with <see cref="HarvesterConfig.AutoChance"/> while a human plays. An admin's command ignores the cooldown, never the
/// "one at a time". Pure: the host feeds it the clock and tells it when a harvester began and ended.
/// </summary>
public sealed class HarvesterDirector
{
    readonly HarvesterConfig _cfg;
    readonly Random _rng;
    Countdown _cooldown;
    Countdown _roll;

    public HarvesterDirector(HarvesterConfig cfg, Random rng)
    {
        _cfg = cfg; _rng = rng;
        _cooldown.Start(cfg.InitialDelayS);
    }

    /// <summary>A harvester is out (from its start until it is done).</summary>
    public bool Active { get; private set; }

    /// <summary>Seconds of cooldown left (0 when a harvester may come).</summary>
    public float CooldownLeft => _cooldown.Left;

    /// <summary>The refusal for an admin's <c>/event harvester</c>, or null when it may start: only a running harvester stops it, the cooldown does not.</summary>
    public string? WhyNotForced() => Active ? "A harvester is already out. One at a time." : null;

    /// <summary>A harvester began (either way).</summary>
    public void Started()
    {
        Active = true;
        _cooldown.Cancel();
        _roll.Cancel();
    }

    /// <summary>The harvester is done (left the field, lost its driver, was destroyed or cancelled): the cooldown starts.</summary>
    public void Finished()
    {
        Active = false;
        _cooldown.Start(_cfg.CooldownS);
    }

    /// <summary>A new round clears the world: nothing is out, and the first one comes after the initial delay again.</summary>
    public void Reset()
    {
        Active = false;
        _cooldown.Start(_cfg.InitialDelayS);
        _roll.Cancel();
    }

    /// <summary>One tick. True when an automatic harvester is due now (the host picks a field and, if it finds none, simply waits for the next roll).</summary>
    public bool Tick(float dt, bool humansPlaying, bool otherEventRunning)
    {
        if (Active) return false;
        _cooldown.Tick(dt);
        if (_cooldown.Running || !humansPlaying || otherEventRunning) return false;
        if (_roll.Running) { _roll.Tick(dt); return false; }
        _roll.Start(_cfg.AutoRollS);
        return _rng.NextDouble() < _cfg.AutoChance;
    }
}

/// <summary>Where a bale is: whole, burning, or ash. The machine is <see cref="HayBale.Spec"/>; the values travel in the harvester effect event, so they never change.</summary>
public enum BaleState : byte { Intact = 0, Burning = 1, Ash = 2 }

/// <summary>
/// One hay bale (#331): a field's own bale or the one a harvester made of a corpse. A shot ignites it (any shot), it burns <see cref="HarvesterConfig.BaleBurnSeconds"/> and turns to ash.
/// While it burns, players within <see cref="HarvesterConfig.BaleFireRadiusM"/> are set ablaze (the burn is credited to nobody, <see cref="BurnSet.HazardSource"/>). Ash does not burn again.
/// </summary>
public sealed class HayBale
{
    public static readonly StateMachineSpec<BaleState> Spec = StateMachineSpec<BaleState>
        .Define("Hay.Bale", "A hay bale: whole, set alight by a shot, burning for a few seconds, then ash.", BaleState.Intact)
        .Allow(BaleState.Intact, BaleState.Burning)
        .Allow(BaleState.Burning, BaleState.Ash)
        .Note(BaleState.Intact, "Any shot that reaches it ignites it (a bullet, a pellet). A harvester's bale starts here too.")
        .Note(BaleState.Burning, "Fire for BaleBurnSeconds; everyone close enough is ignited each tick. Nobody is credited for those burns.")
        .Note(BaleState.Ash, "Burnt out: a dark heap. Stays until the bale despawns (a corpse's lifetime) or the round ends; it cannot be lit again.")
        .Build();

    readonly StateMachine<BaleState> _fsm = new(Spec);
    readonly HarvesterConfig _cfg;

    public HayBale(Vector3 position, HarvesterConfig cfg, ushort id = 0)
    {
        Position = position; _cfg = cfg; Id = id;
    }

    public Vector3 Position { get; }
    /// <summary>The host's handle: the corpse's id for a made bale, the index in the field's list for a static one.</summary>
    public ushort Id { get; }
    public BaleState State => _fsm.Current;
    public bool Burning => _fsm.Is(BaleState.Burning);

    /// <summary>A shot reached it. True when it caught fire now (it was whole).</summary>
    public bool Ignite()
    {
        if (!_fsm.Is(BaleState.Intact)) return false;
        _fsm.Enter(BaleState.Burning, _cfg.BaleBurnSeconds);
        return true;
    }

    /// <summary>Advances the fire. True on the tick it burns out.</summary>
    public bool Tick(float dt) => Burning && _fsm.Tick(dt) && _fsm.Enter(BaleState.Ash);

    /// <summary>Whether a shot along <paramref name="from"/> + t * <paramref name="dir"/> for <paramref name="length"/> metres passes within <see cref="HarvesterConfig.BaleHitRadiusM"/> of the bale.</summary>
    public bool HitBy(Vector3 from, Vector3 dir, float length)
    {
        var c = Position + Vector3.UnitY * 0.6f; // a bale's middle, about a metre high
        var t = Math.Clamp(Vector3.Dot(c - from, dir), 0, Math.Max(0, length));
        return Vector3.Distance(from + dir * t, c) <= _cfg.BaleHitRadiusM;
    }

    /// <summary>The players (id, feet) a burning bale sets ablaze now.</summary>
    public IEnumerable<ushort> Scorched(IEnumerable<(ushort Id, Vector3 Feet)> players)
    {
        if (!Burning) yield break;
        foreach (var (id, feet) in players)
            if (Vector3.Distance(new Vector3(feet.X, Position.Y, feet.Z), Position) <= _cfg.BaleFireRadiusM && MathF.Abs(feet.Y - Position.Y) <= 2.5f) yield return id;
    }
}

/// <summary>Where a harvester's victim is: drawn in at the front, inside the machine, baled. The machine is <see cref="MachineCorpse.Spec"/>.</summary>
public enum MachineCorpsePhase : byte { Drawn = 0, Inside = 1, Baled = 2 }

/// <summary>
/// A player the harvester killed, on the way to a bale (#331): the corpse is drawn in at the front (<see cref="HarvesterConfig.DrawSeconds"/>, <see cref="DrawFraction"/> says how far),
/// spends <see cref="HarvesterConfig.ChewSeconds"/> inside, and then the bale leaves the unloader. Pure timing; the host moves the corpse, carries its Items into the bale and spawns it.
/// </summary>
public sealed class MachineCorpse
{
    public static readonly StateMachineSpec<MachineCorpsePhase> Spec = StateMachineSpec<MachineCorpsePhase>
        .Define("Harvester.Corpse", "A victim of the harvester: drawn in at the front, inside the machine for a few seconds, then ejected as a bale at the unloader.", MachineCorpsePhase.Drawn)
        .Allow(MachineCorpsePhase.Drawn, MachineCorpsePhase.Inside)
        .Allow(MachineCorpsePhase.Inside, MachineCorpsePhase.Baled)
        .Note(MachineCorpsePhase.Drawn, "The body slides from where it fell to the intake at the front; it cannot be looted meanwhile.")
        .Note(MachineCorpsePhase.Inside, "Gone from the world (the corpse entity is removed, its Items are kept for the bale).")
        .Note(MachineCorpsePhase.Baled, "The bale is spawned at the unloader with the Items. The end.")
        .Build();

    readonly StateMachine<MachineCorpsePhase> _fsm = new(Spec);
    readonly HarvesterConfig _cfg;

    public MachineCorpse(ushort corpse, HarvesterConfig cfg)
    {
        Corpse = corpse; _cfg = cfg;
        _fsm.Restart(cfg.DrawSeconds);
    }

    public ushort Corpse { get; }
    public MachineCorpsePhase Phase => _fsm.Current;
    public bool Done => _fsm.Is(MachineCorpsePhase.Baled);

    /// <summary>0 at the start of the draw, 1 when it reaches the intake.</summary>
    public float DrawFraction => Phase == MachineCorpsePhase.Drawn ? Math.Clamp(1f - _fsm.Left / _cfg.DrawSeconds, 0f, 1f) : 1f;

    /// <summary>Advances the clock and returns the phase it entered this tick, or null.</summary>
    public MachineCorpsePhase? Tick(float dt)
    {
        if (Done || !_fsm.Tick(dt)) return null;
        if (_fsm.Is(MachineCorpsePhase.Drawn)) { _fsm.Enter(MachineCorpsePhase.Inside, _cfg.ChewSeconds); return MachineCorpsePhase.Inside; }
        _fsm.Enter(MachineCorpsePhase.Baled);
        return MachineCorpsePhase.Baled;
    }
}

/// <summary>Who a burn's damage is credited to (<see cref="BurnSet"/>): the player who set it, or nobody.</summary>
public static class BurnCredit
{
    /// <summary>
    /// The attacker id the damage is dealt under. A fireball's burn (<see cref="BurnSet.NoSource"/>), a burning bale's (<see cref="BurnSet.HazardSource"/>) and the burn of a source that has left
    /// are the victim's own damage: a death, and nobody scores the kill. Anyone else's burn (the devil's curse) is credited to its owner.
    /// </summary>
    public static ushort Attacker(ushort source, ushort victim, bool sourceIsInGame) =>
        source == BurnSet.NoSource || source == BurnSet.HazardSource || !sourceIsInGame ? victim : source;
}

/// <summary>Wire payloads of the harvester events (little-endian).</summary>
public static class HarvesterCodec
{
    public const int StateBytes = 12;

    /// <summary>The harvester's job as everyone needs it to draw the field: where it is in its job, which field (index in the map's field list) and the progress of the cut, and the plan's numbers.</summary>
    public readonly record struct StateMsg(HarvesterPhase Phase, ushort Car, ushort Field, byte Pass, float Along, bool Cutting, bool Driven, float HeaderM, float MarginM, float OverlapM);

    /// <summary>Server to everyone, <see cref="EventKind.HarvesterState"/>: u8 phase, u16 vehicle id, u16 field index, u8 pass (255: none yet), u16 metres done on the pass in decimetres, u8 flags (1 cutting, 2 driven by the AI), u8 header, u8 margin and u8 overlap in decimetres.</summary>
    public static byte[] EncodeState(StateMsg m)
    {
        var b = new byte[StateBytes];
        b[0] = (byte)m.Phase;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), m.Car);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), m.Field);
        b[5] = m.Pass;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)Math.Clamp((int)MathF.Round(m.Along * 10f), 0, ushort.MaxValue));
        b[8] = (byte)((m.Cutting ? 1 : 0) | (m.Driven ? 2 : 0));
        b[9] = Dm(m.HeaderM); b[10] = Dm(m.MarginM); b[11] = Dm(m.OverlapM);
        return b;
    }

    static byte Dm(float m) => (byte)Math.Clamp((int)MathF.Round(m * 10f), 0, 255);

    public static StateMsg? DecodeState(ReadOnlySpan<byte> d)
    {
        if (d.Length != StateBytes || !Enum.IsDefined((HarvesterPhase)d[0])) return null;
        return new StateMsg((HarvesterPhase)d[0], System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d[1..]), System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d[3..]), d[5],
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d[6..]) / 10f, (d[8] & 1) != 0, (d[8] & 2) != 0, d[9] / 10f, d[10] / 10f, d[11] / 10f);
    }

    public const int FxBytes = 15;

    public enum FxKind : byte
    {
        /// <summary>A bale caught fire (the id is the corpse's for a made bale, else the static bale's index with the high bit set).</summary>
        BaleLit = 0,
        /// <summary>A bale burnt out.</summary>
        BaleAsh = 1,
        /// <summary>A victim was drawn in (the id is the corpse's).</summary>
        Drawn = 2,
        /// <summary>A bale left the unloader (the id is the bale corpse's).</summary>
        BaleOut = 3,
    }

    public readonly record struct FxMsg(FxKind Kind, Vector3 Position, ushort Id);

    /// <summary>Server to everyone, <see cref="EventKind.HarvesterFx"/>: u8 kind, three i32 position in 1/128 m, u16 id.</summary>
    public static byte[] EncodeFx(FxMsg m)
    {
        var b = new byte[FxBytes];
        b[0] = (byte)m.Kind;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), Quant.Pos(m.Position.X));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), Quant.Pos(m.Position.Y));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), Quant.Pos(m.Position.Z));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), m.Id);
        return b;
    }

    public static FxMsg? DecodeFx(ReadOnlySpan<byte> d)
    {
        if (d.Length != FxBytes || !Enum.IsDefined((FxKind)d[0])) return null;
        var p = new Vector3(Quant.Pos(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(d[1..])), Quant.Pos(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(d[5..])), Quant.Pos(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(d[9..])));
        return new FxMsg((FxKind)d[0], p, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d[13..]));
    }

    /// <summary>The id a static field bale (index in the list) goes by in an effect: the high bit says it is not a corpse.</summary>
    public static ushort StaticBaleId(int index) => (ushort)(0x8000 | index);
    public static bool IsStaticBale(ushort id) => (id & 0x8000) != 0;
    public static int StaticBaleIndex(ushort id) => id & 0x7FFF;
}
