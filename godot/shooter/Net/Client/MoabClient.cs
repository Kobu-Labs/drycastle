using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The MOAB on the client (ticket #129): the countdown banner from the armed event, and at detonation the shockwave
/// (<see cref="RocketFx.Explode"/>, the same blast as a rocket, larger), the vehicle blast sound and the flash and
/// shake (both dropped under reduce motion). Its kills and vehicle deaths arrive as the normal kill and death events.
/// </summary>
public sealed class MoabClient
{
    /// <summary>Shockwave radius in metres: a rocket's splash is a few metres, the MOAB is a city block.</summary>
    const float BlastRadius = 40f;

    readonly ClientContext _ctx;
    MoabBanner? _banner;

    public MoabClient(ClientContext ctx) { _ctx = ctx; }

    public void OnMoab(MoabInfo m)
    {
        if (_banner == null)
        {
            _banner = new MoabBanner();
            _ctx.Root.AddChild(_banner);
        }
        var owner = _ctx.Match.NameOf(m.Owner);
        var at = new Vector3(m.At.X, m.At.Y, m.At.Z);
        switch (m.Phase)
        {
            case MoabPhase.Armed:
                _banner.Armed(m.Seconds);
                GD.Print($"client {_ctx.PlayerName}: MOAB armed by {owner}, {m.Seconds:F0} s");
                break;
            case MoabPhase.Detonated:
                RocketFx.Explode(_ctx.Owned, at, BlastRadius);
                if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at);
                _ctx.Shake.At(at, BlastRadius); // dropped under reduce motion by the shake itself
                _banner.Blast(owner);
                GD.Print($"client {_ctx.PlayerName}: MOAB detonated (seed {m.Seed})");
                break;
            case MoabPhase.Cancelled:
                _banner.Cancelled();
                break;
        }
    }
}
