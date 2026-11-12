using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The gunship FX on the client (ticket #159): one server tick's tracers, impacts and blasts, coalesced by the server (kinds 121-123).
/// Every coalesced tracer, impact and blast is drawn, through the same code as the single events: a tracer is <see cref="OnShot"/>,
/// an impact is <see cref="OnImpactFx"/> (a pooled emitter, round-robin), a blast is the explosion look at its radius with the shake.
/// The owner's hit marker comes with its blasts message.
/// </summary>
public partial class NetClient
{
    /// <summary>A tracer, from a shot event or a gunship tick: the muzzle, the tracer, the hole and the sound (prediction echo skipped).</summary>
    /// <param name="exactMuzzle">The origin is the real muzzle (a flak barrel): the tracer starts there, not at the shooter's hands, and the shooter's own flash and hole are skipped.</param>
    void OnShot(ShotMsg sh, float? speed = null, float? length = null, bool impact = true, bool exactMuzzle = false, float width = 1f, bool? holes = null)
    {
        if (KeepEventLogs) _shotLog.Add((Now, sh));
        if (_remotes.TryGetValue(sh.Shooter, out var shooter)) shooter.FireUntil = Now + 0.45;
        if (sh.Shooter == _myId && Weapons != null && sh.Weapon < Weapons.Weapons.Count) _spread.Shot(Weapons.Weapons[sh.Weapon], Now);
        var melee = Weapons != null && sh.Weapon < Weapons.Weapons.Count && Weapons.Weapons[sh.Weapon].Kind == WeaponKind.Melee;
        if (EchoOfPredicted(sh.Shooter)) return; // already played and drawn by prediction
        var endV = new Vector3(sh.End.X, sh.End.Y, sh.End.Z);
        SfxShot(sh, melee);
        if (!melee && !exactMuzzle) { if (sh.Shooter == _myId) _viewmodel?.Flash(0.4f); else if (_remotes.TryGetValue(sh.Shooter, out var flasher)) flasher.View?.Flash(); }
        var originV = new Vector3(sh.Origin.X, sh.Origin.Y, sh.Origin.Z);
        // a gunship's impact is its own message (OnGunshipImpacts), so its tracer does not draw one too: no 300 m cut-off for a long round
        ShotFx.Spawn(Owned, exactMuzzle ? originV : MuzzleOf(sh.Shooter, originV, endV), endV, melee, impact: impact, speed: speed, length: length, width: width);
        var marks = !melee && (holes ?? !exactMuzzle);
        if (marks) ShotHole(originV, endV);
        if (marks) BulletSurfaceHit(originV, endV);
    }

    /// <summary>One gunner's tracers this tick: each one drawn as its own shot.</summary>
    void OnGunshipShots(GunshipShotsMsg m)
    {
        var flak = FlakCarOf(m.Owner); // a flak gunner's shells leave the barrels: the tracer starts at the muzzle, and the barrel kicks
        foreach (var t in m.Tracers)
        {
            OnShot(new ShotMsg(m.Owner, t.Weapon, t.Origin, t.End), t.SpeedMps > 0 ? t.SpeedMps : null, t.LengthM > 0 ? t.LengthM : null, impact: false, exactMuzzle: true, width: flak != null ? FlakTracerWidth : 1f, holes: flak == null);
            flak?.Flak?.Kick();
        }
    }

    /// <summary>The tick's gunship impacts (kills included): each one drawn as an ImpactFx.</summary>
    void OnGunshipImpacts(IReadOnlyList<GunshipImpactFx> impacts)
    {
        foreach (var i in impacts) OnImpactFx(i.Impact);
    }

    /// <summary>
    /// One gunner's blasts this tick: each one the explosion look at its radius and the shake, the sound once for the batch. The owner's
    /// hit marker follows the flags.
    /// </summary>
    void OnGunshipBlasts(GunshipBlastsMsg m)
    {
        for (var i = 0; i < m.Blasts.Count; i++)
        {
            var b = m.Blasts[i];
            var at = new Vector3(b.At.X, b.At.Y, b.At.Z);
            if (i == 0 && Sfx.Enabled) Sfx.Play3D(SoundRules.Event("explosion", "bazooka"), at);
            if (Mathf.Abs(b.Radius - FlakPuffRadius) < 0.05f) FlakFx.Burst(Owned, at, b.Radius); // an airburst: black flak smoke, not a fireball
            else RocketFx.Explode(Owned, at, b.Radius);
            ShakeFor(at, b.Radius);
        }
        if (m.Owner == _myId && m.Hits != GunshipHitFlags.None) OnGunnerHits(m.Hits);
    }

    /// <summary>The owner's hit marker from a gunship tick: the turret overlay while at a gun, the HUD's marker, and the hit sound once.</summary>
    void OnGunnerHits(GunshipHitFlags flags)
    {
        var head = flags.HasFlag(GunshipHitFlags.Headshot);
        var kill = flags.HasFlag(GunshipHitFlags.Kill);
        if (_gunship != null) _gunOverlay.Hit(head, kill);
        _hud?.Hit(head, kill);
        if (Sfx.Enabled) Sfx.Play2D(kill ? "kill.confirm" : "hit.tick");
    }
}
