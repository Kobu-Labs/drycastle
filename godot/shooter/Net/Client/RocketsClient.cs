using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Rockets, thrown grenades and homing missiles in the air: drawn from the launch event and the snapshots, dead-reckoned between them, retired by the explosion event.
/// A homing missile (<see cref="EventKind.HomingLaunch"/>, #347) is drawn closing on its Aircraft so that it arrives when the server's hit does.
/// </summary>
internal sealed class RocketsClient
{
    readonly ClientContext _ctx;
    readonly OwnWeapon _weapon;
    readonly BlastDustClient _blastDust;
    readonly LockOnClient _lockOn;
    /// <summary>Where and when a homing missile last shot an Aircraft down, so the Aircraft's own crash blast is not drawn on top of the missile's.</summary>
    (double T, Vector3 At) _homingKill = (double.NegativeInfinity, Vector3.Zero);

    public RocketsClient(ClientContext ctx, OwnWeapon weapon, BlastDustClient blastDust, LockOnClient lockOn)
    {
        _ctx = ctx; _weapon = weapon; _blastDust = blastDust; _lockOn = lockOn;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.HomingLaunch && LockCodec.DecodeLaunch(payload) is { } l) OnHomingLaunch(l);
    }

    readonly HashSet<ushort> _deadRockets = new();

    /// <summary>Rockets are drawn at the newest snapshot position and dead-reckoned along their direction (a 100 ms interpolation delay would make them vanish 4 m short of the blast).</summary>
    public void OnState(ushort id, EntityRecord rec)
    {
        if (_deadRockets.Contains(id)) return;
        var dir = HitMath.Forward(rec.YawRad, rec.PitchRad);
        // the speed is the launch's (set there); a snapshot before the launch falls back to the weapon's, and the launch corrects it
        if (!_ctx.Rockets.TryGetValue(id, out var r))
        {
            var speed = _ctx.Weapons != null && rec.Weapon < _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[rec.Weapon].ProjectileSpeed : 40f;
            r = new RocketView { Node = RocketFx.Make(_ctx.Owned, _ctx.Sound.WeaponId(rec.Weapon)), Born = _ctx.Now, Speed = speed }; _ctx.Rockets[id] = r;
            r.Gravity = GrenadeGravity(rec.Weapon); r.Grenade = r.Gravity > 0;
        }
        if (r.Grenade) r.Speed = rec.Health / NetServer.GrenadeSpeedScale; // a grenade's speed rides the health byte
        r.Pos = rec.Position; r.Dir = dir; r.At = _ctx.Now;
    }

    /// <summary>The fall of a thrown weapon (weapons.json grenade.gravity, m/s²); 0 for a rocket.</summary>
    float GrenadeGravity(int weapon) => _ctx.Weapons != null && weapon >= 0 && weapon < _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[weapon].Grenade?.Gravity ?? 0f : 0f;

    public void Render(double delta)
    {
        var camAt = _ctx.Root.GetViewport()?.GetCamera3D()?.GlobalPosition;
        foreach (var r in _ctx.Rockets.Values)
        {
            if (r.Node == null) continue;
            if (r.Homing) StepHoming(r);
            var dtp = (float)Math.Min(0.1, _ctx.Now - r.At);
            var falls = r.Speed > GrenadeFlight.RestSpeed; // a grenade lying still does not keep falling between snapshots (it sank up to 10 cm into the road, #421)
            var p = r.Homing ? r.Pos : r.Pos + r.Dir * (r.Speed * dtp) - NVec3.UnitY * (falls ? 0.5f * r.Gravity * dtp * dtp : 0f); // a grenade falls between snapshots
            // visual only: the server rocket starts at the eye; draw it leaving the muzzle (0.8 m ahead, right and down) and blend onto the true path
            var k = Mathf.Max(0f, 1f - (float)(_ctx.Now - r.Born) / 0.35f);
            var d = new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z);
            var side = d.Cross(Vector3.Up).Normalized();
            r.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z) + (d * 0.8f + side * 0.25f + Vector3.Down * 0.2f) * k;
            if (r.Grenade)
            {
                // it tumbles while it moves, and lies on its side when it stops (upright, its lower half would sink into the road ribbons floating 2-8 cm over the ground)
                var spin = (float)delta;
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
    public void OnLaunch(LaunchMsg l)
    {
        _ctx.Logs?.Launches.Add((_ctx.Now, l));
        if (_ctx.Remotes.TryGetValue(l.Owner, out var launcher)) launcher.FireUntil = _ctx.Now + 0.45;
        // the launch's velocity is the flight's speed and direction (a gunship's rocket is faster than the rpg's)
        if (_ctx.Rockets.TryGetValue(l.Id, out var known)) { known.Speed = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z).Length(); known.Gravity = GrenadeGravity(l.Weapon); known.Grenade = known.Gravity > 0; }
        else if (!_deadRockets.Contains(l.Id) && ShotFx.Enabled && _ctx.Weapons != null)
        {
            // show the rocket at once from the launch event; snapshots take over
            var v = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z);
            _ctx.Rockets[l.Id] = new RocketView { Node = RocketFx.Make(_ctx.Owned, _ctx.Sound.WeaponId(l.Weapon)), Born = _ctx.Now, Pos = l.Origin, Dir = NVec3.Normalize(v), At = _ctx.Now, Speed = v.Length(), Gravity = GrenadeGravity(l.Weapon), Grenade = GrenadeGravity(l.Weapon) > 0 };
        }
        if (!_weapon.EchoOfPredicted(l.Owner))
        {
            _ctx.Sound.Launch(l);
            if (GrenadeGravity(l.Weapon) > 0) { if (_ctx.Remotes.TryGetValue(l.Owner, out var thrower)) thrower.FireUntil = _ctx.Now + 0.45; return; } // a throw: no flash, no backblast
            if (l.Owner == _ctx.MyId) _ctx.Viewmodel.Flash(0.5f); else if (_ctx.Remotes.TryGetValue(l.Owner, out var lf)) lf.View?.Flash(0.6f);
            // a held launcher's backblast, behind the shooter's shoulder (a gunship's rocket has no owner on foot: its owner is not a remote here)
            if (_ctx.Remotes.ContainsKey(l.Owner) && l.Velocity.LengthSquared() > 1e-4f) { var ld = NVec3.Normalize(l.Velocity).ToGodot(); RocketFx.Backblast(_ctx.Owned, l.Origin.ToGodot() - ld * WeaponFx.LauncherRear + Vector3.Down * 0.15f, ld); }
        }
    }

    /// <summary>A rocket blew up: its model retires, the blast is drawn and felt.</summary>
    public void OnExplosion(ExplosionMsg ex)
    {
        _ctx.Logs?.Explosions.Add((_ctx.Now, ex));
        if (_ctx.Rockets.Remove(ex.Id, out var rk)) { rk.Snd?.Release(); RocketFx.Retire(_ctx.Owned, rk.Node); if (rk.Homing) NoteHomingBlast(new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z), ex.Radius); }
        _ctx.Snapshots.Forget(ex.Id);
        _deadRockets.Add(ex.Id);
        if (_deadRockets.Count > 256) _deadRockets.Clear();
        var exR = ex.Radius > 0 ? ex.Radius : 6f; // the blast the server used (a gunship rocket's is 8 m)
        var exAt = new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z);
        RocketFx.Explode(_ctx.Owned, exAt, exR, ex.WaveSpeed);
        _blastDust.BlastFelt(exAt, exR, SoundRules.Event("explosion", _ctx.Sound.WeaponId(ex.Weapon)), ex.WaveSpeed);
    }

    /// <summary>A homing missile leaves a launcher: drawn from the launch, flown by <see cref="Render"/> after its Aircraft, ended by the Explosion with its id.</summary>
    void OnHomingLaunch(HomingLaunchMsg l)
    {
        if (_ctx.Remotes.TryGetValue(l.Owner, out var launcher)) launcher.FireUntil = _ctx.Now + 0.45;
        if (!_weapon.EchoOfPredicted(l.Owner))
        {
            if (Sfx.Enabled) { var snd = SoundRules.Event("launch", _ctx.Sound.WeaponId(l.Weapon)); if (l.Owner == _ctx.MyId) Sfx.Play2D(snd); else Sfx.Play3D(snd, l.Origin.ToGodot()); }
            if (l.Owner == _ctx.MyId) _ctx.Viewmodel.Flash(0.5f); else if (_ctx.Remotes.TryGetValue(l.Owner, out var lf)) lf.View?.Flash(0.6f);
        }
        if (_deadRockets.Contains(l.Id) || !ShotFx.Enabled || _ctx.Weapons == null || _ctx.Rockets.ContainsKey(l.Id)) return;
        var speed = l.Weapon < _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[l.Weapon].ProjectileSpeed : 160f;
        var to = _lockOn.TargetPosition(l.Target) is { } tp ? new NVec3(tp.X, tp.Y, tp.Z) - l.Origin : NVec3.UnitY;
        _ctx.Rockets[l.Id] = new RocketView { Node = RocketFx.Make(_ctx.Owned), Born = _ctx.Now, Pos = l.Origin, Dir = NVec3.Normalize(to), At = _ctx.Now, Speed = speed, Homing = true, Target = l.Target, Arrive = l.Seconds > 0 ? _ctx.Now + l.Seconds : 0 };
    }

    /// <summary>One frame of a homing missile: it closes on where its Aircraft is drawn at the rate that lands it there on time; with the Aircraft gone it flies straight on.</summary>
    void StepHoming(RocketView r)
    {
        var dt = (float)(_ctx.Now - r.At);
        r.At = _ctx.Now;
        if (dt <= 0) return;
        if (_lockOn.TargetPosition(r.Target) is { } p)
        {
            // an Aircraft: arrives when the server's hit does; a helicopter (Arrive 0, Seconds 0): flown as the server does, at its speed, straight at the vehicle
            var next = r.Arrive <= 0 ? LockRules.Pursue(r.Pos, new NVec3(p.X, p.Y, p.Z), r.Speed, 0f, dt).Next : LockRules.Chase(r.Pos, new NVec3(p.X, p.Y, p.Z), (float)(r.Arrive - _ctx.Now) + dt, dt);
            var step = next - r.Pos;
            if (step.LengthSquared() > 1e-8f) r.Dir = NVec3.Normalize(step);
            r.Pos = next;
        }
        else r.Pos += r.Dir * (r.Speed * dt);
    }

    /// <summary>The blast of a homing missile's hit; a big one (the kill's) is remembered so the Aircraft's own crash blast is skipped.</summary>
    void NoteHomingBlast(Vector3 at, float radius)
    {
        if (radius >= DustRules.FrontMinRadius) _homingKill = (_ctx.Now, at);
    }

    /// <summary>Whether a shot-down Aircraft at <paramref name="at"/> has just had a homing missile's kill blast drawn where it flew.</summary>
    public bool HomingKillShown(Vector3 at) => _ctx.Now - _homingKill.T < 0.75 && _homingKill.At.DistanceTo(at) < 60f;

}
