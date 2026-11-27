using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Plantation;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where a Drill on a Trap house's safe is (machine <c>Drill.Run</c>). The wire carries the byte: do not renumber.</summary>
public enum DrillState : byte
{
    /// <summary>The safe is armed for a Job's party: nobody set the drill on it yet.</summary>
    Armed = 0,
    Running = 1,
    Jammed = 2,
    /// <summary>Through: the safe stands open.</summary>
    Done = 3,
}

/// <summary>What a hold of the interact key at the safe does: nothing, set the drill on it, or fix the jammed drill. The wire carries the byte.</summary>
public enum DrillHold : byte { None = 0, Set = 1, Fix = 2 }

/// <summary>What one tick of a running Drill came to.</summary>
public enum DrillStep : byte { None, Jammed, Done }

/// <summary>
/// The Drill on a Trap house's safe (GLOSSARY "Drill"; docs/cartel.md "Trap house"; trap_house.json <c>drill</c>), pure: armed for a Job's party at a tier, a party player sets it (a hold
/// of the interact key), it runs <see cref="RunSeconds"/> and jams at the moments of <see cref="JamsAt"/> (drilled seconds), a party player fixes each jam (another hold), and through it
/// is done (the safe opens). Only Running drills. Machine <see cref="Spec"/>: the clock of Running counts down to the next stop (a jam, or the end).
/// </summary>
public sealed class Drill
{
    public static readonly StateMachineSpec<DrillState> Spec = StateMachineSpec<DrillState>
        .Define("Drill.Run", "The drill on a Trap house's safe: armed for a Job's party, set by one of them, running toward the open safe, jammed now and then until one of them fixes it, done.", DrillState.Armed)
        .Allow(DrillState.Armed, DrillState.Running)
        .Allow(DrillState.Running, DrillState.Jammed, DrillState.Done)
        .Allow(DrillState.Jammed, DrillState.Running)
        .Note(DrillState.Armed, "The safe waits for the party: a party player's hold of the interact key at its stand spot (drill.set_hold_seconds) sets the drill on it. No clock.")
        .Note(DrillState.Running, "Drills: the progress grows with time. Clock: the seconds to the next stop, the next jam (drill.jams, at random moments inside drill.jam_window) or the end (drill.run_seconds by tier).")
        .Note(DrillState.Jammed, "Stuck at its progress, sparking: a party player's hold at the safe (drill.fix_hold_seconds) sets it running again. No clock: it waits as long as it takes.")
        .Note(DrillState.Done, "Through: the safe stands open and its cash Duffels lie by it (drill.cash_duffels by tier). Never left; the Drill goes when its house is stripped.")
        .Build();

    readonly StateMachine<DrillState> _fsm = new(Spec);
    readonly float[] _jams;
    int _nextJam;

    /// <param name="jamsAt">The drilled seconds it jams at, rising, each inside (0, runSeconds) (<see cref="DrillRules.JamsAt"/>).</param>
    public Drill(string house, int tier, IReadOnlySet<ushort> party, float runSeconds, float[] jamsAt)
    {
        if (!(runSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(runSeconds));
        House = house; Tier = tier; Party = party; RunSeconds = runSeconds; _jams = jamsAt;
    }

    /// <summary>The building id of its Trap house.</summary>
    public string House { get; }
    public int Tier { get; }
    /// <summary>The player ids that may set and fix it (the Job's party, read live).</summary>
    public IReadOnlySet<ushort> Party { get; }
    public float RunSeconds { get; }
    public DrillState State => _fsm.Current;
    /// <summary>Seconds drilled so far (0 to <see cref="RunSeconds"/>).</summary>
    public float Progress { get; private set; }
    public float Fraction => Math.Clamp(Progress / RunSeconds, 0, 1);
    public float SecondsLeft => Math.Max(0, RunSeconds - Progress);
    public int Jams => _jams.Length;
    /// <summary>Jams it has had so far.</summary>
    public int Jammed => _nextJam;
    public IReadOnlyList<float> JamsAt => _jams;

    /// <summary>What a party player's hold at the safe would do now.</summary>
    public DrillHold Hold => State switch { DrillState.Armed => DrillHold.Set, DrillState.Jammed => DrillHold.Fix, _ => DrillHold.None };

    /// <summary>A completed hold: Armed sets it running, Jammed fixes it. False when the hold does nothing in this state.</summary>
    public bool Work()
    {
        if (Hold == DrillHold.None) return false;
        Run();
        return true;
    }

    /// <summary>Into Running, the clock to the next stop.</summary>
    void Run() => _fsm.Enter(DrillState.Running, NextStop - Progress);

    float NextStop => _nextJam < _jams.Length ? _jams[_nextJam] : RunSeconds;

    /// <summary>One tick: only Running drills; reaching a jam jams it, reaching the end is done.</summary>
    public DrillStep Tick(float dt)
    {
        if (dt <= 0) return DrillStep.None;
        var running = _fsm.Is(DrillState.Running);
        var expired = _fsm.Tick(dt);
        if (!running) return DrillStep.None;
        var stop = NextStop;
        Progress = Math.Min(stop, Progress + dt);
        if (!expired) return DrillStep.None;
        Progress = stop;
        if (_nextJam < _jams.Length) { _nextJam++; _fsm.Enter(DrillState.Jammed); return DrillStep.Jammed; }
        _fsm.Enter(DrillState.Done);
        return DrillStep.Done;
    }

    public override string ToString() => $"drill {House} tier {Tier} {State} {Progress:0.#}/{RunSeconds:0.#} s, jam {_nextJam}/{_jams.Length}";
}

/// <summary>The rules of the Drill that need no world, from trap_house.json <c>drill</c>: the run time, the jams, the holds, who may hold and from where, the cash it pays out.</summary>
public static class DrillRules
{
    /// <summary>The run by tier (a tier past the list takes the last: <see cref="CartelTiers.Of"/>).</summary>
    public static float RunSeconds(TrapDrillData d, int tier) => CartelTiers.Of(d.RunSeconds, tier);

    public static int CashDuffels(TrapDrillData d, int tier) => CartelTiers.Of(d.CashDuffels, tier);

    public static float HoldSeconds(TrapDrillData d, DrillHold hold) => hold == DrillHold.Fix ? d.FixHoldSeconds : d.SetHoldSeconds;

    /// <summary>
    /// The drilled seconds of the jams: <c>jams_min</c> to <c>jams_max</c> of them, one at a random moment in each equal slot of <c>jam_window</c> (fractions of the run), kept off the
    /// slot's edges so two never come close. Rising.
    /// </summary>
    public static float[] JamsAt(TrapDrillData d, float runSeconds, Random rng)
    {
        var n = rng.Next(d.JamsMin, d.JamsMax + 1);
        var at = new float[n];
        float a = d.JamWindow[0], w = (d.JamWindow[1] - d.JamWindow[0]) / Math.Max(1, n);
        for (var i = 0; i < n; i++) at[i] = runSeconds * (a + w * (i + 0.15f + 0.7f * (float)rng.NextDouble()));
        return at;
    }

    /// <summary>Whether a player's feet are where a hold at the safe counts: within <c>reach_m</c> of its stand spot on the ground, and within <c>reach_up_m</c> of its height.</summary>
    public static bool InReach(TrapDrillData d, Vector3 stand, Vector3 feet) =>
        MathF.Abs(feet.Y - stand.Y) <= d.ReachUpM && Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(stand.X, stand.Z)) <= d.ReachM;

    /// <summary>
    /// What gives up the key to the drill: a corpse and a ground item. A mate shot while fixing a jam falls on the stand spot; if his body won the key, the safe would stay jammed
    /// until the body went (the client gives the drill the key before them too, <see cref="Hud.InteractArbiter"/>).
    /// </summary>
    public const KeyCompetitor YieldToDrill = KeyCompetitor.Corpse | KeyCompetitor.GroundItem;

    /// <summary>
    /// What a player's hold at a drill does: only a party player, alive and on foot, in reach of the stand spot, with nothing else in reach of the key that wins it (a crate, a Duffel,
    /// a door, a vehicle, ...: <see cref="KeyCompetitor"/>; not the <see cref="YieldToDrill"/> ones), and only while it waits to be set or fixed.
    /// </summary>
    public static DrillHold HoldOf(Drill drill, ushort player, bool aliveOnFoot, bool inReach, KeyCompetitor others) =>
        aliveOnFoot && inReach && (others & ~YieldToDrill) == KeyCompetitor.None && drill.Party.Contains(player) ? drill.Hold : DrillHold.None;

    /// <summary>Where the opened safe's cash Duffels lie: in a row across its front, between its face and the stand spot, <c>duffel_spread_m</c> apart (Godot axes, the floor).</summary>
    public static Vector3[] CashSpots(TrapDrillData d, SafeSpot safe, float safeDepth, int count)
    {
        var front = new Vector3(-MathF.Sin(safe.Yaw), 0, -MathF.Cos(safe.Yaw));
        var side = new Vector3(front.Z, 0, -front.X);
        var row = safe.Pos + front * (safeDepth / 2 + d.DuffelOutM);
        var spots = new Vector3[count];
        for (var i = 0; i < count; i++) spots[i] = row + side * ((i - (count - 1) / 2f) * d.DuffelSpreadM);
        return spots;
    }
}

/// <summary>What one player is told of one Drill: its house, state, progress and run, and whether the player is of its party (only they get the bar and the holds).</summary>
public readonly record struct DrillMsg(string House, DrillState State, bool Party, float Progress, float RunSeconds);

/// <summary>
/// The Drill events (all little endian; a house id is <see cref="HouseIdWire"/>: u8 length + UTF-8): <see cref="Net.EventKind.DrillState"/> (house, u8 state, u8 flags (1: the receiver is of the party), f32 progress
/// seconds, f32 run seconds), <see cref="Net.EventKind.DrillCleared"/> (house) and <see cref="Net.EventKind.DrillReach"/> (u8 <see cref="DrillHold"/>, house; an empty house with None).
/// </summary>
public static class DrillCodec
{
    const byte PartyFlag = 1;

    public static byte[] EncodeState(DrillMsg m)
    {
        var id = HouseIdWire.Bytes(m.House);
        var o = new byte[1 + id.Length + 10];
        var at = HouseIdWire.Write(o, 0, id);
        o[at] = (byte)m.State; o[at + 1] = m.Party ? PartyFlag : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(at + 2), m.Progress);
        BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(at + 6), m.RunSeconds);
        return o;
    }

    public static DrillMsg? DecodeState(byte[] p)
    {
        if (HouseIdWire.Read(p, 0, out var at) is not { } house || p.Length != at + 10 || p[at] > (byte)DrillState.Done) return null;
        return new DrillMsg(house, (DrillState)p[at], (p[at + 1] & PartyFlag) != 0, BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at + 2)), BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at + 6)));
    }

    public static byte[] EncodeCleared(string house) => HouseIdWire.Encode(house);

    public static string? DecodeCleared(byte[] p) => HouseIdWire.Decode(p);

    public static byte[] EncodeReach(DrillHold hold, string house)
    {
        var id = HouseIdWire.Bytes(hold == DrillHold.None ? "" : house);
        var o = new byte[2 + id.Length];
        o[0] = (byte)hold;
        HouseIdWire.Write(o, 1, id);
        return o;
    }

    public static (DrillHold Hold, string House)? DecodeReach(byte[] p)
    {
        if (p.Length < 1 || p[0] > (byte)DrillHold.Fix || HouseIdWire.Read(p, 1, out var end) is not { } house || end != p.Length) return null;
        return ((DrillHold)p[0], house);
    }
}
