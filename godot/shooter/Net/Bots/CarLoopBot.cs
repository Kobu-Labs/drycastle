using Godot;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// --car-loop (perf, ADR 0014): a headless driver on the normal map. It enters the nearest free car that has a drive block, weaves through the
/// street at full throttle for <c>--car-loop-seconds</c> (backing off and turning when it is stuck), and prints the distance it drove and its
/// reconciles. Nothing is checked: it is a load for the measurement (docs/perf-investigation).
/// </summary>
public sealed class CarLoopBot : SmokeBot
{
    /// <summary>The pace the loop holds, m/s.</summary>
    const float Speed = 18f;

    readonly double _seconds;
    IEnumerator<int>? _script;
    float _moveX, _moveY;
    MoveButtons _pulse;

    public CarLoopBot(IBotHost host, RozhlasTestBot? chat, double seconds) : base(host, chat) { _seconds = seconds; }

    public override MoveInput Step()
    {
        try
        {
            _script ??= Script().GetEnumerator();
            if (!Host.LinkEnded && !_script.MoveNext()) _script = null;
        }
        catch (SmokeAbort e) { GD.PrintErr($"client {Ctx.PlayerName}: car loop: {e.Message}"); FinishWalk(); }
        var mi = new MoveInput { MoveX = _moveX, MoveY = _moveY, Yaw = 0, Buttons = Ctx.Dead ? MoveButtons.None : _pulse, SpeedScale = 1 };
        _pulse = MoveButtons.None;
        return mi;
    }

    IEnumerable<int> Script()
    {
        foreach (var x in WaitFor(() => Host.OwnSpawned && Ctx.Me != null && !Ctx.Dead && Host.Cars.Any(v => v.Driver == 0 && !v.Destroyed && v.HasDriveBlock), 60, "a free car with a drive block")) yield return x;
        var me = Ctx.Me!.State.Position;
        var car = Host.Cars.Where(v => v.Driver == 0 && !v.Destroyed && v.HasDriveBlock).OrderBy(v => XZ(v.Pos - me)).First();
        var id = car.Id;
        var side = car.Pos + new NVec3(2.2f, 0, 0);
        Teleport(Ctx.MyId, side);
        foreach (var x in WaitFor(() => Host.OwnSpawned && NearXZ(Ctx.Me!.State.Position, side) && Ctx.Me!.State.OnFloor, 6, "teleport beside the car")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;
        _pulse = MoveButtons.Use;
        foreach (var x in WaitFor(() => Host.Driving && Host.Car(id) is { } cv && cv.Driver == Ctx.MyId, 3, "entering the car")) yield return x;
        GD.Print($"client {Ctx.PlayerName}: car loop: driving car {id} from ({car.Pos.X:F0},{car.Pos.Z:F0})");
        var t0 = Ctx.Now; var driven = 0f; var last = Car(id).Pos; var stuckSince = -1.0; var reverseUntil = -1.0; var stops = 0;
        while (Ctx.Now - t0 < _seconds && Host.Driving && Host.Car(id) is { } current && !current.Destroyed)
        {
            var now = Ctx.Now;
            var p = Car(id).Pos;
            driven += XZ(p - last); last = p;
            if (reverseUntil > now) { _moveY = -1; _moveX = -1; }
            else
            {
                // a weave, one period per 8 s, at a steady pace (a P controller on the speed): full throttle runs it into the street's walls
                var kmh = Car(id).Kmh;
                _moveY = Math.Clamp((Speed - kmh / 3.6f) * 0.5f, -1f, 1f);
                _moveX = (float)(0.5 * Math.Sin((now - t0) * 2 * Math.PI / 8));
                if (kmh < 5) { if (stuckSince < 0) stuckSince = now; else if (now - stuckSince > 1.5) { reverseUntil = now + 1.5; stuckSince = -1; stops++; } }
                else stuckSince = -1;
            }
            yield return 0;
        }
        _moveX = 0; _moveY = 0;
        GD.Print($"client {Ctx.PlayerName}: car loop: drove {driven:F0} m in {Ctx.Now - t0:F0} s, stuck {stops} times, still driving {Host.Driving}, reconciles {Host.Recon} (drive {Host.DriveCorrections})");
        GD.Print($"client {Ctx.PlayerName}: car loop rubber: {string.Join(" ", Host.Rubber.ToDict().Select(kv => $"{kv.Key}={kv.Value}"))}");
        FinishWalk();
        yield break;
    }
}
