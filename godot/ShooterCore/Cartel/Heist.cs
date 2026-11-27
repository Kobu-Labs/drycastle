using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where a Heist is (machine <c>Heist.Phase</c>, docs/cartel.md "Heists").</summary>
public enum HeistPhase : byte { ToObjective, AtObjective, ToDrop, Delivered, Lost }

/// <summary>The Assault clock of a Heist (machine <c>Heist.Assaults</c>): when the next Rival crew van is due.</summary>
public enum AssaultClockState : byte { Idle, Grace, Waiting, Paused, Over }

/// <summary>What the server reads of the world each tick for a Heist: the Cartel van, where it stands, what is loaded in it.</summary>
/// <param name="VanDestroyed">The Cartel van is destroyed (or gone): the Heist is lost.</param>
/// <param name="VanInArea">The van stands in the objective's area (the plantation and its margin; the trap house's yard).</param>
/// <param name="VanAtDrop">The van stands still within the drop radius of the Drop point.</param>
/// <param name="Loaded">Duffels loaded in the van now.</param>
public readonly record struct HeistFacts(bool VanDestroyed, bool VanInArea, bool VanAtDrop, int Loaded);

/// <summary>One step of a Heist: the Job's step, and whether an Assault is due now.</summary>
public readonly record struct HeistTick(JobStep Step, bool AssaultDue);

/// <summary>What a Heist's marker points at now.</summary>
public enum HeistMarker : byte { None, Objective, Van, Drop }

/// <summary>
/// When the Assaults of a Heist come (docs/cartel.md "Assaults"): nothing before the van first reaches the objective, then one after the grace time and one every interval while the Heist is
/// at its objective. The clock pauses while the van is away on its way to the drop (no chase in v1) and goes on where it was if the van comes back; it is over when the Heist ends.
/// </summary>
public sealed class AssaultClock
{
    public static readonly StateMachineSpec<AssaultClockState> Spec = StateMachineSpec<AssaultClockState>
        .Define("Heist.Assaults", "When a Heist's next Assault (a Rival crew van) is due: after a grace time from the van's arrival at the objective, then every interval, only while the Heist is at its objective.", AssaultClockState.Idle)
        .Allow(AssaultClockState.Idle, AssaultClockState.Grace)
        .Allow(AssaultClockState.Grace, AssaultClockState.Waiting, AssaultClockState.Paused)
        .Allow(AssaultClockState.Waiting, AssaultClockState.Paused)
        .Allow(AssaultClockState.Paused, AssaultClockState.Waiting)
        .FromAnywhere(AssaultClockState.Over)
        .Note(AssaultClockState.Idle, "The van has not reached the objective yet; no clock.")
        .Note(AssaultClockState.Grace, "The van reached the objective (clock: the grace seconds); at its end the first Assault is due.")
        .Note(AssaultClockState.Waiting, "Assaults came (clock: the interval); at its end the next is due and the clock starts over.")
        .Note(AssaultClockState.Paused, "The van left the objective with the minimum loaded: no new Assault. The time left is kept; the van's return goes on with it (Waiting).")
        .Note(AssaultClockState.Over, "The Heist ended. Never left.")
        .Build();

    readonly StateMachine<AssaultClockState> _fsm = new(Spec);
    readonly float _grace, _interval;
    float _held;

    public AssaultClock(float graceSeconds, float intervalSeconds)
    {
        _grace = graceSeconds; _interval = intervalSeconds;
    }

    public AssaultClockState State => _fsm.Current;
    /// <summary>Seconds to the next Assault (infinite while idle, paused or over).</summary>
    public float Left => _fsm.IsAny(AssaultClockState.Grace, AssaultClockState.Waiting) ? _fsm.Left : float.PositiveInfinity;
    /// <summary>Assaults that fell due so far.</summary>
    public int Due { get; private set; }

    /// <summary>One tick: <paramref name="atObjective"/> is the Heist's phase AtObjective. True when an Assault is due now (once per due time).</summary>
    public bool Tick(float dt, bool atObjective)
    {
        switch (_fsm.Current)
        {
            case AssaultClockState.Idle:
                if (atObjective) _fsm.Enter(AssaultClockState.Grace, _grace);
                return false;
            case AssaultClockState.Grace:
            case AssaultClockState.Waiting:
                if (!atObjective) { _held = _fsm.Left; _fsm.Enter(AssaultClockState.Paused); return false; }
                if (!_fsm.Tick(dt)) return false;
                if (_fsm.Is(AssaultClockState.Grace)) _fsm.Enter(AssaultClockState.Waiting, _interval);
                else _fsm.Restart(_interval);
                Due++;
                return true;
            case AssaultClockState.Paused:
                if (atObjective) _fsm.Enter(AssaultClockState.Waiting, _held);
                return false;
            default:
                return false;
        }
    }

    /// <summary>The Heist ended: no Assault ever again.</summary>
    public void Stop() => _fsm.Enter(AssaultClockState.Over);
}

/// <summary>
/// The Heist template (GLOSSARY "Heist"; docs/cartel.md "Heists"), pure: the phase of one Heist and its Assault clock, stepped once a tick from <see cref="HeistFacts"/>. The van drives to the
/// objective (ToObjective), the party fills Duffels and loads them (AtObjective, Assaults come), the van leaves the objective's area with at least the minimum loaded (ToDrop, no new Assault)
/// and stops at the Drop point (Delivered: the Job succeeds). The van destroyed loses it (Lost: the Job fails). What the objective is (the plantation, a trap house) is the kind's; the template
/// knows only its area. The server part of a kind feeds the facts and launches the Assaults.
/// </summary>
public sealed class Heist
{
    public static readonly StateMachineSpec<HeistPhase> PhaseSpec = StateMachineSpec<HeistPhase>
        .Define("Heist.Phase", "One Heist from the Contact to the Drop point: the Cartel van to the objective, filling and loading under Assaults, the van to the drop; the van's destruction loses it.", HeistPhase.ToObjective)
        .Allow(HeistPhase.ToObjective, HeistPhase.AtObjective)
        .Allow(HeistPhase.AtObjective, HeistPhase.ToDrop)
        .Allow(HeistPhase.ToDrop, HeistPhase.AtObjective, HeistPhase.Delivered)
        .FromAnywhere(HeistPhase.Lost)
        .Note(HeistPhase.ToObjective, "The Cartel van, put down beside the Contact, has not reached the objective's area yet; no Assault. Ends when it stands in the area.")
        .Note(HeistPhase.AtObjective, "The party fills Duffels and loads them in the van; the Assault clock runs. Stays while the van is away with fewer than the minimum loaded.")
        .Note(HeistPhase.ToDrop, "The van left the area with at least the minimum loaded: the Assaults stop. Back in the area it is AtObjective again; stopped within the drop radius it is Delivered.")
        .Note(HeistPhase.Delivered, "The van stopped at the Drop point with at least the minimum loaded: the Job succeeds; Respect counts what was loaded then. Never left.")
        .Note(HeistPhase.Lost, "The Cartel van was destroyed: the Job fails. Never left.")
        .Build();

    readonly StateMachine<HeistPhase> _phase = new(PhaseSpec);

    /// <param name="min">The fewest Duffels loaded that the drop takes (<see cref="HeistRules.MinDuffels"/>).</param>
    /// <param name="max">The most Duffels the party can fill (<see cref="HeistRules.MaxDuffels"/>).</param>
    public Heist(int min, int max, float graceSeconds, float intervalSeconds)
    {
        Min = min; Max = max;
        Assaults = new AssaultClock(graceSeconds, intervalSeconds);
    }

    public int Min { get; }
    public int Max { get; }
    public HeistPhase Phase => _phase.Current;
    public AssaultClock Assaults { get; }
    /// <summary>Duffels loaded as of the last step (frozen at the moment it was delivered).</summary>
    public int Loaded { get; private set; }
    /// <summary>The van stood in the objective's area at the last step.</summary>
    public bool VanInArea { get; private set; }

    public HeistTick Step(in HeistFacts f, float dt)
    {
        if (_phase.IsAny(HeistPhase.Delivered, HeistPhase.Lost)) return new(_phase.Is(HeistPhase.Delivered) ? JobStep.Succeeded : JobStep.Failed, false);
        if (f.VanDestroyed)
        {
            _phase.Enter(HeistPhase.Lost);
            Assaults.Stop();
            return new(JobStep.Failed, false);
        }
        Loaded = f.Loaded; VanInArea = f.VanInArea;
        var enough = f.Loaded >= Min;
        switch (_phase.Current)
        {
            case HeistPhase.ToObjective: if (f.VanInArea) _phase.Enter(HeistPhase.AtObjective); break;
            case HeistPhase.AtObjective: if (!f.VanInArea && enough) _phase.Enter(HeistPhase.ToDrop); break;
            case HeistPhase.ToDrop: if (f.VanInArea) _phase.Enter(HeistPhase.AtObjective); break;
        }
        if (_phase.Is(HeistPhase.ToDrop) && enough && f.VanAtDrop)
        {
            _phase.Enter(HeistPhase.Delivered);
            Assaults.Stop();
            return new(JobStep.Succeeded, false);
        }
        return new(JobStep.Running, Assaults.Tick(dt, _phase.Is(HeistPhase.AtObjective)));
    }

    /// <summary>What the party's marker points at now: the objective on the way there (and while the van is away short of the minimum), the van's bay while loading, the drop after.</summary>
    public HeistMarker Marker => _phase.Current switch
    {
        HeistPhase.ToObjective => HeistMarker.Objective,
        HeistPhase.AtObjective => VanInArea ? HeistMarker.Van : HeistMarker.Objective,
        HeistPhase.ToDrop => HeistMarker.Drop,
        _ => HeistMarker.None,
    };

    /// <summary>The tracker's objective line now. <paramref name="place"/> names the objective ("the plantation"); <paramref name="fill"/> says how the party fills a Duffel there.</summary>
    public string Line(string place, string fill) => _phase.Current switch
    {
        HeistPhase.ToObjective => $"Drive the Cartel van to {place}",
        HeistPhase.AtObjective when !VanInArea => $"Bring the van back to {place}: {Loaded}/{Min} loaded",
        HeistPhase.AtObjective when Loaded < Min => $"{fill}: {Loaded}/{Min} loaded",
        HeistPhase.AtObjective => $"{Loaded} loaded (up to {Max}): load more or drive the van to the drop point",
        HeistPhase.ToDrop => Loaded < Min ? $"Load at least {Min} duffels: {Loaded} loaded" : $"Drive the van to the drop point and stop there: {Loaded} loaded",
        HeistPhase.Delivered => $"Delivered: {Loaded} duffels",
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
    /// <summary>The first Assault comes this long after the van reaches the objective, the next every interval, per tier.</summary>
    public float[] GraceSeconds { get; set; } = { 60, 55, 50, 45, 40 };
    public float[] AssaultIntervalSeconds { get; set; } = { 100, 90, 80, 70, 60 };
    /// <summary>One Assault's crew per tier (Gunmen and Enforcers of the Rival crew), plus Gunmen per party member beyond the first; at most max_crew a van.</summary>
    public int[] AssaultGunmen { get; set; } = { 1, 2, 2, 3, 3 };
    public int[] AssaultEnforcers { get; set; } = { 2, 2, 3, 3, 4 };
    public int GunmenPerExtraMember { get; set; } = 1;
    public int MaxCrew { get; set; } = 8;
    /// <summary>No new Assault while this many of the Heist's are still in the fight (a crew member alive or still in its van: <see cref="HeistAssaults.InFight"/>); the clock goes on.</summary>
    public int MaxAssaultsAlive { get; set; } = 3;
    /// <summary>The vehicle type the Heist puts down (vehicles.json), on the road spot nearest the Contact at least van_spawn_min_m from him.</summary>
    public string VanType { get; set; } = "cartel_van";
    public float VanSpawnMinM { get; set; } = 8;
    /// <summary>The van's bay takes a Duffel this much outside its volume too (the closed rear doors).</summary>
    public float VanLoadMarginM { get; set; } = 0.35f;
    /// <summary>The Drop point: at least drop_min_m from the objective, drop_apart_m from another Job's; the van must stop (slower than drop_stop_mps) within drop_radius_m of it.</summary>
    public float DropMinM { get; set; } = 300;
    public float DropApartM { get; set; } = 40;
    public float DropRadiusM { get; set; } = 10;
    public float DropStopMps { get; set; } = 1;

    /// <summary>Checks the numbers; <paramref name="what"/> names the section (<c>cartel.json: jobs.harvest</c>).</summary>
    public virtual void Validate(string what, int tiers)
    {
        DataFileException Bad(string text) => new($"{what}: {text}");
        void PerTier<T>(T[] a, string name, Func<T, bool> ok, string rule) => CartelTiers.Check(a, tiers, $"{what}: {name}", ok, rule);
        PerTier(Seconds, "seconds", CartelTiers.Positive, "more than 0");
        PerTier(MinDuffels, "min_duffels", n => n >= 1, "at least 1");
        PerTier(MaxDuffels, "max_duffels", n => n >= 1, "at least 1");
        for (var t = 0; t < tiers; t++) if (MaxDuffels[t] < MinDuffels[t]) throw Bad($"tier {t + 1}: max_duffels is below min_duffels");
        PerTier(GraceSeconds, "grace_seconds", s => float.IsFinite(s) && s >= 0, "0 or more");
        PerTier(AssaultIntervalSeconds, "assault_interval_seconds", CartelTiers.Positive, "more than 0");
        PerTier(AssaultGunmen, "assault_gunmen", CartelTiers.Count, "0 or more");
        PerTier(AssaultEnforcers, "assault_enforcers", CartelTiers.Count, "0 or more");
        for (var t = 0; t < tiers; t++) if (AssaultGunmen[t] + AssaultEnforcers[t] < 1) throw Bad($"tier {t + 1}: an Assault needs at least one member");
        if (MinPerExtraMember < 0 || MaxPerExtraMember < 0 || GunmenPerExtraMember < 0) throw Bad("the per_extra_member numbers must be 0 or more");
        if (BonusRespectPerDuffel < 0) throw Bad("bonus_respect_per_duffel must be 0 or more");
        if (MaxCrew is < 1 or > Thieves.RaidVanRules.MaxInside) throw Bad($"max_crew must be 1 to {Thieves.RaidVanRules.MaxInside}");
        if (MaxAssaultsAlive < 1) throw Bad("max_assaults_alive must be at least 1");
        if (string.IsNullOrWhiteSpace(VanType)) throw Bad("van_type names a vehicles.json type");
        foreach (var (v, name) in new[] { (VanSpawnMinM, "van_spawn_min_m"), (VanLoadMarginM, "van_load_margin_m"), (DropMinM, "drop_min_m"), (DropApartM, "drop_apart_m") })
            if (!float.IsFinite(v) || v < 0) throw Bad($"{name} must be 0 or more");
        if (!(DropRadiusM > 0) || !(DropStopMps > 0)) throw Bad("drop_radius_m and drop_stop_mps must be more than 0");
    }
}

/// <summary>The Harvest's numbers (cartel.json <c>jobs.harvest</c>): the Heist template's and the plantation's own.</summary>
public sealed class HarvestData : HeistData
{
    public HarvestData() => Name = "Harvest";
    /// <summary>A party member's hold of the interact key in the plantation that fills a weed Duffel (instead of the High of plantation.json).</summary>
    public float FillHoldSeconds { get; set; } = 3;
    /// <summary>The objective's area: the plantation's outline grown by this.</summary>
    public float AreaMarginM { get; set; } = 25;

    /// <summary>The template's checks and the plantation's own.</summary>
    public override void Validate(string what, int tiers)
    {
        base.Validate(what, tiers);
        if (!float.IsFinite(FillHoldSeconds) || FillHoldSeconds <= 0) throw new DataFileException($"{what}: fill_hold_seconds must be more than 0");
        if (!float.IsFinite(AreaMarginM) || AreaMarginM < 0) throw new DataFileException($"{what}: area_margin_m must be 0 or more");
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

    /// <summary>One Assault's crew for a party of <paramref name="size"/>: the tier's Gunmen plus gunmen_per_extra_member for each extra member, and its Enforcers, Gunmen first within max_crew.</summary>
    public static (int Gunmen, int Enforcers) AssaultCrew(HeistData d, int tier, int size) =>
        CrewRules.Split(CartelTiers.Of(d.AssaultGunmen, tier), d.GunmenPerExtraMember, CartelTiers.Of(d.AssaultEnforcers, tier), 0, size, d.MaxCrew);

    /// <summary>The Job timer of a tier.</summary>
    public static float Seconds(HeistData d, int tier) => CartelTiers.Of(d.Seconds, tier);
    public static float GraceSeconds(HeistData d, int tier) => CartelTiers.Of(d.GraceSeconds, tier);
    public static float IntervalSeconds(HeistData d, int tier) => CartelTiers.Of(d.AssaultIntervalSeconds, tier);
}

/// <summary>
/// The Drop point of a Job (docs/cartel.md "Who runs a job": two Jobs never share one): a random <c>cartel_drop</c> candidate that no other Job holds, at least <c>minFrom</c> from a point (the
/// objective) and <c>apart</c> from every drop point another Job holds, reserved in the book as <see cref="Key"/>.
/// </summary>
public static class JobDrops
{
    const string Prefix = CartelZones.DropKind + ":";

    /// <summary>The book's name of drop point number <paramref name="index"/> of <see cref="CartelMap.DropPoints"/>.</summary>
    public static string Key(int index) => Prefix + index;

    /// <summary>The drop points other Jobs hold now.</summary>
    public static IEnumerable<Vector3> Held(CartelMap map, JobBook book)
    {
        for (var i = 0; i < map.DropPoints.Count; i++) if (book.IsReserved(Key(i))) yield return map.DropPoints[i];
    }

    /// <summary>Picks and reserves a drop point for <paramref name="job"/>; null when none is left that keeps the rules.</summary>
    public static Vector3? Reserve(CartelMap map, JobBook book, Job job, Random rng, Vector3 from, float minFromM, float apartM)
    {
        var apart = CartelMap.Apart(Held(map, book).ToList(), apartM);
        var min2 = minFromM * minFromM;
        var free = new List<int>();
        for (var i = 0; i < map.DropPoints.Count; i++)
        {
            var p = map.DropPoints[i];
            var dx = p.X - from.X; var dz = p.Z - from.Z;
            if (!book.IsReserved(Key(i)) && dx * dx + dz * dz >= min2 && apart(p)) free.Add(i);
        }
        if (free.Count == 0) return null;
        var pick = free[rng.Next(free.Count)];
        return book.Reserve(job, Key(pick)) ? map.DropPoints[pick] : null;
    }
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
