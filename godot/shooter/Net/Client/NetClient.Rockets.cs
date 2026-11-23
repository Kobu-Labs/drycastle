using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Rockets and thrown grenades: drawn from the launch event and the snapshots, dead-reckoned between them, retired by the explosion event.</summary>
public partial class NetClient
{
    readonly HashSet<ushort> _deadRockets = new();

    /// <summary>Rockets are drawn at the newest snapshot position and dead-reckoned along their direction (a 100 ms interpolation delay would make them vanish 4 m short of the blast).</summary>
    void OnRocketState(ushort id, EntityRecord rec)
    {
        if (_deadRockets.Contains(id)) return;
        var dir = HitMath.Forward(rec.YawRad, rec.PitchRad);
        // the speed is the launch's (set there); a snapshot before the launch falls back to the weapon's, and the launch corrects it
        if (!Ctx.Rockets.TryGetValue(id, out var r))
        {
            var speed = Weapons != null && rec.Weapon < Weapons.Weapons.Count ? Weapons.Weapons[rec.Weapon].ProjectileSpeed : 40f;
            r = new RocketView { Node = RocketFx.Make(Ctx.Owned, Ctx.Sound.WeaponId(rec.Weapon)), Born = Ctx.Now, Speed = speed }; Ctx.Rockets[id] = r;
            r.Gravity = GrenadeGravity(rec.Weapon); r.Grenade = r.Gravity > 0;
        }
        if (r.Grenade) r.Speed = rec.Health / NetServer.GrenadeSpeedScale; // a grenade's speed rides the health byte
        r.Pos = rec.Position; r.Dir = dir; r.At = Ctx.Now;
    }

    /// <summary>The fall of a thrown weapon (weapons.json grenade.gravity, m/s²); 0 for a rocket.</summary>
    float GrenadeGravity(int weapon) => Weapons != null && weapon >= 0 && weapon < Weapons.Weapons.Count ? Weapons.Weapons[weapon].Grenade?.Gravity ?? 0f : 0f;

    void RenderRockets()
    {
        var camAt = GetViewport()?.GetCamera3D()?.GlobalPosition;
        foreach (var r in Ctx.Rockets.Values)
        {
            if (r.Node == null) continue;
            if (r.Homing) StepHoming(r);
            var dtp = (float)Math.Min(0.1, Ctx.Now - r.At);
            var falls = r.Speed > GrenadeFlight.RestSpeed; // a grenade lying still does not keep falling between snapshots (it sank up to 10 cm into the road, #421)
            var p = r.Homing ? r.Pos : r.Pos + r.Dir * (r.Speed * dtp) - NVec3.UnitY * (falls ? 0.5f * r.Gravity * dtp * dtp : 0f); // a grenade falls between snapshots
            // visual only: the server rocket starts at the eye; draw it leaving the muzzle (0.8 m ahead, right and down) and blend onto the true path
            var k = Mathf.Max(0f, 1f - (float)(Ctx.Now - r.Born) / 0.35f);
            var d = new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z);
            var side = d.Cross(Vector3.Up).Normalized();
            r.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z) + (d * 0.8f + side * 0.25f + Vector3.Down * 0.2f) * k;
            if (r.Grenade)
            {
                // it tumbles while it moves, and lies on its side when it stops (upright, its lower half would sink into the road ribbons floating 2-8 cm over the ground)
                var spin = (float)GetProcessDeltaTime();
                r.Tumble = r.Speed > GrenadeFlight.RestSpeed ? r.Tumble + 9f * spin : Mathf.MoveToward(r.Tumble, MathF.Round((r.Tumble - Mathf.Pi / 2) / Mathf.Pi) * Mathf.Pi + Mathf.Pi / 2, 8f * spin);
                r.Node.Rotation = new Vector3(r.Tumble, 0, 0);
                continue; // no trail, no flight loop
            }
            r.PlayFlightLoop(r.Node.GlobalPosition);
            if (camAt is { } eye) RocketFx.GateTrail(r.Node, eye);
            if (Mathf.Abs(r.Dir.Y) < 0.99f) r.Node.LookAt(r.Node.GlobalPosition + new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z));
        }
    }

    /// <summary>A rocket or a thrown grenade left its owner: drawn at once from the event (snapshots take over), the sound, the flash and the backblast.</summary>
    void OnLaunch(LaunchMsg l)
    {
        Ctx.Logs?.Launches.Add((Ctx.Now, l));
        if (Ctx.Remotes.TryGetValue(l.Owner, out var launcher)) launcher.FireUntil = Ctx.Now + 0.45;
        // the launch's velocity is the flight's speed and direction (a gunship's rocket is faster than the rpg's)
        if (Ctx.Rockets.TryGetValue(l.Id, out var known)) { known.Speed = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z).Length(); known.Gravity = GrenadeGravity(l.Weapon); known.Grenade = known.Gravity > 0; }
        else if (!_deadRockets.Contains(l.Id) && ShotFx.Enabled && Weapons != null)
        {
            // show the rocket at once from the launch event; snapshots take over
            var v = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z);
            Ctx.Rockets[l.Id] = new RocketView { Node = RocketFx.Make(Ctx.Owned, Ctx.Sound.WeaponId(l.Weapon)), Born = Ctx.Now, Pos = l.Origin, Dir = NVec3.Normalize(v), At = Ctx.Now, Speed = v.Length(), Gravity = GrenadeGravity(l.Weapon), Grenade = GrenadeGravity(l.Weapon) > 0 };
        }
        if (!EchoOfPredicted(l.Owner))
        {
            Ctx.Sound.Launch(l);
            if (GrenadeGravity(l.Weapon) > 0) { if (Ctx.Remotes.TryGetValue(l.Owner, out var thrower)) thrower.FireUntil = Ctx.Now + 0.45; return; } // a throw: no flash, no backblast
            if (l.Owner == Ctx.MyId) _viewmodel.Flash(0.5f); else if (Ctx.Remotes.TryGetValue(l.Owner, out var lf)) lf.View?.Flash(0.6f);
            // a held launcher's backblast, behind the shooter's shoulder (a gunship's rocket has no owner on foot: its owner is not a remote here)
            if (Ctx.Remotes.ContainsKey(l.Owner) && l.Velocity.LengthSquared() > 1e-4f) { var ld = G(NVec3.Normalize(l.Velocity)); RocketFx.Backblast(Ctx.Owned, G(l.Origin) - ld * WeaponFx.LauncherRear + Vector3.Down * 0.15f, ld); }
        }
    }

    /// <summary>A rocket blew up: its model retires, the blast is drawn and felt.</summary>
    void OnExplosion(ExplosionMsg ex)
    {
        Ctx.Logs?.Explosions.Add((Ctx.Now, ex));
        if (Ctx.Rockets.Remove(ex.Id, out var rk)) { rk.Snd?.Release(); RocketFx.Retire(Ctx.Owned, rk.Node); if (rk.Homing) NoteHomingBlast(new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z), ex.Radius); }
        Ctx.Snapshots.Forget(ex.Id);
        _deadRockets.Add(ex.Id);
        if (_deadRockets.Count > 256) _deadRockets.Clear();
        var exR = ex.Radius > 0 ? ex.Radius : 6f; // the blast the server used (a gunship rocket's is 8 m)
        var exAt = new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z);
        RocketFx.Explode(Ctx.Owned, exAt, exR, ex.WaveSpeed);
        _blastDust.BlastFelt(exAt, exR, SoundRules.Event("explosion", Ctx.Sound.WeaponId(ex.Weapon)), ex.WaveSpeed);
    }
}
