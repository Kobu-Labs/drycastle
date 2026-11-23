using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Server side of the shootable road sign plates (ADR 0039), the sibling of the pole lamps (NetServer.Lamps.cs). The server owns every plate's state
/// (<see cref="BreakableSigns"/>): a bullet, pellet or gun round that passed a plate's centre on its way bends it, and it is whole again by itself; a new
/// round straightens all of them. The plate list is the placed objects' plates in id order (MapLoader.ObjectSignPlates()), the one the clients draw; the
/// headless server builds no sign visuals but has the same list. Visual only: a plate stops no round, and the pole is solid by its own collider.
/// </summary>
public partial class NetServer
{
    /// <summary>environment.json "sign_plate"; the defaults when the host sets none.</summary>
    public SignPlateLook SignPlateData = new();

    BreakableSigns? _signs;

    /// <summary>Made on the first shot or join, so a server that nobody shoots at never lists the map's signs.</summary>
    BreakableSigns Signs => _signs ??= new BreakableSigns(Map?.ObjectSignPlates().Select(p => N(p.Position)).ToList() ?? new List<NVec3>(), SignPlateData);

    /// <summary>A round that flew <paramref name="length"/> m from <paramref name="from"/> along <paramref name="dir"/> (to where it stopped) passed the lamps and sign plates on its way.</summary>
    void ShotScenery(NVec3 from, NVec3 dir, float length)
    {
        ShotLamps(from, dir, length);
        if (Map == null || !(length > 0)) return;
        Signs.ShootAlong(from, dir, length);
    }

    /// <summary>Steps the clocks of the bent plates and sends the changes of the tick, one reliable event each (once per tick).</summary>
    void FlushSigns(float dt)
    {
        if (_signs == null) return;
        _signs.Tick(dt);
        foreach (var m in _signs.TakeChanges()) BroadcastEvent(EventKind.SignPlateState, SignPlateCodec.EncodeState(m));
    }

    /// <summary>The joiner's catch-up: every plate that is not whole.</summary>
    void SendSignsOnJoin(Client c)
    {
        if (_signs?.Bent() is { Count: > 0 } bent) Send(c, new EventMsg((byte)EventKind.SignPlateSync, SignPlateCodec.EncodeSync(bent)));
    }

    /// <summary>A new round: every plate is whole again.</summary>
    void RestoreSignsForRound()
    {
        if (_signs?.RestoreAll() == true) BroadcastEvent(EventKind.SignPlateSync, SignPlateCodec.EncodeSync(Array.Empty<SignPlateMsg>()));
    }
}
