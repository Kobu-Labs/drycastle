using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where a Heist is (machine <c>Heist.Phase</c>, docs/cartel.md "Heists"). <see cref="ToVan"/> is the start (last in the enum: the numbers of the others stand).</summary>
public enum HeistPhase : byte { ToObjective, AtObjective, ToDrop, AtDrop, Delivered, Lost, ToVan }

/// <summary>The Assault clock of a Heist (machine <c>Heist.Assaults</c>): when the next Rival crew van is due.</summary>
public enum AssaultClockState : byte { Idle, Waiting, Paused, DropWaiting, Over }

/// <summary>Where the Cartel van is as far as the Assaults care (<see cref="AssaultClock.Tick(float, AssaultSite)"/>): in the objective's area, in the drop's area, or on the road between.</summary>
public enum AssaultSite : byte { Away, Objective, Drop }

/// <summary>What the server reads of the world each tick for a Heist: the Cartel van, where it stands, what is loaded in it and in the drop's Receiver.</summary>
/// <param name="VanDestroyed">The Cartel van is destroyed (or gone): the Heist is lost.</param>
/// <param name="VanInArea">The van stands in the objective's area (the plantation and its margin; the trap house's yard).</param>
/// <param name="VanInDropArea">The van stands within the drop's area (the Receiver's: <see cref="ReceiverKindData.AreaM"/>).</param>
/// <param name="Loaded">Duffels loaded in the van now.</param>
/// <param name="Received">Duffels on the Receiver now (its own count; frozen at the moment it was sent off).</param>
/// <param name="Sent">The Receiver was sent off (it leaves, or the buyer took the bags): the Job succeeds with <paramref name="Received"/>.</param>
/// <param name="PartyAtVan">A human of the party is at the Cartel van or sits in it (the server's reach): the party has found its van.</param>
public readonly record struct HeistFacts(bool VanDestroyed, bool VanInArea, bool VanInDropArea, int Loaded, int Received = 0, bool Sent = false, bool PartyAtVan = false);

/// <summary>How a Heist's drop is worded in the tracker line: where ("the helicopter") and how it is finished ("tell the pilot to leave"). The server fills it from the Receiver (<see cref="ReceiverRules"/>).</summary>
public readonly record struct DropText(string Place, string Leave)
{
    public static readonly DropText Generic = new("the drop point", "send it off");
}

/// <summary>One step of a Heist: the Job's step, and whether an Assault is due now.</summary>
public readonly record struct HeistTick(JobStep Step, bool AssaultDue);

/// <summary>What a Heist's marker points at now.</summary>
public enum HeistMarker : byte { None, Objective, Van, Drop }

/// <summary>
/// When the Assaults of a Heist come (docs/cartel.md "Assaults"): nothing before the van first reaches the objective; the first Assault is due the moment it does (no grace: the Assault's drive in is
/// the warning), then one every interval while the Heist is at its objective. The clock pauses while the van is away on the road and goes on where it was if the van comes back. At the drop (the van within
/// the Receiver's area) it runs again: the first Assault is due the moment the van arrives, then one every interval, until the Heist ends. It is over when the Heist ends. A stalled Assault (the newest one made no
/// headway) asks for the next at once: <see cref="Restart"/> starts the interval over from that one.
/// </summary>
public sealed class AssaultClock
{
    public static readonly StateMachineSpec<AssaultClockState> Spec = StateMachineSpec<AssaultClockState>
        .Define("Heist.Assaults", "When a Heist's next Assault (a Rival crew van) is due: the moment the Cartel van first reaches the objective, then every interval while it is there; again the moment it first reaches the drop, then every interval, until the Heist ends.", AssaultClockState.Idle)
        .Allow(AssaultClockState.Idle, AssaultClockState.Waiting)
        .Allow(AssaultClockState.Waiting, AssaultClockState.Paused)
        .Allow(AssaultClockState.Paused, AssaultClockState.Waiting, AssaultClockState.DropWaiting)
        .Allow(AssaultClockState.DropWaiting, AssaultClockState.Paused)
        .FromAnywhere(AssaultClockState.Over)
        .Note(AssaultClockState.Idle, "The van has not reached the objective yet; no clock.")
        .Note(AssaultClockState.Waiting, "The van is at the objective: the first Assault came the moment it arrived, then every interval (clock: the interval); at its end the next is due and the clock starts over. A stalled Assault sends the next at once and starts the interval over.")
        .Note(AssaultClockState.Paused, "The van is on the road, away from the objective and from the drop: no new Assault. The time left is kept; back at the objective it goes on with it (Waiting), back at the drop it goes on in its own state (DropWaiting), a first arrival at the drop makes an Assault due at once.")
        .Note(AssaultClockState.DropWaiting, "The van is at the drop: the first Assault came the moment it arrived, then every interval (clock: the interval); at its end the next is due and the clock starts over, until the Heist ends.")
        .Note(AssaultClockState.Over, "The Heist ended. Never left.")
        .Build();

    readonly StateMachine<AssaultClockState> _fsm = new(Spec);
    readonly float _interval;
    float _held;
    /// <summary>The state the clock was in when it was paused (what a return to the same place goes on with).</summary>
    AssaultClockState _from;

    public AssaultClock(float intervalSeconds) { _interval = intervalSeconds; }

    public AssaultClockState State => _fsm.Current;
    /// <summary>The clock runs: the van is at the objective or at the drop (a stalled Assault may send the next now).</summary>
    public bool Running => _fsm.IsAny(AssaultClockState.Waiting, AssaultClockState.DropWaiting);
    /// <summary>Seconds to the next Assault (infinite while idle, paused or over).</summary>
    public float Left => Running ? _fsm.Left : float.PositiveInfinity;
    /// <summary>Assaults that fell due so far (a stalled one's early next not counted: <see cref="Restart"/>).</summary>
    public int Due { get; private set; }

    /// <summary>One tick with the van at the objective or not (the template before the drop's Assaults).</summary>
    public bool Tick(float dt, bool atObjective) => Tick(dt, atObjective ? AssaultSite.Objective : AssaultSite.Away);

    /// <summary>One tick: <paramref name="site"/> is where the van is. True when an Assault is due now (once per due time).</summary>
    public bool Tick(float dt, AssaultSite site)
    {
        switch (_fsm.Current)
        {
            case AssaultClockState.Idle:
                if (site != AssaultSite.Objective) return false;
                _fsm.Enter(AssaultClockState.Waiting, _interval); // the van's first arrival: an Assault now, the next in an interval
                Due++;
                return true;
            case AssaultClockState.Waiting:
                return Run(dt, site == AssaultSite.Objective);
            case AssaultClockState.DropWaiting:
                return Run(dt, site == AssaultSite.Drop);
            case AssaultClockState.Paused:
                if (site == AssaultSite.Objective) _fsm.Enter(AssaultClockState.Waiting, _from == AssaultClockState.Waiting ? _held : _interval);
                else if (site == AssaultSite.Drop)
                {
                    if (_from == AssaultClockState.DropWaiting) _fsm.Enter(AssaultClockState.DropWaiting, _held);
                    else { _fsm.Enter(AssaultClockState.DropWaiting, _interval); Due++; return true; } // the first arrival at the drop: an Assault now
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>A running state (at the objective or at the drop): paused when the van left its site, else counted down; at zero an Assault is due and the interval starts.</summary>
    bool Run(float dt, bool here)
    {
        if (!here) { _held = _fsm.Left; _from = _fsm.Current; _fsm.Enter(AssaultClockState.Paused); return false; }
        if (!_fsm.Tick(dt)) return false;
        _fsm.Restart(_interval);
        Due++;
        return true;
    }

    /// <summary>An Assault was sent out of turn (the newest one stalled): the interval starts over from it. Nothing unless the clock runs.</summary>
    public void Restart()
    {
        if (Running) _fsm.Restart(_interval);
    }

    /// <summary>The Heist ended: no Assault ever again.</summary>
    public void Stop() => _fsm.Enter(AssaultClockState.Over);
}

/// <summary>
/// The Heist template (GLOSSARY "Heist"; docs/cartel.md "Heists"), pure: the phase of one Heist and its Assault clock, stepped once a tick from <see cref="HeistFacts"/>. The van drives to the
/// objective (ToObjective), the party fills Duffels and loads them (AtObjective, Assaults come), the van leaves the objective's area with at least the minimum loaded (ToDrop, no new Assault)
/// and reaches the drop's area (AtDrop: the party unloads the van into the drop's Receiver and Assaults come to the drop). The Heist succeeds the moment the Receiver is sent off with at least
/// the minimum on it (Delivered), wherever the van is by then. The van destroyed loses it (Lost: the Job fails). What the objective is (the plantation, a trap house) is the kind's; the template
/// knows only its area. The server part of a kind feeds the facts and launches the Assaults. The party starts at the van (ToVan: the marker is on it) and drives it to the objective (ToObjective) once a party player is at it or in it.
/// </summary>
public sealed class Heist
{
    public static readonly StateMachineSpec<HeistPhase> PhaseSpec = StateMachineSpec<HeistPhase>
        .Define("Heist.Phase", "One Heist from the Contact to the drop: the party to the Cartel van, the van to the objective, filling and loading under Assaults, the van to the drop, unloading into its Receiver under Assaults; the van's destruction loses it.", HeistPhase.ToVan)
        .Allow(HeistPhase.ToVan, HeistPhase.ToObjective, HeistPhase.AtObjective, HeistPhase.Delivered)
        .Allow(HeistPhase.ToObjective, HeistPhase.AtObjective, HeistPhase.Delivered)
        .Allow(HeistPhase.AtObjective, HeistPhase.ToDrop, HeistPhase.Delivered)
        .Allow(HeistPhase.ToDrop, HeistPhase.AtObjective, HeistPhase.AtDrop, HeistPhase.Delivered)
        .Allow(HeistPhase.AtDrop, HeistPhase.ToDrop, HeistPhase.AtObjective, HeistPhase.Delivered)
        .FromAnywhere(HeistPhase.Lost)
        .Note(HeistPhase.ToVan, "The Cartel van is put down beside the Contact and the party has not reached it yet (the marker is on the van). Ends when a party player is at the van or sits in it.")
        .Note(HeistPhase.ToObjective, "The party has the Cartel van and it has not reached the objective's area yet; no Assault. Ends when it stands in the area.")
        .Note(HeistPhase.AtObjective, "The party fills Duffels and loads them in the van; the Assault clock runs. Stays while the van is away with fewer than the minimum loaded.")
        .Note(HeistPhase.ToDrop, "The van left the area with at least the minimum loaded: the Assaults stop. Back in the area it is AtObjective again; within the drop's area it is AtDrop.")
        .Note(HeistPhase.AtDrop, "The van is in the drop's area: the party carries or throws the Duffels from the van into the Receiver (helicopter, boat, or the pallet of a stash house); the Assault clock runs again (the first Assault the moment the van arrives). Leaving the area is ToDrop again.")
        .Note(HeistPhase.Delivered, "The Receiver was sent off with at least the minimum on it (it left by itself with every Duffel aboard, the leader told it to, or the buyer took the bags): the Job succeeds; Respect counts what was on the Receiver then. Never left.")
        .Note(HeistPhase.Lost, "The Cartel van was destroyed: the Job fails. Never left.")
        .Build();

    readonly StateMachine<HeistPhase> _phase = new(PhaseSpec);

    /// <param name="min">The fewest Duffels the drop takes (<see cref="HeistRules.MinDuffels"/>).</param>
    /// <param name="max">The most Duffels the party can fill (<see cref="HeistRules.MaxDuffels"/>).</param>
    /// <param name="intervalSeconds">The Assaults come this far apart, at the objective and at the drop (the first the moment the van arrives).</param>
    public Heist(int min, int max, float intervalSeconds)
    {
        Min = min; Max = max;
        Assaults = new AssaultClock(intervalSeconds);
    }

    public int Min { get; }
    public int Max { get; }
    public HeistPhase Phase => _phase.Current;
    /// <summary>The phase before the last change (the first until the first change): what the server asks to tell "the van left the objective" and "the van reached the drop".</summary>
    public HeistPhase Previous => _phase.Previous;
    public AssaultClock Assaults { get; }
    /// <summary>Duffels loaded in the van as of the last step.</summary>
    public int Loaded { get; private set; }
    /// <summary>Duffels on the drop's Receiver as of the last step (frozen at the moment it was sent off: what Respect counts).</summary>
    public int Received { get; private set; }
    /// <summary>The van stood in the objective's area at the last step.</summary>
    public bool VanInArea { get; private set; }
    /// <summary>The van stood in the drop's area at the last step.</summary>
    public bool VanInDropArea { get; private set; }
    /// <summary>How the drop is worded in the line ("the helicopter", "tell the pilot to leave"); set by the server once the drop is chosen.</summary>
    public DropText Drop { get; set; } = DropText.Generic;

    /// <summary>The phase changed (from, to): raised by <see cref="Step"/>. A later wave hangs the Rival chase cars on "the van left the objective with the minimum" (AtObjective to ToDrop) and "the van reached the drop" (to AtDrop).</summary>
    public event Action<HeistPhase, HeistPhase>? PhaseChanged
    {
        add => _phase.Changed += value;
        remove => _phase.Changed -= value;
    }

    public HeistTick Step(in HeistFacts f, float dt)
    {
        if (_phase.IsAny(HeistPhase.Delivered, HeistPhase.Lost)) return new(_phase.Is(HeistPhase.Delivered) ? JobStep.Succeeded : JobStep.Failed, false);
        if (f.VanDestroyed)
        {
            _phase.Enter(HeistPhase.Lost);
            Assaults.Stop();
            return new(JobStep.Failed, false);
        }
        Loaded = f.Loaded; VanInArea = f.VanInArea; VanInDropArea = f.VanInDropArea; Received = f.Received;
        if (f.Sent && f.Received >= Min)
        {
            _phase.Enter(HeistPhase.Delivered);
            Assaults.Stop();
            return new(JobStep.Succeeded, false);
        }
        var enough = f.Loaded >= Min;
        switch (_phase.Current)
        {
            case HeistPhase.ToVan: if (f.VanInArea) _phase.Enter(HeistPhase.AtObjective); else if (f.PartyAtVan) _phase.Enter(HeistPhase.ToObjective); break;
            case HeistPhase.ToObjective: if (f.VanInArea) _phase.Enter(HeistPhase.AtObjective); break;
            case HeistPhase.AtObjective: if (!f.VanInArea && enough) _phase.Enter(HeistPhase.ToDrop); break;
            case HeistPhase.ToDrop:
                if (f.VanInArea) _phase.Enter(HeistPhase.AtObjective);
                else if (f.VanInDropArea) _phase.Enter(HeistPhase.AtDrop);
                break;
            case HeistPhase.AtDrop:
                if (!f.VanInDropArea) _phase.Enter(f.VanInArea ? HeistPhase.AtObjective : HeistPhase.ToDrop);
                break;
        }
        var site = _phase.Current switch { HeistPhase.AtObjective => AssaultSite.Objective, HeistPhase.AtDrop => AssaultSite.Drop, _ => AssaultSite.Away };
        return new(JobStep.Running, Assaults.Tick(dt, site));
    }

    /// <summary>
    /// What the party's marker points at now: the van until the party has reached it, then the objective on the way there (and while the van is away short of the minimum), the van's bay while loading,
    /// nothing once the van holds the minimum in the area (the line says to leave; the drop's marker waits until the van is out), the drop after.
    /// </summary>
    public HeistMarker Marker => _phase.Current switch
    {
        HeistPhase.ToVan => HeistMarker.Van,
        HeistPhase.ToObjective => HeistMarker.Objective,
        HeistPhase.AtObjective => !VanInArea ? HeistMarker.Objective : Loaded >= Min ? HeistMarker.None : HeistMarker.Van,
        HeistPhase.ToDrop or HeistPhase.AtDrop => HeistMarker.Drop,
        _ => HeistMarker.None,
    };

    /// <summary>The tracker's objective line now. <paramref name="place"/> names the objective ("the plantation"); <paramref name="fill"/> says how the party fills a Duffel there. With the minimum loaded and the van still in the area the line is "Leave the area": the drop's own line comes once the van is out.</summary>
    public string Line(string place, string fill) => _phase.Current switch
    {
        HeistPhase.ToVan => $"Go to the Cartel van, then drive it to {place}",
        HeistPhase.ToObjective => $"Drive the Cartel van to {place}",
        HeistPhase.AtObjective when !VanInArea => $"Bring the van back to {place}: {Loaded}/{Min} loaded",
        HeistPhase.AtObjective when Loaded < Min => $"{fill}: {Loaded}/{Min} loaded",
        HeistPhase.AtObjective => $"Leave the area: {Loaded} loaded (up to {Max})",
        HeistPhase.ToDrop => Loaded < Min ? $"Load at least {Min} duffels: {Loaded} loaded" : $"Drive the van to {Drop.Place}: {Loaded} loaded",
        HeistPhase.AtDrop when Received < Min => $"Unload the duffels into {Drop.Place}: {Received}/{Min}",
        HeistPhase.AtDrop => $"{Received} on board (up to {Max}): unload the rest, then {Drop.Leave}",
        HeistPhase.Delivered => $"Delivered: {Received} duffels",
        _ => "The Cartel van is lost",
    };
}

/// <summary>
/// A Heist kind's numbers (cartel.json <c>jobs.&lt;kind&gt;</c>, docs/cartel.md "Heists", "Difficulty and reward"): the timer, the Duffel counts and the Assaults by tier and squad size, the Cartel van,
/// the Drop point. Each array has one number per tier, tier 1 first. The defaults here are first guesses. A kind adds its own fields (<see cref="HarvestData"/>).
/// </summary>
public class HeistData
{
    public string Name { get; set; } = "Heist";
    /// <summary>The Job's timer per tier: a safety, the heist fails when it runs out.</summary>
    public float[] Seconds { get; set; } = { 900, 870, 840, 810, 780 };
    /// <summary>The fewest Duffels loaded that the drop takes, per tier, and how many more per party member beyond the first.</summary>
    public int[] MinDuffels { get; set; } = { 2, 3, 3, 4, 4 };
    public int MinPerExtraMember { get; set; } = 1;
    /// <summary>The most Duffels the party can fill, per tier, and how many more per party member beyond the first; never more than the van takes.</summary>
    public int[] MaxDuffels { get; set; } = { 5, 6, 7, 8, 9 };
    public int MaxPerExtraMember { get; set; } = 1;
    /// <summary>Respect for each Duffel loaded above the minimum at the drop, times the tier, on top of the tier's Respect (each earning member).</summary>
    public int BonusRespectPerDuffel { get; set; } = 15;
    /// <summary>The first Assault comes the moment the van reaches the objective (and again at the drop), the next every interval, per tier: 40% of the first guesses (100 to 60).</summary>
    public float[] AssaultIntervalSeconds { get; set; } = { 40, 36, 32, 28, 24 };
    /// <summary>One Assault's crew per tier (Gunmen and Enforcers of the Rival crew), plus Gunmen per party member beyond the first; at most max_crew a van, its Enforcers kept first. A van carries 5: 1 melee (an Enforcer) and 4 Gunmen.</summary>
    public int[] AssaultGunmen { get; set; } = { 4, 4, 4, 4, 5 };
    public int[] AssaultEnforcers { get; set; } = { 1, 1, 1, 1, 1 };
    public int GunmenPerExtraMember { get; set; } = 1;
    public int MaxCrew { get; set; } = 6;
    /// <summary>No new Assault while this many of the Heist's are still in the fight (a crew member alive, or the crew still riding: <see cref="ChaseCar.InFight"/>; a stalled one does not count); the clock goes on.</summary>
    public int MaxAssaultsAlive { get; set; } = 3;
    /// <summary>The vehicle type the Heist puts down (vehicles.json), on the road spot nearest the Contact at least van_spawn_min_m from him.</summary>
    public string VanType { get; set; } = "cartel_van";
    /// <summary>The van's top speed with any Duffel loaded, km/h (empty it drives as vehicles.json says): a governor on the throttle, held by the server and the driver's predicting client alike (<see cref="Vehicles.SpeedGovernor"/>, <see cref="CartelFile.LoadedCapMps"/>).</summary>
    public float LoadedMaxKmh { get; set; } = 60;
    public float VanSpawnMinM { get; set; } = 8;
    /// <summary>The van's bay takes a Duffel this much outside its volume too (the closed rear doors).</summary>
    public float VanLoadMarginM { get; set; } = 0.35f;
    /// <summary>The drop (a Receiver: a stash house, a helicopter's landing spot or a boat's mooring): at least drop_min_m from the objective, drop_apart_m from another Job's. The van is "at the drop" within the Receiver's own area (cartel.json receivers).</summary>
    public float DropMinM { get; set; } = 300;
    public float DropApartM { get; set; } = 40;
    /// <summary>The Rival chase cars after the van once it leaves the objective with the minimum (docs/cartel.md "Chase cars"): when, how big, where, how they fight and when they give up.</summary>
    public ChaseData Chase { get; set; } = new();

    /// <summary>Checks the numbers; <paramref name="what"/> names the section (<c>cartel.json: jobs.harvest</c>).</summary>
    public virtual void Validate(string what, int tiers)
    {
        DataFileException Bad(string text) => new($"{what}: {text}");
        void PerTier<T>(T[] a, string name, Func<T, bool> ok, string rule) => CartelTiers.Check(a, tiers, $"{what}: {name}", ok, rule);
        PerTier(Seconds, "seconds", CartelTiers.Positive, "more than 0");
        PerTier(MinDuffels, "min_duffels", n => n >= 1, "at least 1");
        PerTier(MaxDuffels, "max_duffels", n => n >= 1, "at least 1");
        for (var t = 0; t < tiers; t++) if (MaxDuffels[t] < MinDuffels[t]) throw Bad($"tier {t + 1}: max_duffels is below min_duffels");
        PerTier(AssaultIntervalSeconds, "assault_interval_seconds", CartelTiers.Positive, "more than 0");
        PerTier(AssaultGunmen, "assault_gunmen", CartelTiers.Count, "0 or more");
        PerTier(AssaultEnforcers, "assault_enforcers", CartelTiers.Count, "0 or more");
        for (var t = 0; t < tiers; t++) if (AssaultGunmen[t] + AssaultEnforcers[t] < 1) throw Bad($"tier {t + 1}: an Assault needs at least one member");
        if (MinPerExtraMember < 0 || MaxPerExtraMember < 0 || GunmenPerExtraMember < 0) throw Bad("the per_extra_member numbers must be 0 or more");
        if (BonusRespectPerDuffel < 0) throw Bad("bonus_respect_per_duffel must be 0 or more");
        if (MaxCrew is < 1 or > Thieves.RaidVanRules.MaxInside) throw Bad($"max_crew must be 1 to {Thieves.RaidVanRules.MaxInside}");
        if (MaxAssaultsAlive < 1) throw Bad("max_assaults_alive must be at least 1");
        if (string.IsNullOrWhiteSpace(VanType)) throw Bad("van_type names a vehicles.json type");
        if (!float.IsFinite(LoadedMaxKmh) || LoadedMaxKmh <= 0) throw Bad("loaded_max_kmh must be more than 0");
        foreach (var (v, name) in new[] { (VanSpawnMinM, "van_spawn_min_m"), (VanLoadMarginM, "van_load_margin_m"), (DropMinM, "drop_min_m"), (DropApartM, "drop_apart_m") })
            if (!float.IsFinite(v) || v < 0) throw Bad($"{name} must be 0 or more");
        Chase.Validate($"{what}.chase", tiers);
    }
}

/// <summary>The Harvest's numbers (cartel.json <c>jobs.harvest</c>): the Heist template's and the plantation's own.</summary>
public sealed class HarvestData : HeistData
{
    /// <summary>The kind's id in cartel.json <c>jobs</c>.</summary>
    public const string KindId = "harvest";
    public HarvestData() => Name = "Harvest";
    /// <summary>A party member's hold of the interact key in the plantation that fills a weed Duffel (instead of the High of plantation.json).</summary>
    public float FillHoldSeconds { get; set; } = 3;
    /// <summary>The objective's area: the plantation's outline grown by this.</summary>
    public float AreaMarginM { get; set; } = 25;
    /// <summary>The filler's figure bends and reaches (everyone sees it, the status bit Filling) once the hold has run this long: a tap of the key shows nothing and sends nothing.</summary>
    public float FillShowAfterSeconds { get; set; } = 0.3f;

    /// <summary>The template's checks and the plantation's own.</summary>
    public override void Validate(string what, int tiers)
    {
        base.Validate(what, tiers);
        if (!float.IsFinite(FillHoldSeconds) || FillHoldSeconds <= 0) throw new DataFileException($"{what}: fill_hold_seconds must be more than 0");
        if (!float.IsFinite(AreaMarginM) || AreaMarginM < 0) throw new DataFileException($"{what}: area_margin_m must be 0 or more");
        if (!float.IsFinite(FillShowAfterSeconds) || FillShowAfterSeconds < 0 || FillShowAfterSeconds >= FillHoldSeconds) throw new DataFileException($"{what}: fill_show_after_seconds must be 0 or more and less than fill_hold_seconds");
    }
}

/// <summary>The Heist template's rules (docs/cartel.md "Difficulty and reward", "Assaults"): counts by tier and squad size, the bonus Respect, an Assault's crew.</summary>
public static class HeistRules
{
    /// <summary>The fewest Duffels loaded that the drop takes for a party of <paramref name="size"/>, never more than the van takes (<paramref name="capacity"/>, 0: no limit).</summary>
    public static int MinDuffels(HeistData d, int tier, int size, int capacity = 0)
    {
        var n = CartelTiers.Of(d.MinDuffels, tier) + d.MinPerExtraMember * Math.Max(0, size - 1);
        return capacity > 0 ? Math.Min(n, capacity) : n;
    }

    /// <summary>The most Duffels the party can fill: at least the minimum, never more than the van takes (<paramref name="capacity"/>, 0: no limit).</summary>
    public static int MaxDuffels(HeistData d, int tier, int size, int capacity = 0)
    {
        var n = Math.Max(MinDuffels(d, tier, size), CartelTiers.Of(d.MaxDuffels, tier) + d.MaxPerExtraMember * Math.Max(0, size - 1));
        return capacity > 0 ? Math.Min(n, capacity) : n;
    }

    /// <summary>Respect on top of the tier's: each Duffel loaded above the minimum pays bonus_respect_per_duffel times the tier.</summary>
    public static int Bonus(HeistData d, int tier, int loaded, int min) => d.BonusRespectPerDuffel * Math.Max(0, loaded - min) * Math.Max(1, tier);

    /// <summary>
    /// One Assault's crew for a party of <paramref name="size"/>: the tier's Enforcers (the melee: 1 a van), kept first, and its Gunmen plus gunmen_per_extra_member for each extra member in what is left of max_crew
    /// (a bigger squad adds Gunmen, never a second melee member).
    /// </summary>
    public static (int Gunmen, int Enforcers) AssaultCrew(HeistData d, int tier, int size)
    {
        var cap = Math.Max(0, d.MaxCrew);
        var enforcers = Math.Min(cap, CartelTiers.Of(d.AssaultEnforcers, tier));
        var gunmen = Math.Min(cap - enforcers, CartelTiers.Of(d.AssaultGunmen, tier) + d.GunmenPerExtraMember * Math.Max(0, size - 1));
        return (gunmen, enforcers);
    }

    /// <summary>The Job timer of a tier.</summary>
    public static float Seconds(HeistData d, int tier) => CartelTiers.Of(d.Seconds, tier);
    public static float IntervalSeconds(HeistData d, int tier) => CartelTiers.Of(d.AssaultIntervalSeconds, tier);
}

/// <summary>
/// The objective's area of a Harvest (docs/cartel.md "Harvest"): a plantation's outline (Godot ground axes x, z) grown by a margin. The van stands in it while it is inside the outline or
/// within the margin of its edge; its centre (the outline's mean) is where the marker points and the Assaults head.
/// </summary>
public sealed class PlantationArea
{
    readonly Vector2[] _ring;
    readonly float _margin;

    public PlantationArea(IReadOnlyList<Vector2> ring, float marginM)
    {
        if (ring.Count < 3) throw new ArgumentException("a plantation outline has at least three points", nameof(ring));
        _ring = ring.ToArray(); _margin = marginM;
        var sum = Vector2.Zero;
        foreach (var p in _ring) sum += p;
        Centre = sum / _ring.Length;
    }

    /// <summary>The outline's mean point (x, z).</summary>
    public Vector2 Centre { get; }

    /// <summary>The ground point (x, z) is in the area: inside the outline or within the margin of its edge.</summary>
    public bool Contains(float x, float z)
    {
        if (Plantation.ZonePolygons.PointInPolygon(_ring, x, z)) return true;
        var p = new Vector2(x, z);
        var m2 = _margin * _margin;
        for (int i = 0, j = _ring.Length - 1; i < _ring.Length; j = i++)
            if (SegmentDistance2(p, _ring[j], _ring[i]) <= m2) return true;
        return false;
    }

    /// <summary>Of several plantation outlines, the one nearest the point (its mean point): the Contact's own.</summary>
    public static int Nearest(IReadOnlyList<IReadOnlyList<Vector2>> rings, Vector2 to)
    {
        var best = -1; var bestD = float.PositiveInfinity;
        for (var i = 0; i < rings.Count; i++)
        {
            if (rings[i].Count < 3) continue;
            var c = Vector2.Zero;
            foreach (var p in rings[i]) c += p;
            var d = Vector2.DistanceSquared(c / rings[i].Count, to);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    static float SegmentDistance2(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        var t = len2 > 1e-9f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.DistanceSquared(p, a + ab * t);
    }
}
