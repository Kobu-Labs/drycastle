using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The MOAB on the client (ticket #129): the countdown banner from the armed event, and at detonation the shockwave
/// (<see cref="RocketFx.Explode"/>, the same blast as a rocket, larger), the vehicle blast sound and the flash and
/// shake (both dropped under reduce motion). Its kills and vehicle deaths arrive as the normal kill and death events.
/// </summary>
public partial class NetClient
{
    /// <summary>Shockwave radius in metres: a rocket's splash is a few metres, the MOAB is a city block.</summary>
    const float MoabBlastRadius = 40f;

    MoabBanner? _moabBanner;

    void OnMoab(MoabInfo m)
    {
        if (_moabBanner == null)
        {
            _moabBanner = new MoabBanner();
            AddChild(_moabBanner);
        }
        var owner = Match.NameOf(m.Owner);
        var at = new Vector3(m.At.X, m.At.Y, m.At.Z);
        switch (m.Phase)
        {
            case MoabPhase.Armed:
                _moabBanner.Armed(m.Seconds);
                GD.Print($"client {PlayerName}: MOAB armed by {owner}, {m.Seconds:F0} s");
                break;
            case MoabPhase.Detonated:
                RocketFx.Explode(Owned, at, MoabBlastRadius);
                if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at);
                if (!GameSettings.Current.ReduceMotion) ShakeFor(at, MoabBlastRadius);
                _moabBanner.Blast(owner);
                GD.Print($"client {PlayerName}: MOAB detonated (seed {m.Seed})");
                break;
            case MoabPhase.Cancelled:
                _moabBanner.Cancelled();
                break;
        }
    }
}
