using Godot;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// Stationary target dummies (#148) on the server: planning (ground-checked against the map colliders), their hit targets, their snapshot
/// entries, the roster sent on join and the kill credit. Damage itself runs through <see cref="MatchSim.ApplyDamage"/>, which routes dummy ids
/// to <see cref="TargetDummies"/>, so every weapon path that asks the <see cref="TargetBook"/> reaches them without a second code path.
/// </summary>
internal sealed class DummyServer
{
    readonly ServerContext _ctx;
    readonly ShotWorld _shot;
    readonly DeathPush _deathPush;
    readonly Func<StreakServer> _streaks;
    readonly ProgressionServer _progress;
    readonly List<NVec3> _spawns;
    readonly MatchConfig _config;

    public DummyServer(ServerContext ctx, ShotWorld shot, DeathPush deathPush, Func<StreakServer> streaks, ProgressionServer progress, List<NVec3> spawns, MatchConfig config)
    {
        _ctx = ctx; _shot = shot; _deathPush = deathPush; _streaks = streaks; _progress = progress; _spawns = spawns; _config = config;
    }

    /// <summary>Planned once after the spawn list is validated (--targets N; 0 plants none).</summary>
    public void Plan()
    {
        var n = _config.Targets;
        if (n <= 0 || _spawns.Count == 0)
        {
            if (n > 0) GD.PrintErr("server: --targets needs spawn points; no target dummies planted");
            return;
        }
        var centre = new NVec3((float)_spawns.Average(s => s.X), 0, (float)_spawns.Average(s => s.Z));
        var top = _spawns.Max(s => s.Y) + 60f;
        var bottom = _spawns.Min(s => s.Y) - 60f;

        // The first surface from above: the ground, or a roof where the ray meets one first. Steep faces are rejected.
        float? GroundY(float x, float z)
        {
            if (_shot.Query.Ground(MoverKind.Walker, new Vector3(x, top, z), 0f, top - bottom) is not { } hit) return null;
            return hit.Normal.Y >= 0.7f ? hit.Position.Y : null;
        }

        var spots = TargetPlacement.Plan(centre, n, GroundY);
        _ctx.Match.SetTargets(spots);
        GD.Print($"server: planted {spots.Count}/{n} target dummies (rings {string.Join("/", TargetPlacement.RingsMetres)} m; cover and wall spots not generated)");
    }

    /// <summary>The living dummies, as hit targets.</summary>
    public void AddTargets(List<HitTarget> list)
    {
        var dummies = _ctx.Match.Targets.All; // living target dummies: an index loop, no enumerator object
        for (var i = 0; i < dummies.Count; i++)
        {
            var d = dummies[i];
            if (d.Alive) list.Add(new HitTarget(d.Id, d.Spot.Feet, d.Spot.Pose, d.Spot.Yaw));
        }
    }

    /// <summary>Snapshot entries for every dummy: standing, or down (the Defeat pose) until it respawns.</summary>
    public List<EntityRecord> Records()
    {
        var list = new List<EntityRecord>(_ctx.Match.Targets.All.Count);
        foreach (var d in _ctx.Match.Targets.All)
            list.Add(EntityRecord.Make(d.Id, EntityType.Target, d.Body.Position, d.Spot.Yaw, 0, MathF.Max(0, MathF.Ceiling(d.Health)),
                d.Alive ? EntityState.Alive : EntityState.Dead, 0, d.Spot.Pose));
        return list;
    }

    /// <summary>The names of the dummies, sent once on join (their ids and spots never change for the server's life).</summary>
    public void SendRoster(ServerClient c)
    {
        if (_ctx.Match.Targets.All.Count == 0) return;
        var roster = _ctx.Match.Targets.All.Select(d => (d.Id, d.Name)).ToList();
        _ctx.SendEvent(c, EventKind.TargetRoster, TargetCodec.EncodeRoster(roster));
    }

    /// <summary>
    /// A dummy died: the shooter's streak counter and XP (when <c>targets_award_xp</c>) move as for a player kill. No stats
    /// or progression rows are written, and the kill feed names the dummy. The score (+1) was already credited by the match.
    /// </summary>
    public void OnKilled(TargetKilled tk)
    {
        if (_deathPush.Take(tk.Target.Id, out var push)) tk.Target.Body.Launch(push); // thrown by the blast or the car (the ragdoll)
        var killed = new PlayerKilled(tk.Killer, tk.Target.Id, tk.Weapon, tk.Cause);
        var fromStreak = _streaks().Kill(killed, _progress.WeaponKey(killed.Weapon));
        if (_config.TargetsAwardXp && _progress.Scores(tk.Killer)) _progress.Kill(new KillFacts(tk.Killer, tk.Target.Id, tk.Cause, fromStreak));
        _ctx.Broadcast(EventKind.Kill, MatchCodec.Encode(new KillMsg(tk.Killer, tk.Target.Id, tk.Weapon, tk.Cause)));
        GD.Print($"server: #{tk.Killer} killed {tk.Target.Name} (weapon {tk.Weapon}, {tk.Cause})");
    }
}
