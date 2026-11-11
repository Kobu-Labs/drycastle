using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Stationary target dummies (#148) on the server: planning (ground-checked against the map colliders), their snapshot
/// entries, the roster sent on join, the kill credit and the roadkill path. Damage itself runs through
/// <see cref="MatchSim.ApplyDamage"/>, which routes dummy ids to <see cref="TargetDummies"/>, so every weapon path that
/// iterates <see cref="NetServer.LiveTargets"/> reaches them without a second code path.
/// </summary>
public partial class NetServer
{
    /// <summary>Planned once after the spawn list is validated (--targets N; 0 plants none).</summary>
    void PlanTargets()
    {
        var n = MatchConfig.Targets;
        if (n <= 0 || Spawns.Count == 0)
        {
            if (n > 0) GD.PrintErr("server: --targets needs spawn points; no target dummies planted");
            return;
        }
        var space = World.GetWorld3D().DirectSpaceState;
        var centre = new NVec3((float)Spawns.Average(s => s.X), 0, (float)Spawns.Average(s => s.Z));
        var top = Spawns.Max(s => s.Y) + 60f;
        var bottom = Spawns.Min(s => s.Y) - 60f;

        // The first surface from above: the ground, or a roof where the ray meets one first. Steep faces are rejected.
        float? GroundY(float x, float z)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(x, top, z), new Vector3(x, bottom, z), CollisionLayers.World));
            if (hit.Count == 0) return null;
            var normal = (Vector3)hit["normal"];
            return normal.Y >= 0.7f ? ((Vector3)hit["position"]).Y : null;
        }

        var spots = TargetPlacement.Plan(centre, n, GroundY);
        _match.SetTargets(spots);
        GD.Print($"server: planted {spots.Count}/{n} target dummies (rings {string.Join("/", TargetPlacement.RingsMetres)} m; cover and wall spots not generated)");
    }

    /// <summary>Living dummies as hit targets (hitscan, pellets, explosions, the vehicle gun and the turret all read these).</summary>
    IEnumerable<HitTarget> TargetHitTargets() =>
        _match.Targets.All.Where(d => d.Alive).Select(d => new HitTarget(d.Id, d.Spot.Feet, d.Spot.Pose, d.Spot.Yaw));

    /// <summary>Dummies for the UAV: visible to the enemy pings like anyone, with the default visibility.</summary>
    IEnumerable<UavContact> TargetUavContacts() =>
        _match.Targets.All.Where(d => d.Alive).Select(d => new UavContact(d.Id, d.Spot.Feet.X, d.Spot.Feet.Z, PerkModifiers.None.UavVisibility));

    /// <summary>Snapshot entries for every dummy: standing, or down (the Defeat pose) until it respawns.</summary>
    List<EntityRecord> TargetRecords()
    {
        var list = new List<EntityRecord>(_match.Targets.All.Count);
        foreach (var d in _match.Targets.All)
            list.Add(EntityRecord.Make(d.Id, EntityType.Target, d.Spot.Feet, d.Spot.Yaw, 0, MathF.Max(0, MathF.Ceiling(d.Health)),
                d.Alive ? EntityState.Alive : EntityState.Dead, 0, d.Spot.Pose));
        return list;
    }

    /// <summary>The names of the dummies, sent once on join (their ids and spots never change for the server's life).</summary>
    void SendTargetRoster(Client c)
    {
        if (_match.Targets.All.Count == 0) return;
        var roster = _match.Targets.All.Select(d => (d.Id, d.Name)).ToList();
        Send(c, new EventMsg((byte)EventKind.TargetRoster, TargetCodec.EncodeRoster(roster)));
    }

    /// <summary>
    /// A dummy died: the shooter's streak counter and XP (when <c>targets_award_xp</c>) move as for a player kill. No stats
    /// or progression rows are written, and the kill feed names the dummy. The score (+1) was already credited by the match.
    /// </summary>
    void OnTargetKilled(TargetKilled tk)
    {
        var killed = new PlayerKilled(tk.Killer, tk.Target.Id, tk.Weapon, tk.Cause);
        var fromStreak = StreakKill(killed);
        if (MatchConfig.TargetsAwardXp && Scores(tk.Killer)) AwardXp(_xp.OnKill(new KillFacts(tk.Killer, tk.Target.Id, tk.Cause, fromStreak), _elapsed));
        BroadcastEvent(EventKind.Kill, MatchCodec.Encode(new KillMsg(tk.Killer, tk.Target.Id, tk.Weapon, tk.Cause)));
        GD.Print($"server: #{tk.Killer} killed {tk.Target.Name} (weapon {tk.Weapon}, {tk.Cause})");
    }

    /// <summary>Roadkill: a car drives over dummies like players (the same footprint test, the same damage).</summary>
    void RunOverTargets(ServerCar car, Client driver, float dmg)
    {
        foreach (var t in _match.Targets.All)
        {
            if (!t.Alive) continue;
            if (!CarHits(car, new Vector3(t.Spot.Feet.X, t.Spot.Feet.Y, t.Spot.Feet.Z))) continue;
            if (car.HitCooldown.TryGetValue(t.Id, out var until) && _elapsed < until) continue;
            car.HitCooldown[t.Id] = _elapsed + RunOverCooldown;
            Deal(driver.Id, SpecialWeapons.Vehicle, t.Id, dmg, false, false, KillCause.Roadkill);
        }
    }
}
