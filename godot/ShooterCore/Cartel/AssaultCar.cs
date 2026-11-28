using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// A Heist's Assault van (cartel.json <c>assault_car</c>; GLOSSARY "Assault", docs/cartel.md "Assaults"): a Rival crew's vehicle that drives like a chase car (the same unit, <c>Chase.Car</c>, the chase's own
/// driving numbers of the Heist's <c>chase</c> section) with its own vehicle, skin, crew posts, spawn rule and give-up distance. Lengths metres, times seconds. The defaults are the shipped numbers (first guesses, nothing was played).
/// </summary>
public sealed class AssaultCarData
{
    /// <summary>The vehicle type of vehicles.json an Assault is and its skin (an index of that type's <c>skins</c>; 0 is the player's Cartel van).</summary>
    public string VehicleType { get; set; } = "cartel_van";
    public int Skin { get; set; } = 1;

    /// <summary>
    /// Where it is put down: a road or open drivable ground point whose drivable way to the Cartel van is in the first band [min, max] (the length of the way the vehicle would drive), at least
    /// <see cref="SpawnPlayerMinM"/> from every player, clear of vehicles and <see cref="SpawnApartM"/> from the other Rival vehicles. No out-of-sight rule: the drive in is the warning. When no point fits the first band the
    /// next, wider one is tried (and so on): an Assault is sent rather than none. No band reaches past <see cref="GiveUpM"/> (a van put down there would give up at once).
    /// </summary>
    public float[][] SpawnBandsM { get; set; } = { new[] { 250f, 400f }, new[] { 200f, 600f }, new[] { 100f, 600f } };
    public float SpawnPlayerMinM { get; set; } = 150;
    public float SpawnApartM { get; set; } = 25;
    /// <summary>The road points looked at: one every this many metres; the open ground ones: one every <see cref="SpawnRingStepDeg"/> degrees on rings <see cref="SpawnRingStepM"/> apart round the van.</summary>
    public float SpawnRoadStepM { get; set; } = 30;
    public float SpawnRingStepDeg { get; set; } = 20;
    public float SpawnRingStepM { get; set; } = 50;
    /// <summary>At most this many candidates have their way worked out (a route plan each), ONE budget for all the bands together (the rest are not looked at).</summary>
    public int SpawnChecks { get; set; } = 12;
    /// <summary>The half width a spot and the way from it are checked for (the van's, vehicles.json half_width 1.32, and a margin).</summary>
    public float SpawnHalfWidthM { get; set; } = 1.45f;

    /// <summary>The Assault gives up (is removed with its crew) when it, or its crew on foot, stays more than this from the Cartel van for <see cref="GiveUpSeconds"/>: well beyond its spawn band.</summary>
    public float GiveUpM { get; set; } = 600;
    public float GiveUpSeconds { get; set; } = 20;

    /// <summary>
    /// Stalled: the newest Assault whose crew still rides in its vehicle and that got no <see cref="StallProgressM"/> closer to the van in <see cref="StallSeconds"/> (within <see cref="StallNearM"/> of it counts
    /// as close enough: it is on the van's tail). It stops counting towards max_assaults_alive, the next Assault is sent right away, and it is removed with its crew once it is more than
    /// <see cref="RemoveFarM"/> from every player and no player can see it.
    /// </summary>
    public float StallProgressM { get; set; } = 10;
    public float StallSeconds { get; set; } = 10;
    public float StallNearM { get; set; } = 40;
    public float RemoveFarM { get; set; } = 150;
    /// <summary>How often a stalled Assault is looked at for removal (the sight check is a ray from every player).</summary>
    public float RemoveCheckSeconds { get; set; } = 1;

    /// <summary>
    /// The crew posts in the cargo bay, in the vehicle's frame (x right, y up, z towards the REAR; the van's game frame, converted from scripts/vehicles/specs/cartel_van.json's source frame as the standardizer does it: z_game = 0.074 - z_src, x mirrored; the bulkhead at src z 0.271 is game z -0.197, the rear doors' hinge at src -2.295 is 2.369, the bay floor y 0.73):
    /// points on the bay floor. Riders after the cab's two seats take them in order; drawn seated (the figure's hips 0.42 above its feet), never shooting while they ride.
    /// </summary>
    public float[][] BayPosts { get; set; } =
    {
        new[] { -0.5f, 0.73f, 0.45f }, new[] { 0.5f, 0.73f, 0.45f }, new[] { -0.5f, 0.73f, 1.35f }, new[] { 0.5f, 0.73f, 1.35f },
    };
    /// <summary>A bay rider steps out (and walks back to) a point <see cref="BayExitZ"/> along the van's frame (z, rear positive; the van's tail is at 2.53), at the post's own x, on the ground: out of the rear doors.</summary>
    public float BayExitZ { get; set; } = 3.6f;

    /// <summary>The chase car's drive numbers with this vehicle, skin and give-up distance: what an Assault's unit runs on.</summary>
    public ChaseData Drive(ChaseData chase)
    {
        var d = chase.Copy();
        d.VehicleType = VehicleType; d.Skin = Skin; d.GiveUpM = GiveUpM; d.GiveUpSeconds = GiveUpSeconds;
        return d;
    }

    public void Validate(string what)
    {
        DataFileException Bad(string text) => new($"{what}: {text}");
        if (string.IsNullOrWhiteSpace(VehicleType)) throw Bad("vehicle_type names a vehicles.json type");
        if (Skin < 0) throw Bad("skin must be 0 or more");
        if (SpawnBandsM.Length == 0) throw Bad("spawn_bands_m needs at least one [min, max] band");
        for (var i = 0; i < SpawnBandsM.Length; i++)
        {
            var b = SpawnBandsM[i];
            if (b.Length != 2 || !float.IsFinite(b[0]) || !float.IsFinite(b[1]) || b[0] <= 0 || b[1] < b[0]) throw Bad($"spawn_bands_m[{i}] must be [min, max] with 0 < min <= max");
            if (b[1] > GiveUpM) throw Bad($"spawn_bands_m[{i}] reaches {b[1]:F0} m, past give_up_m {GiveUpM:F0}: an Assault put down there would give up at once");
            if (i > 0 && (b[0] > SpawnBandsM[i - 1][0] || b[1] < SpawnBandsM[i - 1][1])) throw Bad($"spawn_bands_m[{i}] must contain the band before it (a wider band)");
        }
        foreach (var (v, name) in new[] { (SpawnPlayerMinM, "spawn_player_min_m"), (SpawnApartM, "spawn_apart_m"), (RemoveFarM, "remove_far_m"), (StallNearM, "stall_near_m") })
            if (!float.IsFinite(v) || v < 0) throw Bad($"{name} must be 0 or more");
        foreach (var (v, name) in new[] { (SpawnRoadStepM, "spawn_road_step_m"), (SpawnRingStepDeg, "spawn_ring_step_deg"), (SpawnRingStepM, "spawn_ring_step_m"), (SpawnHalfWidthM, "spawn_half_width_m"), (GiveUpM, "give_up_m"), (GiveUpSeconds, "give_up_seconds"),
                     (StallProgressM, "stall_progress_m"), (StallSeconds, "stall_seconds"), (RemoveCheckSeconds, "remove_check_seconds") })
            if (!float.IsFinite(v) || v <= 0) throw Bad($"{name} must be more than 0");
        if (SpawnChecks < 1) throw Bad("spawn_checks must be at least 1");
        if (!float.IsFinite(BayExitZ)) throw Bad("bay_exit_z_m must be a number");
        for (var i = 0; i < BayPosts.Length; i++)
            if (BayPosts[i].Length != 3 || BayPosts[i].Any(v => !float.IsFinite(v))) throw Bad($"bay_posts[{i}] must be [x, y, z]");
    }
}

/// <summary>The rules of an Assault van's crew that are plain numbers (docs/cartel.md "Assaults").</summary>
public static class AssaultRules
{
    /// <summary>
    /// The kinds of the crew in the order they take their slots (a seat of the vehicle first, then the bay's posts): the driver (slot 0) is an Enforcer when the crew has one, the Gunmen follow (the cab's other seat
    /// is a window: that Gunman shoots; the bay's riders do not), any Enforcer after the first last. The server cuts it to the room the vehicle has.
    /// </summary>
    public static List<GangMemberKind> SeatOrder(int gunmen, int enforcers)
    {
        var kinds = new List<GangMemberKind>(gunmen + enforcers);
        if (enforcers > 0) kinds.Add(GangMemberKind.Enforcer);
        for (var i = 0; i < gunmen; i++) kinds.Add(GangMemberKind.Gunman);
        for (var i = 1; i < enforcers; i++) kinds.Add(GangMemberKind.Enforcer);
        return kinds;
    }
}

/// <summary>Where one Assault's progress to the Cartel van is (machine <c>Assault.Stall</c>).</summary>
public enum AssaultStallState : byte { Idle, Watching, Stalled }

/// <summary>
/// Whether an Assault makes headway (docs/cartel.md "Assaults"), pure: only the NEWEST Assault whose crew still rides is watched. It gets stall_progress_m closer to the Cartel van in stall_seconds, or it is
/// Stalled (the fleet then sends the next Assault right away and stops counting this one). Within stall_near_m of the van is close enough (on its tail). A stalled Assault stays so until its crew is out.
/// </summary>
public sealed class AssaultStall
{
    public static readonly StateMachineSpec<AssaultStallState> Spec = StateMachineSpec<AssaultStallState>
        .Define("Assault.Stall", "Whether an Assault van makes headway to the Cartel van: only the newest Assault whose crew still rides is watched; no stall_progress_m closer in stall_seconds is stalled (the next Assault is sent right away, this one no longer counts towards the cap and goes once it is far from every player and unseen).", AssaultStallState.Idle)
        .Allow(AssaultStallState.Idle, AssaultStallState.Watching)
        .Allow(AssaultStallState.Watching, AssaultStallState.Idle, AssaultStallState.Stalled)
        .Allow(AssaultStallState.Stalled, AssaultStallState.Idle)
        .Note(AssaultStallState.Idle, "Not watched: a newer Assault whose crew rides exists, or the crew is out (on foot), or it just spawned.")
        .Note(AssaultStallState.Watching, "The newest riding Assault: the window (clock: stall_seconds) starts over each time it gets stall_progress_m closer to the van, or is within stall_near_m of it. Out of time: Stalled.")
        .Note(AssaultStallState.Stalled, "Made no headway: counts no more towards max_assaults_alive (the next was sent at once); removed with its crew when more than remove_far_m from every player and out of every player's sight. Ends when the crew gets out.")
        .Build();

    readonly StateMachine<AssaultStallState> _fsm = new(Spec);
    readonly AssaultCarData _d;
    float _ref;

    public AssaultStall(AssaultCarData d) { _d = d; }

    public AssaultStallState State => _fsm.Current;
    /// <summary>It counts towards max_assaults_alive (while in the fight): every state but Stalled.</summary>
    public bool Counts => !_fsm.Is(AssaultStallState.Stalled);

    /// <summary>One tick. <paramref name="riding"/>: the crew sits in the vehicle; <paramref name="newest"/>: it is the newest Assault that rides; <paramref name="distM"/>: to the Cartel van. True once, the tick it becomes Stalled.</summary>
    public bool Step(bool riding, bool newest, float distM, float dt)
    {
        switch (_fsm.Current)
        {
            case AssaultStallState.Idle:
                if (riding && newest) { _ref = distM; _fsm.Enter(AssaultStallState.Watching, _d.StallSeconds); }
                return false;
            case AssaultStallState.Watching:
                if (!riding || !newest) { _fsm.Enter(AssaultStallState.Idle); return false; }
                if (distM <= _d.StallNearM || distM <= _ref - _d.StallProgressM) { _ref = distM; _fsm.Restart(_d.StallSeconds); return false; }
                if (!_fsm.Tick(dt)) return false;
                _fsm.Enter(AssaultStallState.Stalled);
                return true;
            default:
                if (!riding) _fsm.Enter(AssaultStallState.Idle);
                return false;
        }
    }
}

/// <summary>An Assault's place: its ground point, which way it faces, the length of the way it would drive to the van, the band it was found in (0: the first) and the road place when on a road.</summary>
public readonly record struct AssaultSpot(RoadPlace? Place, Vector2 Ground, float Yaw, float WayM, int Band);

/// <summary>Where an Assault van is put down, pure (docs/cartel.md "Assaults"): the rules of the spot.</summary>
public static class AssaultSpawn
{
    /// <summary>
    /// A spot for a new Assault, or null (with <paramref name="why"/>) when none fits. Band by band (the first is the wanted one, the others widen it): a road point (every spawn_road_step_m) or an open ground point
    /// (on rings round <paramref name="target"/>, where <paramref name="open"/> says a van can stand) that is at least spawn_player_min_m from every player, spawn_apart_m from the other Rival vehicles
    /// (<paramref name="rivals"/>), not <paramref name="blocked"/> (a vehicle beside it) and whose drivable way to <paramref name="target"/> (<paramref name="wayM"/>: its length, null when none) is in the band. Candidates are
    /// looked at in random order, at most spawn_checks route plans in all the bands together (one budget: a band that uses it up leaves none to the wider ones); the first that fits is the spot (it faces along the road, or straight, towards the target).
    /// </summary>
    public static AssaultSpot? Pick(RoadGraph roads, Vector2 target, IReadOnlyList<Vector2> players, IReadOnlyList<Vector2> rivals, AssaultCarData d, Func<Vector2, bool> blocked,
        Func<Vector2, bool>? open, Func<Vector2, float?> wayM, Random rng, out string why)
    {
        var min2 = d.SpawnPlayerMinM * d.SpawnPlayerMinM; var apart2 = d.SpawnApartM * d.SpawnApartM;
        bool Fits(Vector2 ground)
        {
            foreach (var pl in players) if (Vector2.DistanceSquared(pl, ground) < min2) return false;
            foreach (var c in rivals) if (Vector2.DistanceSquared(c, ground) < apart2) return false;
            return !blocked(ground);
        }
        var ways = new Dictionary<(int, int), float?>();
        var plans = 0; // the route plans made, over all the bands: the budget is spawn_checks
        // the way from p (a plan is paid from the budget; a point planned before is free); false when the budget is spent
        bool TryWay(Vector2 p, out float? way)
        {
            var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y));
            if (!ways.TryGetValue(key, out way))
            {
                if (plans >= d.SpawnChecks) return false;
                plans++;
                ways[key] = way = wayM(p);
            }
            return true;
        }
        var seen = 0;
        for (var b = 0; b < d.SpawnBandsM.Length; b++)
        {
            var (lo, hi) = (d.SpawnBandsM[b][0], d.SpawnBandsM[b][1]);
            var cands = new List<(RoadPlace? Place, Vector2 Ground)>();
            if (!roads.IsEmpty)
                foreach (var rp in roads.PointsAlong(d.SpawnRoadStepM, target - new Vector2(hi, hi), target + new Vector2(hi, hi), 0))
                    if (Vector2.Distance(rp.Ground, target) <= hi && Fits(rp.Ground)) cands.Add((rp.Place, rp.Ground));
            if (open != null)
                for (var r = Math.Max(lo * 0.5f, d.SpawnRingStepM); r <= hi; r += d.SpawnRingStepM)
                    for (var deg = 0f; deg < 360f; deg += d.SpawnRingStepDeg)
                    {
                        var a = deg * MathF.PI / 180f;
                        var p = target + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
                        if (Fits(p) && open(p)) cands.Add((null, p));
                    }
            seen += cands.Count;
            for (var i = cands.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (cands[i], cands[j]) = (cands[j], cands[i]); }
            for (var i = 0; i < cands.Count; i++)
            {
                if (!TryWay(cands[i].Ground, out var found)) break;
                if (found is not { } way || way < lo || way > hi) continue;
                var (place, ground) = cands[i];
                var toTarget = RoadPath.Yaw(target - ground);
                var yaw = toTarget;
                if (place is { } rpl)
                {
                    var fwd = roads.YawAt(rpl, true); var bwd = roads.YawAt(rpl, false);
                    yaw = MathF.Abs(FlightMath.Wrap(fwd - toTarget)) <= MathF.Abs(FlightMath.Wrap(bwd - toTarget)) ? fwd : bwd;
                }
                why = "";
                return new AssaultSpot(place, ground, yaw, way, b);
            }
        }
        why = seen == 0
            ? $"no road or open ground point is {d.SpawnPlayerMinM:F0} m from every player and clear of vehicles in any band up to {d.SpawnBandsM[^1][1]:F0} m"
            : $"{seen} candidate point(s) in the bands, {plans} way(s) looked at, none within {d.SpawnBandsM[^1][0]:F0} to {d.SpawnBandsM[^1][1]:F0} m of drivable way from the van";
        return null;
    }
}
