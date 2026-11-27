using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a Delivery needs of the server: the Duffels, and the players' feet and interact key.</summary>
public interface IDeliveryWorld
{
    DuffelBook Duffels { get; }
    /// <summary>Where a living player on foot stands; null otherwise (dead, gone, in a vehicle).</summary>
    Vector3? Feet(ushort player);
    /// <summary>Whether the player holds the interact key now (its last input).</summary>
    bool UseHeld(ushort player);
}

/// <summary>
/// The Delivery (docs/cartel.md "Simple jobs", #494): its Duffel lies on a free spot of the Cartel pallet by the Contact (<see cref="CartelPallet"/>), for the party to pick up; a party player carrying it who stands within <c>radius_m</c> of the Job's Drop point and
/// holds the interact key <c>hold_seconds</c> (<see cref="InteractHold"/>) hands it over: success. A dropped Duffel lies for any of the party to pick up (the leader's death still fails the Job; so does the Duffel's loss, whatever made it: <see cref="JobFailReason.Objective"/>). A start needs a pallet on the map and a free spot on it (<see cref="CartelWords.Start"/>: "The pallet is full."); the spot is held until the Job ends. The
/// drop point is at least <c>min_contact_m</c> from the Contact and <c>apart_m</c> from every point another Job holds (<see cref="CartelSpots"/>). The Duffel belongs to the Job's party
/// (<see cref="Job.Party"/>). When it ends, whichever way, its Duffel goes (<see cref="DuffelBook.RemoveParty"/>, which unburdens its carrier).
/// Numbers in cartel.json <c>jobs.delivery</c>.
/// </summary>
public sealed class DeliveryJobKind : IJobKind
{
    public const string KindId = "delivery";

    /// <summary>cartel.json <c>jobs.delivery</c>.</summary>
    public sealed class Data
    {
        public string Name { get; set; } = "Delivery";
        /// <summary>The timer per tier, tier 1 first: one number per tier of cartel.json.</summary>
        public float[] Seconds { get; set; } = { 240, 210, 185, 165, 150 };
        /// <summary>What the Duffel holds (duffels.json kind: its weight slows the carrier).</summary>
        public string Duffel { get; set; } = "product";
        public float MinContactM { get; set; } = 150;
        public float ApartM { get; set; } = 40;
        public float RadiusM { get; set; } = 4;
        public float HoldSeconds { get; set; } = 2;
    }

    /// <summary>Metres from its spot within which a lying Duffel still counts as on the pallet.</summary>
    const float OnPalletM = 0.5f;

    readonly Data _data;
    readonly DuffelKind _duffel;
    readonly CartelPalletData _pallet;
    readonly Func<CartelMap> _map;
    readonly Random _rng;
    readonly IDeliveryWorld _world;

    public DeliveryJobKind(CartelFile file, Func<CartelMap> map, Random rng, IDeliveryWorld world)
    {
        _data = Read(file);
        _duffel = DuffelRules.Parse(_data.Duffel)!.Value;
        _pallet = file.Pallet;
        _map = map; _rng = rng; _world = world;
    }

    /// <summary>cartel.json <c>jobs.delivery</c>, checked (the client reads it too: the hand-over's radius and hold for its prompt and ring).</summary>
    public static Data Read(CartelFile file)
    {
        var data = DataFile.Section<Data>(file.JobSection(KindId), "cartel.json: jobs.delivery");
        CartelTiers.Check(data.Seconds, file.Tiers.Count, "cartel.json: jobs.delivery.seconds", CartelTiers.Positive, "more than 0");
        if (DuffelRules.Parse(data.Duffel) == null) throw new DataFileException($"cartel.json: jobs.delivery.duffel '{data.Duffel}' is not weed, cash or product");
        void Positive(float v, string what) { if (!float.IsFinite(v) || v <= 0) throw new DataFileException($"cartel.json: jobs.delivery.{what} must be more than 0"); }
        Positive(data.RadiusM, "radius_m"); Positive(data.HoldSeconds, "hold_seconds");
        if (!float.IsFinite(data.MinContactM) || data.MinContactM < 0 || !float.IsFinite(data.ApartM) || data.ApartM < 0) throw new DataFileException("cartel.json: jobs.delivery.min_contact_m and apart_m must not be negative");
        return data;
    }

    public string Id => KindId;
    public string Name => _data.Name;
    public Data Numbers => _data;
    public float Seconds(int tier) => CartelTiers.Of(_data.Seconds, tier);

    /// <summary>Every spot of the Cartel pallet is held by a running Job (the server's check before a start; false on a map without a pallet, which <see cref="Begin"/> refuses in its own words).</summary>
    public bool PalletFull(JobBook book) => _map().Pallet != null && CartelPallet.FreeSpot(book, _pallet) < 0;

    public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
    {
        refusal = null;
        var map = _map();
        if (map.Pallet is not { } pallet) { refusal = "This map has no pallet."; return null; }
        var spot = CartelPallet.Claim(job, book, _pallet);
        if (spot < 0) { refusal = CartelWords.Start.Text(JobStartRefusal.PalletFull); return null; }
        if (CartelSpots.Claim(job, book, map, CartelZones.DropKind, _rng, map.Contact?.Position, "the Contact", _data.MinContactM, _data.ApartM, out refusal) is not { } drop) return null;
        var at = pallet.SpotAt(_pallet, spot);
        if (_world.Duffels.Place(_duffel, job.Party, at, pallet.YawRad) is not { } duffel) { refusal = "The world is full: no duffel can be put on the pallet."; return null; }
        return new Run(this, job.Party, duffel, at, drop);
    }

    /// <summary>One Delivery's state: its Duffel and the pallet spot it lay on, its drop point and the hold under way there.</summary>
    public sealed class Run : IJobObjective
    {
        readonly DeliveryJobKind _k;
        readonly JobParty _party;
        InteractHold _hold;
        ushort _holder;

        internal Run(DeliveryJobKind kind, JobParty party, Duffel duffel, Vector3 spot, Vector3 drop)
        {
            _k = kind; _party = party; Duffel = duffel; Spot = spot; Drop = drop;
        }

        public Duffel Duffel { get; }
        /// <summary>Where the Duffel lay on the pallet.</summary>
        public Vector3 Spot { get; }
        public Vector3 Drop { get; }

        bool Present => _k._world.Duffels.Find(Duffel.Id) == Duffel;

        /// <summary>The Duffel still lies on its pallet spot (nobody has picked it up yet, or it was put back).</summary>
        bool OnPallet => Duffel.State == DuffelState.Lying && Vector3.DistanceSquared(Duffel.Position, Spot) <= OnPalletM * OnPalletM;

        /// <summary>The party player who carries the Duffel inside the drop radius, or 0.</summary>
        ushort CarrierAtDrop()
        {
            if (Duffel.State != DuffelState.Carried || !_party.Contains(Duffel.Carrier) || _k._world.Feet(Duffel.Carrier) is not { } feet) return 0;
            var flat = new Vector2(feet.X - Drop.X, feet.Z - Drop.Z);
            return flat.Length() <= _k._data.RadiusM ? Duffel.Carrier : (ushort)0;
        }

        public JobStep Step(Job job, float dt)
        {
            if (!Present) return JobStep.Failed; // gone from the world for any reason but the Job's own end (the Job's end removes it after the last Step): JobFailReason.Objective
            var at = CarrierAtDrop();
            if (at != _holder) { _hold.Reset(); _holder = at; }
            if (at == 0) return JobStep.Running;
            return _hold.Tick(_k._world.UseHeld(at), true, dt, _k._data.HoldSeconds) ? JobStep.Succeeded : JobStep.Running;
        }

        public void End(Job job) => _k._world.Duffels.RemoveParty(_party);

        public string Objective(Job job) => !Present ? ""
            : Duffel.State != DuffelState.Carried ? (OnPallet ? "Take the duffel from the pallet" : "Pick the duffel up")
            : CarrierAtDrop() != 0 ? $"Hold {JobText.Interact} to hand the duffel over"
            : "Take the duffel to the drop point";

        /// <summary>The pallet spot (the Duffel's own position) until a party player carries it, then the drop point.</summary>
        public Vector3? Marker(Job job) => !Present ? null : Duffel.State == DuffelState.Carried ? Drop : Duffel.Position;
    }
}
