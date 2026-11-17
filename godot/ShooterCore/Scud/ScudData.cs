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
    public ScudReloadData Reload { get; set; } = new();
    public ScudEventData Event { get; set; } = new();

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

        var ev = Event;
        NonNeg(ev.SpawnWeight, "event.spawn_weight"); NonNeg(ev.XpReward, "event.xp_reward");
        if (!(ev.SearchRadiusCells is >= 1 and <= 40)) throw new DataFileException("scud.json: event.search_radius_cells must be between 1 and 40");
        Pos(ev.DespawnMinutes, "event.despawn_minutes"); Pos(ev.CartCooldownSeconds, "event.cart_cooldown_seconds"); NonNeg(ev.MinPlayerDistanceM, "event.min_player_distance_m");
        NonNeg(ev.CartStandoffM, "event.cart_standoff_m"); Pos(ev.SpotClearanceM, "event.spot_clearance_m");

        var r = Reload;
        Pos(r.HoldSeconds, "reload.hold_seconds"); Pos(r.CartReachM, "reload.cart_reach_m"); Pos(r.PlayerReachM, "reload.player_reach_m"); Pos(r.MovedCancelM, "reload.moved_cancel_m");
        if (!(r.TransferShare is > 0 and < 1)) throw new DataFileException("scud.json: reload.transfer_share must be between 0 and 1");

        var b = Blast;
        NonNeg(b.ChainDelaySeconds, "blast.chain_delay_seconds");
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
    /// <summary>A loaded rocket cart in the damage chain of a Scud blast goes up this long after the blast that doomed it (plus the wave's travel), so a row of carts does not all go in one tick. Seconds.</summary>
    public double ChainDelaySeconds { get; set; } = 0.8;
}

/// <summary>scud.json "event": the Scud event (<see cref="ScudEventRun"/>, GLOSSARY "Scud event").</summary>
public sealed class ScudEventData
{
    /// <summary>The weight of this event in a future scheduler's draw (no scheduler runs events yet: <c>/event scud</c> starts it). 0: never drawn.</summary>
    public double SpawnWeight { get; set; } = 1;
    /// <summary>The search area's radius, in grid cells of the tactical map (<see cref="ScudSearch.RadiusM"/>).</summary>
    public int SearchRadiusCells { get; set; } = 9;
    /// <summary>Nobody boarded the truck in this long: it is removed with its carts, no reward. Minutes.</summary>
    public double DespawnMinutes { get; set; } = 20;
    /// <summary>The XP of the player who first boards the truck (<c>XpReason.EventWin</c>).</summary>
    public double XpReward { get; set; } = 400;
    /// <summary>The truck's place is at least this far from every human player. Metres.</summary>
    public double MinPlayerDistanceM { get; set; } = 250;
    /// <summary>A bunker's cart that was emptied or destroyed is replaced by a new loaded one after this long. Seconds.</summary>
    public double CartCooldownSeconds { get; set; } = 600;
    /// <summary>A bunker's cart stands this far beyond the bunker's footprint (its rear end), metres.</summary>
    public double CartStandoffM { get; set; } = 3;
    /// <summary>A place for the truck or a cart needs its footprint grown by this much to be clear of solids (walls, trees, rocks), metres.</summary>
    public double SpotClearanceM { get; set; } = 1.5;
}

/// <summary>scud.json "reload": loading a rocket from a rocket cart onto an empty truck (<see cref="ScudReloadRules"/>, the launcher's Loading state).</summary>
public sealed class ScudReloadData
{
    /// <summary>The interact key is held this long near a truck with an empty rack and a loaded cart to start the loading (a tap still gets in or out). Seconds.</summary>
    public double HoldSeconds { get; set; } = 1.0;
    /// <summary>A loaded cart within this gap of the truck's hull (box to box, hitched or not) can be unloaded onto it. Metres.</summary>
    public double CartReachM { get; set; } = 5;
    /// <summary>A player on foot within this of the truck's hull may start it (one in the truck always may). Metres.</summary>
    public double PlayerReachM { get; set; } = 4;
    /// <summary>The truck or the cart moving this far from where the loading began cancels it. Metres.</summary>
    public double MovedCancelM { get; set; } = 1.0;
    /// <summary>The share of the reload (launcher.reload_seconds) the transfer waits for: until then the cart still holds its rocket and the loading can be cancelled; after it the rocket is on the rack. 0 to 1, exclusive.</summary>
    public double TransferShare { get; set; } = 0.5;
}
