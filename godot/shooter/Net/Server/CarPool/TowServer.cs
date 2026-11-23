using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// Towing on the server (docs/shooter-v2.md "Towing and the rocket cart"; the model is ShooterCore's <see cref="TowLink"/>, <see cref="TowModel"/>, <see cref="TowRules"/>).
/// A trailer is a <see cref="ServerCar"/> with no seats; this is what the vehicle step does about it: the auto-latch (no key: a tractor that reverses its hitch onto a loose trailer's eye
/// couples), the trailer's own tick (it rolls free, or follows its tractor, whose step moved it), the run-over of its box, and the unhitch key.
/// </summary>
internal sealed class TowServer
{
    /// <summary>Only a tractor within this of a loose trailer is asked whether it latches it (metres, between origins: the hitch is a few metres behind the tractor's, the eye ahead of the trailer's).</summary>
    const float LatchBroadRange = 14f;

    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly CarBlast _blast;
    readonly RunOverServer _runOver;
    readonly Action<ServerCar, float> _stepThrown;
    readonly float _killY;

    /// <param name="stepThrown">A trailer the Bigfoot holds is let go of its tractor and carried where it is put (the same step as any thrown vehicle).</param>
    public TowServer(ServerContext ctx, Fleet fleet, CarBlast blast, RunOverServer runOver, Action<ServerCar, float> stepThrown, float killY)
    {
        _ctx = ctx; _fleet = fleet; _blast = blast; _runOver = runOver; _stepThrown = stepThrown; _killY = killY;
    }

    /// <summary>
    /// The auto-latch (GTA style: it couples by itself): every loose trailer against every tractor of a type that can pull it that has no trailer yet and is not thrown, on land, with a driver or not. The rule
    /// is <see cref="TowRules.CheckLatch"/> (reversing, the hitch onto the eye, lined up); a trailer just released is locked out for a while. A coupled pair is the tractor's from this tick on.
    /// </summary>
    public void LatchTick()
    {
        foreach (var trailer in _fleet.Cars)
        {
            if (!trailer.IsTrailer || trailer.Destroyed || trailer.Tractor != null || trailer.Thrown != null || trailer.Tow!.State != TrailerState.Loose) continue;
            var at = trailer.GlobalPosition;
            foreach (var tractor in _fleet.Cars)
            {
                if (tractor.Def.Tow == null || tractor.Destroyed || tractor.Thrown != null || tractor.Towing != null || tractor.Flies || tractor.Afloat) continue;
                if (CarPose.Flat(at.ToNumerics(), tractor.GlobalPosition.ToNumerics()) > LatchBroadRange) continue;
                if (!trailer.TryLatchTo(tractor)) continue;
                var driver = _fleet.OccupantAt(tractor, 0);
                tractor.UnhitchKey.Arm(driver?.UseKey.Held ?? false); // a key held as it latched must be let go before it counts
                GD.Print($"server: {tractor.Def.Id} {tractor.Id} hitched {trailer.Def.Id} {trailer.Id}");
                break;
            }
        }
    }

    /// <summary>
    /// One tick of a trailer in the vehicle step. Hitched, it was already stepped by its tractor's step (and its node placed); loose, it rolls on and stops (<see cref="ServerCar.StepFree"/>). Then the
    /// ordinary life of a vehicle: out of the world, its blast when doomed, and the road kill by its box. A trailer the Bigfoot holds is let go of its tractor and carried where it is put.
    /// </summary>
    public void Step(ServerCar car, float dt)
    {
        if (car.Thrown != null) { car.Unhitch(); _stepThrown(car, dt); car.SyncTrailerFromNode(); return; }
        car.PrevPos = car.TickEndPos;
        if (car.Tractor == null) car.StepFree(dt);
        else car.Wake(); // pulled: never rests (it is not stepped by its own turn, but it is not asleep either)
        var here = car.GlobalPosition;
        car.TickEndPos = here;
        if (here.Y < _killY) { car.Reset(false); return; }
        if (car.Life.DetonationDue(_ctx.Now)) { _blast.Detonate(car); return; }
        var speed = car.Tow!.Body.Speed;
        if (car.Def.RunOver && RunOver.Contacts(speed)) _runOver.Trailer(car, speed);
    }

    /// <summary>
    /// The driver's interact key (the input's <paramref name="useDown"/>, for <paramref name="dt"/>) with a trailer on the hook: a hold unhitches it, a tap leaves the vehicle (on release) — so the same key does
    /// both and neither is lost (<see cref="UnhitchKey"/>). True when the driver leaves now. Only the driver's seat unhitches; any other seat leaves on the press as always.
    /// </summary>
    public static bool UseKey(ServerClient c, ServerCar car, bool useDown, bool useEdge, float dt)
    {
        if (c.CarSeat != 0 || car.Towing == null) return useEdge;
        switch (car.UnhitchKey.Tick(useDown, dt))
        {
            case UnhitchAction.Unhitch:
                if (car.Towing.Unhitch()) GD.Print($"server: {c.Name} #{c.Id} unhitched the trailer of {car.Def.Id} {car.Id}");
                return false;
            case UnhitchAction.Exit: return true;
            default: return false;
        }
    }
}
