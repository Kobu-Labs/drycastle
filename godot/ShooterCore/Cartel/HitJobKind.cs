using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a Hit asks the server to put at its spot (<see cref="IHitWorld.Spawn"/>).</summary>
/// <param name="Spot">The Hit spot: the Rival dealer stands there.</param>
/// <param name="Gunmen">Guards with guns.</param>
/// <param name="Enforcers">Guards with pipes.</param>
/// <param name="RingM">The guards stand on a ring this far round him.</param>
public readonly record struct HitCrewOrder(Vector3 Spot, int Gunmen, int Enforcers, float RingM);

/// <summary>A Hit's Rival crew in the world: the dealer (one marked member of it) and his guards.</summary>
public interface IHitCrew
{
    /// <summary>The dealer is still in the world, alive or lying dead.</summary>
    bool DealerPresent { get; }
    bool DealerAlive { get; }
    Vector3 DealerAt { get; }
    /// <summary>The crew goes, every member with it.</summary>
    void Remove();
}

/// <summary>What a Hit needs of the server: a Rival crew round its dealer, and the players' feet.</summary>
public interface IHitWorld
{
    /// <summary>A Rival crew at the order's spot hostile on sight to <paramref name="hostileTo"/>: the dealer (the Gang boss kind, his own look) and as many guards as the world takes. Null when even the dealer could not be put down.</summary>
    IHitCrew? Spawn(HitCrewOrder order, Func<ushort, bool> hostileTo);
    /// <summary>Where a living player on foot stands; null otherwise.</summary>
    Vector3? Feet(ushort player);
}

/// <summary>Where a Hit's marker points (machine <c>Hit.Target</c>).</summary>
public enum HitPhase : byte
{
    /// <summary>At the Hit spot: the party has not come near the dealer yet.</summary>
    Seek = 0,
    /// <summary>On the dealer himself, wherever he goes.</summary>
    Strike = 1,
}

/// <summary>
/// The Hit (docs/cartel.md "Simple jobs", #497): a Rival dealer stands outdoors at a random Hit spot with a Rival crew of Gunmen and Enforcers (counts by tier, more per extra party member) that attacks the
/// party on sight and anyone who attacks it. The dealer dying, by anyone's hand, is success; the timer and the core's failures stand. The spot is <c>min_contact_m</c> from the Contact and <c>apart_m</c>
/// from every point another Job holds (<see cref="CartelSpots"/>). The marker shows the spot until a party player comes within <c>reveal_m</c> of the dealer, then the dealer (<see cref="Spec"/>). When it
/// ends the crew goes. Killing its members pays nothing (rival_crew.json). Numbers in cartel.json <c>jobs.hit</c>.
/// </summary>
public sealed class HitJobKind : IJobKind
{
    public const string KindId = "hit";

    public static readonly StateMachineSpec<HitPhase> Spec = StateMachineSpec<HitPhase>
        .Define("Hit.Target", "Where a Hit's marker points: the Hit spot until a party player comes near the Rival dealer, then the dealer himself until he dies or the Job ends.", HitPhase.Seek)
        .Allow(HitPhase.Seek, HitPhase.Strike)
        .Note(HitPhase.Seek, "The marker is on the Hit spot; the objective reads \"Find the rival dealer\".")
        .Note(HitPhase.Strike, "A party player came within reveal_m of the dealer: the marker follows him; \"Kill the rival dealer\".")
        .Build();

    /// <summary>cartel.json <c>jobs.hit</c>.</summary>
    public sealed class Data
    {
        public string Name { get; set; } = "Hit";
        /// <summary>The timer per tier, tier 1 first: one number per tier of cartel.json.</summary>
        public float[] Seconds { get; set; } = { 300, 270, 240, 220, 200 };
        public float MinContactM { get; set; } = 150;
        public float ApartM { get; set; } = 60;
        /// <summary>Guards with guns per tier, for a party of one.</summary>
        public int[] Gunmen { get; set; } = { 1, 2, 2, 3, 4 };
        /// <summary>Guards with pipes per tier, for a party of one.</summary>
        public int[] Enforcers { get; set; } = { 2, 2, 3, 3, 4 };
        public int GunmenPerMate { get; set; } = 1;
        public int EnforcersPerMate { get; set; } = 0;
        public int MaxGuards { get; set; } = 10;
        public float RingM { get; set; } = 4;
        public float RevealM { get; set; } = 35;
    }

    readonly Data _data;
    readonly IJobParties _parties;
    readonly Func<CartelMap> _map;
    readonly Random _rng;
    readonly IHitWorld _world;

    public HitJobKind(CartelFile file, IJobParties parties, Func<CartelMap> map, Random rng, IHitWorld world)
    {
        _data = DataFile.Section<Data>(file.JobSection(KindId), "cartel.json: jobs.hit");
        var tiers = file.Tiers.Count;
        CartelTiers.Check(_data.Seconds, tiers, "cartel.json: jobs.hit.seconds", CartelTiers.Positive, "more than 0");
        CartelTiers.Check(_data.Gunmen, tiers, "cartel.json: jobs.hit.gunmen", CartelTiers.Count, "0 or more");
        CartelTiers.Check(_data.Enforcers, tiers, "cartel.json: jobs.hit.enforcers", CartelTiers.Count, "0 or more");
        static DataFileException Bad(string what) => new($"cartel.json: jobs.hit.{what}");
        if (_data.GunmenPerMate < 0 || _data.EnforcersPerMate < 0) throw Bad("gunmen_per_mate and enforcers_per_mate must not be negative");
        if (_data.MaxGuards < 0) throw Bad("max_guards must not be negative");
        if (!float.IsFinite(_data.RingM) || _data.RingM <= 0 || !float.IsFinite(_data.RevealM) || _data.RevealM <= 0) throw Bad("ring_m and reveal_m must be more than 0");
        if (!float.IsFinite(_data.MinContactM) || _data.MinContactM < 0 || !float.IsFinite(_data.ApartM) || _data.ApartM < 0) throw Bad("min_contact_m and apart_m must not be negative");
        _parties = parties; _map = map; _rng = rng; _world = world;
    }

    public string Id => KindId;
    public string Name => _data.Name;
    public float Seconds(int tier) => CartelTiers.Of(_data.Seconds, tier);

    /// <summary>The guards of a Hit of that tier for a party of <paramref name="partySize"/>: the tier's counts plus the per-mate ones for each member past the first, at most max_guards (Gunmen kept first).</summary>
    public (int Gunmen, int Enforcers) Guards(int tier, int partySize) =>
        CrewRules.Split(CartelTiers.Of(_data.Gunmen, tier), _data.GunmenPerMate, CartelTiers.Of(_data.Enforcers, tier), _data.EnforcersPerMate, partySize, _data.MaxGuards);

    public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
    {
        if (CartelSpots.Claim(job, book, _map(), CartelZones.HitKind, _rng, _data.MinContactM, _data.ApartM, out refusal) is not { } spot) return null;
        var party = new JobPartySet(_parties, job.Owner);
        var (gunmen, enforcers) = Guards(job.Tier, party.Count);
        if (_world.Spawn(new HitCrewOrder(spot, gunmen, enforcers, _data.RingM), party.Contains) is not { } crew) { refusal = "The rival crew could not be put down now (the world is full)."; return null; }
        return new Run(this, party, crew, spot);
    }

    /// <summary>One Hit's state: its crew and where its marker points.</summary>
    public sealed class Run : IJobObjective
    {
        readonly HitJobKind _k;
        readonly JobPartySet _party;
        readonly StateMachine<HitPhase> _phase = new(Spec);

        internal Run(HitJobKind kind, JobPartySet party, IHitCrew crew, Vector3 spot)
        {
            _k = kind; _party = party; Crew = crew; Spot = spot;
        }

        public IHitCrew Crew { get; }
        public Vector3 Spot { get; }
        public HitPhase Phase => _phase.Current;

        public JobStep Step(Job job, float dt)
        {
            if (!Crew.DealerPresent) return JobStep.Failed; // taken out of the world without dying (it never should while the Job runs)
            if (!Crew.DealerAlive) return JobStep.Succeeded; // by anyone's hand
            if (_phase.Is(HitPhase.Seek) && PartyNear()) _phase.Enter(HitPhase.Strike);
            return JobStep.Running;
        }

        bool PartyNear()
        {
            var at = Crew.DealerAt;
            foreach (var m in _party)
                if (_k._world.Feet(m) is { } f && new Vector2(f.X - at.X, f.Z - at.Z).Length() <= _k._data.RevealM) return true;
            return false;
        }

        public void End(Job job) => Crew.Remove();

        public string Objective(Job job) => _phase.Is(HitPhase.Seek) ? "Find the rival dealer" : "Kill the rival dealer";

        public Vector3? Marker(Job job) => _phase.Is(HitPhase.Seek) || !Crew.DealerPresent ? Spot : Crew.DealerAt;
    }
}
