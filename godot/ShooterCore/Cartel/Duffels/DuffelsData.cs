using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Movement;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// duffels.json: every number of the Duffel (GLOSSARY "Duffel", docs/cartel.md "Duffels"): the three kinds with their weight, the carrier's speed and the throw's reach, the pick-up reach, the throw key's
/// tap and charge, the arc, and the props. The defaults here are the shipped values, so a data directory without the file still plays. Parsed and checked once; a bad field throws with the field named.
/// </summary>
public sealed class DuffelsFile : IVersioned
{
    public int Version { get; set; } = 1;
    /// <summary>The kinds by their name (weed, cash, product); every kind must be there.</summary>
    public Dictionary<string, DuffelKindData> Kinds { get; set; } = new()
    {
        ["weed"] = new() { WeightKg = 8, SpeedScale = 0.9, ThrowM = 12, Colour = "#4f6b3a" },
        ["cash"] = new() { WeightKg = 15, SpeedScale = 0.8, ThrowM = 9, Colour = "#3d5a73" },
        ["product"] = new() { WeightKg = 25, SpeedScale = 0.7, ThrowM = 6, Colour = "#7a5b3a" },
    };
    /// <summary>Metres (flat) from the feet to a lying Duffel that the interact key picks up.</summary>
    public double ReachM { get; set; } = 2.0;
    /// <summary>Metres up or down from the feet within which a lying Duffel is in reach.</summary>
    public double ReachUpM { get; set; } = 1.8;
    /// <summary>A throw key held less than this (seconds) is a tap: the Duffel is put down at the feet.</summary>
    public double TapSeconds { get; set; } = 0.2;
    /// <summary>Seconds of holding past the tap for a full charge.</summary>
    public double ChargeSeconds { get; set; } = 1.0;
    /// <summary>The share of a kind's throw_m that the shortest throw (no charge) reaches.</summary>
    public double MinThrowShare { get; set; } = 0.5;
    /// <summary>Degrees the throw leaves above the look (a level look throws at this elevation); the launch speed is worked out for this elevation, from a standing eye down to flat ground.</summary>
    public double LiftDeg { get; set; } = 25;
    /// <summary>The throw's elevation is held between these (degrees): looking at the feet does not throw into the ground, looking up does not throw straight up.</summary>
    public double MinElevationDeg { get; set; } = -10;
    public double MaxElevationDeg { get; set; } = 65;
    /// <summary>Metres in front of the eye where a throw leaves the hands.</summary>
    public double HandAheadM { get; set; } = 0.4;
    /// <summary>A flight still going after this (seconds: off the map, into a hole) comes down where it is.</summary>
    public double MaxFlightSeconds { get; set; } = 5;
    /// <summary>Metres a flying Duffel is kept off a wall it struck before it falls straight down.</summary>
    public double WallGapM { get; set; } = 0.2;
    public DuffelView View { get; set; } = new();

    public static DuffelsFile Parse(string json) => DataFile.Parse<DuffelsFile>(json, "duffels.json").Validate();

    public static DuffelsFile Load(string path) => DataFile.Load<DuffelsFile>(path).Validate();

    /// <summary>The shipped numbers (the defaults of every field), checked.</summary>
    public static DuffelsFile Default => new DuffelsFile().Validate();

    /// <summary>The data of one kind (the file is checked to have all of them).</summary>
    public DuffelKindData Of(DuffelKind kind) => Kinds[DuffelRules.NameOf(kind)];

    /// <summary>What carrying a Duffel of this kind does to the carrier's movement: no sprint and the kind's speed (the same on the server and in the client's prediction).</summary>
    public MoveBurden BurdenOf(DuffelKind kind) => new((float)Of(kind).SpeedScale, NoSprint: true);

    DuffelsFile Validate()
    {
        static void Pos(double v, string field)
        {
            if (!double.IsFinite(v) || v <= 0) throw new DataFileException($"duffels.json: {field} must be more than 0");
        }
        foreach (var kind in DuffelRules.AllKinds)
        {
            var name = DuffelRules.NameOf(kind);
            if (!Kinds.TryGetValue(name, out var k)) throw new DataFileException($"duffels.json: kinds.{name} is missing");
            Pos(k.WeightKg, $"kinds.{name}.weight_kg"); Pos(k.ThrowM, $"kinds.{name}.throw_m");
            if (!(k.SpeedScale is > 0 and <= 1)) throw new DataFileException($"duffels.json: kinds.{name}.speed_scale must be more than 0 and at most 1");
        }
        Pos(ReachM, "reach_m"); Pos(ReachUpM, "reach_up_m"); Pos(TapSeconds, "tap_seconds"); Pos(ChargeSeconds, "charge_seconds"); Pos(MaxFlightSeconds, "max_flight_seconds");
        if (!(MinThrowShare is > 0 and <= 1)) throw new DataFileException("duffels.json: min_throw_share must be more than 0 and at most 1");
        if (!(LiftDeg is > 0 and < 90)) throw new DataFileException("duffels.json: lift_deg must be between 0 and 90");
        if (!(MinElevationDeg < MaxElevationDeg && MinElevationDeg > -90 && MaxElevationDeg < 90)) throw new DataFileException("duffels.json: min_elevation_deg must be below max_elevation_deg, both within 90");
        if (!double.IsFinite(HandAheadM) || HandAheadM < 0) throw new DataFileException("duffels.json: hand_ahead_m must be 0 or more");
        if (!double.IsFinite(WallGapM) || WallGapM < 0) throw new DataFileException("duffels.json: wall_gap_m must be 0 or more");
        if (View.Back.Length != 3 || View.Box.Length != 3) throw new DataFileException("duffels.json: view.back and view.box need 3 numbers");
        Pos(View.Scale, "view.scale");
        return this;
    }
}

/// <summary>One kind of Duffel: its weight, what it does to the carrier and how far it flies.</summary>
public sealed class DuffelKindData
{
    /// <summary>The weight (kg). The speed and the throw follow it; it is shown nowhere yet.</summary>
    public double WeightKg { get; set; }
    /// <summary>The carrier's walking speed is multiplied by this (and the carrier cannot sprint).</summary>
    public double SpeedScale { get; set; } = 1;
    /// <summary>Metres a fully charged throw goes on flat ground.</summary>
    public double ThrowM { get; set; }
    /// <summary>The pack model of the prop ("pack:synty-farm/..."); empty or missing pack: a box of colour.</summary>
    public string Model { get; set; } = "pack:synty-farm/SM_Prop_GrainBag_01";
    /// <summary>The box's colour when there is no model, and a tint for the kind.</summary>
    public string Colour { get; set; } = "#808080";
}

/// <summary>How a Duffel is drawn: on the carrier's back and on the ground.</summary>
public sealed class DuffelView
{
    /// <summary>Where a carried Duffel sits on the carrier, metres from the feet in the figure's frame (x right, y up, z back).</summary>
    public double[] Back { get; set; } = { 0, 1.25, 0.28 };
    /// <summary>The prop's scale (the farm grain bag is 0.85 m long).</summary>
    public double Scale { get; set; } = 0.8;
    /// <summary>The fallback box (metres: length, height, depth).</summary>
    public double[] Box { get; set; } = { 0.7, 0.3, 0.35 };
}
