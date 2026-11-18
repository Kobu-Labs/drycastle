using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Server side of the shootable pole lamps (ADR 0038). The server owns every lamp's state (<see cref="BreakableLamps"/>): a bullet, pellet or gun round that
/// passed a lamp's head on its way breaks it, a lamp comes back on its own (flickering first) and a new round lights all of them. The lamp list is the placed
/// objects' luminaires in id order (MapLoader.ObjectLights()), the one the clients draw; the headless server builds no lamp visuals but has the same list.
/// </summary>
public partial class NetServer
{
    /// <summary>environment.json "lamp"; the defaults when the host sets none.</summary>
    public LampLook LampData = new();

    BreakableLamps? _lamps;

    /// <summary>Made on the first shot or join, so a server that nobody shoots at never lists the map's lamps.</summary>
    BreakableLamps Lamps => _lamps ??= new BreakableLamps(Map?.ObjectLights().Select(l => N(l.Position)).ToList() ?? new List<NVec3>(), LampData);

    /// <summary>A round that flew <paramref name="length"/> m from <paramref name="from"/> along <paramref name="dir"/> (to where it stopped) breaks the lamps it passed.</summary>
    void ShotLamps(NVec3 from, NVec3 dir, float length)
    {
        if (Map == null || !(length > 0)) return;
        Lamps.ShootAlong(from, dir, length);
    }

    /// <summary>Steps the clocks of the broken lamps and sends the changes of the tick, one reliable event each (once per tick).</summary>
    void FlushLamps(float dt)
    {
        if (_lamps == null) return;
        _lamps.Tick(dt);
        foreach (var m in _lamps.TakeChanges()) BroadcastEvent(EventKind.LampState, LampCodec.EncodeState(m));
    }

    /// <summary>The joiner's catch-up: every lamp that is not lit.</summary>
    void SendLampsOnJoin(Client c)
    {
        if (_lamps?.Unlit() is { Count: > 0 } unlit) Send(c, new EventMsg((byte)EventKind.LampSync, LampCodec.EncodeSync(unlit)));
    }

    /// <summary>A new round: every lamp is lit again.</summary>
    void RestoreLampsForRound()
    {
        if (_lamps?.RestoreAll() == true) BroadcastEvent(EventKind.LampSync, LampCodec.EncodeSync(Array.Empty<LampStateMsg>()));
    }
}
