using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Killstreaks.Uav;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Progression;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

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
        var centre = new NVec3((float)Spawns.Average(s => s.X), 0, (float)Spawns.Average(s => s.Z));
        var top = Spawns.Max(s => s.Y) + 60f;
        var bottom = Spawns.Min(s => s.Y) - 60f;

        // The first surface from above: the ground, or a roof where the ray meets one first. Steep faces are rejected.
        float? GroundY(float x, float z)
        {
            if (Query.Ground(MoverKind.Walker, new Vector3(x, top, z), 0f, top - bottom) is not { } hit) return null;
            return hit.Normal.Y >= 0.7f ? hit.Position.Y : null;
        }

        var spots = TargetPlacement.Plan(centre, n, GroundY);
        _match.SetTargets(spots);
        GD.Print($"server: planted {spots.Count}/{n} target dummies (rings {string.Join("/", TargetPlacement.RingsMetres)} m; cover and wall spots not generated)");
    }

    void AddDummyTargets(List<HitTarget> list)
    {
        var dummies = _match.Targets.All; // living target dummies: an index loop, no enumerator object
        for (var i = 0; i < dummies.Count; i++)
        {
            var d = dummies[i];
            if (d.Alive) list.Add(new HitTarget(d.Id, d.Spot.Feet, d.Spot.Pose, d.Spot.Yaw));
        }
    }

    void AddAnimalTargets(List<HitTarget> list)
    {
        var partT = Prof.Begin(Prof.S.TargetsAnimals);
        _pets.AddTargets(list);
        _wildlife.AddTargets(list);
        Prof.End(Prof.S.TargetsAnimals, partT);
    }

    /// <summary>Snapshot entries for every dummy: standing, or down (the Defeat pose) until it respawns.</summary>
    List<EntityRecord> TargetRecords()
    {
        var list = new List<EntityRecord>(_match.Targets.All.Count);
        foreach (var d in _match.Targets.All)
            list.Add(EntityRecord.Make(d.Id, EntityType.Target, d.Body.Position, d.Spot.Yaw, 0, MathF.Max(0, MathF.Ceiling(d.Health)),
                d.Alive ? EntityState.Alive : EntityState.Dead, 0, d.Spot.Pose));
        return list;
    }

    /// <summary>The names of the dummies, sent once on join (their ids and spots never change for the server's life).</summary>
    void SendTargetRoster(ServerClient c)
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
        if (_deathPush.Take(tk.Target.Id, out var push)) tk.Target.Body.Launch(push); // thrown by the blast or the car (the ragdoll)
        var killed = new PlayerKilled(tk.Killer, tk.Target.Id, tk.Weapon, tk.Cause);
        var fromStreak = _streaks.Kill(killed, _progress.WeaponKey(killed.Weapon));
        if (MatchConfig.TargetsAwardXp && _progress.Scores(tk.Killer)) _progress.Kill(new KillFacts(tk.Killer, tk.Target.Id, tk.Cause, fromStreak));
        BroadcastEvent(EventKind.Kill, MatchCodec.Encode(new KillMsg(tk.Killer, tk.Target.Id, tk.Weapon, tk.Cause)));
        GD.Print($"server: #{tk.Killer} killed {tk.Target.Name} (weapon {tk.Weapon}, {tk.Cause})");
    }
}
