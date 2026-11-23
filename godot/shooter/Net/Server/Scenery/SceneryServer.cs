using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Server side of the shootable street furniture: pole lamps (ADR 0037) and road sign plates (ADR 0039). The server owns the state of each (<see cref="BreakableLamps"/>,
/// <see cref="BreakableSigns"/>): a bullet, pellet or gun round that passed a lamp's head or a plate's centre on its way breaks or bends it, it comes back by itself
/// (a lamp flickering first) and a new round restores all of them. The lists are the placed objects' luminaires and plates in id order
/// (<c>MapLoader.ObjectLights()</c>, <c>ObjectSignPlates()</c>), the ones the clients draw; the headless server builds no visuals but has the same lists. Visual only:
/// a plate stops no round, and the pole is solid by its own collider.
/// </summary>
internal sealed class SceneryServer
{
    readonly ServerContext _ctx;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly LampLook _lampLook;
    readonly SignPlateLook _signLook;

    // Made on the first shot or join, so a server that nobody shoots at never lists the map's lamps and signs.
    BreakableLamps? _lamps;
    BreakableSigns? _signs;

    public SceneryServer(ServerContext ctx, Drycastle.MapLoader.MapLoader? map, LampLook lampLook, SignPlateLook signLook)
    {
        _ctx = ctx; _map = map; _lampLook = lampLook; _signLook = signLook;
    }

    BreakableLamps Lamps => _lamps ??= new BreakableLamps(_map?.ObjectLights().Select(l => l.Position.ToNumerics()).ToList() ?? new List<NVec3>(), _lampLook);

    BreakableSigns Signs => _signs ??= new BreakableSigns(_map?.ObjectSignPlates().Select(p => p.Position.ToNumerics()).ToList() ?? new List<NVec3>(), _signLook);

    /// <summary>A round that flew <paramref name="length"/> m from <paramref name="from"/> along <paramref name="dir"/> (to where it stopped) passed the lamps and sign plates on its way.</summary>
    public void Shot(NVec3 from, NVec3 dir, float length)
    {
        if (_map == null || !(length > 0)) return;
        Lamps.ShootAlong(from, dir, length);
        Signs.ShootAlong(from, dir, length);
    }

    /// <summary>Steps the clocks of the broken lamps and bent plates and sends the changes of the tick, one reliable event each (once per tick).</summary>
    public void Tick(float dt)
    {
        if (_lamps != null)
        {
            _lamps.Tick(dt);
            foreach (var m in _lamps.TakeChanges()) _ctx.Broadcast(EventKind.LampState, LampCodec.EncodeState(m));
        }
        if (_signs != null)
        {
            _signs.Tick(dt);
            foreach (var m in _signs.TakeChanges()) _ctx.Broadcast(EventKind.SignPlateState, SignPlateCodec.EncodeState(m));
        }
    }

    /// <summary>The joiner's catch-up: every lamp that is not lit and every plate that is not whole.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_lamps?.Unlit() is { Count: > 0 } unlit) _ctx.SendEvent(c, EventKind.LampSync, LampCodec.EncodeSync(unlit));
        if (_signs?.Bent() is { Count: > 0 } bent) _ctx.SendEvent(c, EventKind.SignPlateSync, SignPlateCodec.EncodeSync(bent));
    }

    /// <summary>A new round: every lamp is lit and every plate whole again.</summary>
    public void RoundStart()
    {
        if (_lamps?.RestoreAll() == true) _ctx.Broadcast(EventKind.LampSync, LampCodec.EncodeSync(Array.Empty<LampStateMsg>()));
        if (_signs?.RestoreAll() == true) _ctx.Broadcast(EventKind.SignPlateSync, SignPlateCodec.EncodeSync(Array.Empty<SignPlateMsg>()));
    }
}
