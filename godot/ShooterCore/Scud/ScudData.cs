namespace Drycastle.ShooterCore;

/// <summary>
/// scud.json: every number of the Scud truck (GLOSSARY "Scud truck"): the launcher's timings, the targeting screen, the missile's flight and the blast. The defaults here are the
/// shipped values, so a data directory without the file still plays. Parsed and checked once; a bad field throws with the file and the field named.
/// </summary>
public sealed class ScudFile : IVersioned
{
    public int Version { get; set; } = 1;
    public ScudLauncherData Launcher { get; set; } = new();
    public ScudTargetingData Targeting { get; set; } = new();
    public ScudMissileData Missile { get; set; } = new();
    public ScudBlastData Blast { get; set; } = new();

    public static ScudFile Parse(string json) => DataFile.Parse<ScudFile>(json, "scud.json").Validate();

    public static ScudFile Load(string path) => DataFile.Load<ScudFile>(path).Validate();

    /// <summary>The shipped numbers (the defaults of every field), checked.</summary>
    public static ScudFile Default => new ScudFile().Validate();

    ScudFile Validate()
    {
        void Pos(double v, string field)
        {
            if (!double.IsFinite(v) || v <= 0) throw new DataFileException($"scud.json: {field} must be more than 0");
        }
        void NonNeg(double v, string field)
        {
            if (!double.IsFinite(v) || v < 0) throw new DataFileException($"scud.json: {field} must be 0 or more");
        }

        var l = Launcher;
        Pos(l.ErectSeconds, "launcher.erect_seconds"); Pos(l.IgnitionSeconds, "launcher.ignition_seconds"); Pos(l.SpentHoldSeconds, "launcher.spent_hold_seconds");
        Pos(l.LowerSeconds, "launcher.lower_seconds"); Pos(l.ReloadSeconds, "launcher.reload_seconds"); Pos(l.ProposalTimeoutSeconds, "launcher.proposal_timeout_seconds");
        NonNeg(l.MinRangeM, "launcher.min_range_m"); Pos(l.MovedCancelM, "launcher.moved_cancel_m"); NonNeg(l.MaxSpeedMps, "launcher.max_speed_mps");
        Pos(l.ViewStaleSeconds, "launcher.view_stale_seconds");

        var t = Targeting;
        Pos(t.AltitudeM, "targeting.altitude_m"); Pos(t.PanSpeedMps, "targeting.pan_speed_mps"); Pos(t.ViewHz, "targeting.view_hz");
        if (!(t.FovDeg is > 5 and < 120)) throw new DataFileException("scud.json: targeting.fov_deg must be between 5 and 120");
        if (!(t.ZoomMin >= 1) || !(t.ZoomMax > t.ZoomMin)) throw new DataFileException("scud.json: targeting.zoom_min must be at least 1 and below zoom_max");
        Pos(t.ZoomStep, "targeting.zoom_step");
        if (t.ZoomStep <= 1) throw new DataFileException("scud.json: targeting.zoom_step is a factor above 1");

        var m = Missile;
        Pos(m.BoostSeconds, "missile.boost_seconds"); Pos(m.BoostAltitudeM, "missile.boost_altitude_m"); Pos(m.FullRangeM, "missile.full_range_m");
        if (!(m.FlightMinSeconds > m.BoostSeconds) || !(m.FlightMaxSeconds >= m.FlightMinSeconds))
            throw new DataFileException("scud.json: missile.flight_min_seconds must be more than boost_seconds and not above flight_max_seconds");
        if (!(m.DiveDeg is >= 30 and <= 89)) throw new DataFileException("scud.json: missile.dive_deg must be between 30 and 89");
        NonNeg(m.WarnSeconds, "missile.warn_seconds"); NonNeg(m.WarnRadiusM, "missile.warn_radius_m");
        if (m.WarnSeconds >= m.FlightMinSeconds) throw new DataFileException("scud.json: missile.warn_seconds must be shorter than the shortest flight (flight_min_seconds)");

        var b = Blast;
        Pos(b.LethalRadiusM, "blast.lethal_radius_m");
        if (!(b.DamageRadiusM > b.LethalRadiusM)) throw new DataFileException("scud.json: blast.damage_radius_m must be more than lethal_radius_m");
        NonNeg(b.OuterDamage, "blast.outer_damage"); NonNeg(b.EdgeDamage, "blast.edge_damage");
        if (b.EdgeDamage > b.OuterDamage) throw new DataFileException("scud.json: blast.edge_damage must not exceed outer_damage (the damage falls off outwards)");
        Pos(b.VehicleDestroyRadiusM, "blast.vehicle_destroy_radius_m");
        if (!(b.VehicleDamageRadiusM > b.VehicleDestroyRadiusM)) throw new DataFileException("scud.json: blast.vehicle_damage_radius_m must be more than vehicle_destroy_radius_m");
        NonNeg(b.VehicleOuterDamage, "blast.vehicle_outer_damage"); NonNeg(b.VehicleEdgeDamage, "blast.vehicle_edge_damage");
        if (b.VehicleEdgeDamage > b.VehicleOuterDamage) throw new DataFileException("scud.json: blast.vehicle_edge_damage must not exceed vehicle_outer_damage");
        if (!(b.GlassRadiusM >= b.DamageRadiusM)) throw new DataFileException("scud.json: blast.glass_radius_m must reach at least as far as damage_radius_m");
        Pos(b.FxRadiusM, "blast.fx_radius_m"); Pos(b.WaveSpeedMps, "blast.wave_speed_mps"); Pos(b.ShakeRangeM, "blast.shake_range_m");
        if (!(b.ShakeAmplitude is > 0 and <= 1)) throw new DataFileException("scud.json: blast.shake_amplitude must be more than 0 and at most 1");
        Pos(b.ScorchRadiusM, "blast.scorch_radius_m");
        return this;
    }
}

/// <summary>scud.json "launcher": the timings of the launcher machine (<see cref="ScudLauncher"/>) and the rules of the targeting.</summary>
public sealed class ScudLauncherData
{
    /// <summary>The rack rises, the truck is immobilised; the launch follows. Seconds.</summary>
    public double ErectSeconds { get; set; } = 8;
    /// <summary>The ignition before the missile leaves the rack. Seconds.</summary>
    public double IgnitionSeconds { get; set; } = 2;
    /// <summary>The empty rack stays up this long after the launch before it lowers (the smoke clears). Seconds.</summary>
    public double SpentHoldSeconds { get; set; } = 2.5;
    /// <summary>A full lowering of the rack (from 90 degrees); an aborted erect lowers at the same rate from where it got to. Seconds.</summary>
    public double LowerSeconds { get; set; } = 6;
    /// <summary>A reload, empty to stowed. Seconds.</summary>
    public double ReloadSeconds { get; set; } = 10;
    /// <summary>A proposal nobody confirms is dropped after this long. Seconds.</summary>
    public double ProposalTimeoutSeconds { get; set; } = 60;
    /// <summary>The nearest point the crew may lock: their own truck must not be in the blast. Metres from the truck.</summary>
    public double MinRangeM { get; set; } = 150;
    /// <summary>The truck moving this far from where the point was locked cancels the proposal. Metres.</summary>
    public double MovedCancelM { get; set; } = 0.75;
    /// <summary>The truck must be slower than this to start erecting (it brakes to a stop; a rolling truck would drift under its own rack). Metres a second.</summary>
    public double MaxSpeedMps { get; set; } = 0.5;
    /// <summary>The operator's camera is no longer shown to the others after this long without an update. Seconds.</summary>
    public double ViewStaleSeconds { get; set; } = 3;
}

/// <summary>scud.json "targeting": the operator's bird's-eye infrared screen.</summary>
public sealed class ScudTargetingData
{
    /// <summary>The camera's height over the truck, looking straight down. Metres.</summary>
    public double AltitudeM { get; set; } = 600;
    /// <summary>The camera's field of view at zoom 1. Degrees.</summary>
    public double FovDeg { get; set; } = 45;
    public double ZoomMin { get; set; } = 1;
    public double ZoomMax { get; set; } = 6;
    /// <summary>One notch of the mouse wheel multiplies or divides the zoom by this.</summary>
    public double ZoomStep { get; set; } = 1.25;
    /// <summary>How fast the movement keys pan the camera at zoom 1 (less when zoomed in). Metres a second.</summary>
    public double PanSpeedMps { get; set; } = 300;
    /// <summary>How often the operator's camera is sent to the others in the truck. Per second.</summary>
    public double ViewHz { get; set; } = 5;
}

/// <summary>scud.json "missile": the planned flight (<see cref="ScudFlightPlan"/>) and who is warned.</summary>
public sealed class ScudMissileData
{
    /// <summary>The vertical boost. Seconds.</summary>
    public double BoostSeconds { get; set; } = 3;
    /// <summary>The height the boost reaches over the rack. Metres.</summary>
    public double BoostAltitudeM { get; set; } = 350;
    /// <summary>The whole flight takes this long for a target at the minimum range, and <see cref="FlightMaxSeconds"/> for one at <see cref="FullRangeM"/> or more; between, in proportion. Seconds.</summary>
    public double FlightMinSeconds { get; set; } = 15;
    public double FlightMaxSeconds { get; set; } = 20;
    public double FullRangeM { get; set; } = 4000;
    /// <summary>The angle below the horizon the missile arrives at. Degrees.</summary>
    public double DiveDeg { get; set; } = 70;
    /// <summary>The siren and the marker on the tactical map start this long before the impact. Seconds.</summary>
    public double WarnSeconds { get; set; } = 8;
    /// <summary>Players within this distance of the target are warned. Metres.</summary>
    public double WarnRadiusM { get; set; } = 600;
}

/// <summary>scud.json "blast": what the impact does (<see cref="ScudBlastRules"/>).</summary>
public sealed class ScudBlastData
{
    /// <summary>Everyone inside dies, in a vehicle or not, spawn protection or not (a blast shelter still protects). Metres.</summary>
    public double LethalRadiusM { get; set; } = 60;
    /// <summary>Beyond the lethal radius a player takes <see cref="OuterDamage"/>, falling off to <see cref="EdgeDamage"/> here; nothing past it. Metres.</summary>
    public double DamageRadiusM { get; set; } = 150;
    public double OuterDamage { get; set; } = 90;
    public double EdgeDamage { get; set; } = 10;
    /// <summary>A vehicle whose hull is this close is destroyed. Metres.</summary>
    public double VehicleDestroyRadiusM { get; set; } = 60;
    /// <summary>Beyond the destroy radius a vehicle takes <see cref="VehicleOuterDamage"/> hit points, falling off to <see cref="VehicleEdgeDamage"/> here. Metres.</summary>
    public double VehicleDamageRadiusM { get; set; } = 150;
    public double VehicleOuterDamage { get; set; } = 1000;
    public double VehicleEdgeDamage { get; set; } = 100;
    /// <summary>Every glass pane this close shatters, windows and vehicles' alike. Metres.</summary>
    public double GlassRadiusM { get; set; } = 250;
    /// <summary>The size of the blast the client draws (the fireball, the dust and the Blast wave scale with it); the biggest in the game. Metres.</summary>
    public double FxRadiusM { get; set; } = 90;
    /// <summary>The speed the Blast wave leaves at, which also times when the sound and the shake arrive far away. Metres a second.</summary>
    public double WaveSpeedMps { get; set; } = 260;
    /// <summary>The camera shake reaches this far (the whole map), scaled down with the distance. Metres.</summary>
    public double ShakeRangeM { get; set; } = 3000;
    /// <summary>The strongest shake (at the blast), 0 to 1.</summary>
    public double ShakeAmplitude { get; set; } = 0.45;
    /// <summary>The burnt ground left at the impact. Metres.</summary>
    public double ScorchRadiusM { get; set; } = 32;
}
