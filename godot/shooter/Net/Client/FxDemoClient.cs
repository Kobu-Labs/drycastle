using Drycastle.Shooter.Net.Server.Diagnostics;
using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Server;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Dev aid: <c>--fx-demo dir</c> fires the rpg at the ground ahead (t=3 s) and then an AK burst (t=7 s), saving screenshots of the effects into dir.</summary>
internal sealed class FxDemoClient
{
    readonly ClientContext _ctx;

    public FxDemoClient(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The demo to run: a directory for the weapon demo, <c>vehicle:dir</c> or <c>drive:dir</c> for the vehicle ones; empty: none.</summary>
    public string Mode = "";
    double _demoStart;
    static readonly double[] DemoShots = { 3.1, 3.3, 3.6, 7.02, 7.06, 7.1, 7.14, 7.18, 7.22, 7.26, 7.3, 7.34, 12.7, 13.0, 13.4 };
    int _demoNext; bool _demoTp;

    // vehicle demo (<c>--fx-demo vehicle:dir</c> on a --blast-smoke server): stands 12 m in front of car 0 and hurts it through the stages and the blast, saving screenshots
    static readonly (double T, float Damage)[] VehicleDemoHits = { (1.5, 120), (5.0, 120), (9.0, 40), (12.0, 100) };
    static readonly double[] VehicleDemoShots = { 4.0, 8.0, 9.5, 10.5, 12.05, 12.15, 12.3, 12.6, 13.5, 16.0 };
    int _vdHit, _vdShot;

    void RunVehicle(ref MoveInput mi)
    {
        if (_ctx.Me == null || !_ctx.Connected() || _ctx.Cars.Count == 0) return;
        var dir = Mode["vehicle:".Length..];
        if (_demoStart == 0) { _demoStart = _ctx.Now; System.IO.Directory.CreateDirectory(dir); }
        var t = _ctx.Now - _demoStart;
        var c = new NVec3(SmokeArena.X, 0, SmokeArena.Z);
        if (!_demoTp) { _demoTp = true;
            _ctx.Send(EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_ctx.MyId, c + new NVec3(2, 0, -42))); }
        mi.Yaw = MathF.PI; _ctx.Me.Pitch = -0.08f;
        if (_vdHit < VehicleDemoHits.Length && t >= VehicleDemoHits[_vdHit].T)
        {
            _ctx.Send(EventKind.Debug, WeaponCodec.EncodeDebugVehicleDamage(VehicleRecord.FirstId, VehicleDemoHits[_vdHit++].Damage));
        }
        if (_vdShot < VehicleDemoShots.Length && t >= VehicleDemoShots[_vdShot]) _ = DemoShot(dir, VehicleDemoShots[_vdShot++]);
        if (t > 17) _ctx.Root.GetTree().Quit();
    }

    // drive demo (<c>--fx-demo drive:dir</c> on a real-map server with --debug-cmds): stands 4 m from the nearest vehicle, looks at it, presses E, drives ahead with a turn, brakes and exits; screenshots + positions in the log
    static readonly double[] DriveShots = { 3.5, 6.0, 8.0, 10.5, 13.0, 16.0 };
    int _ddShot; ushort _ddCar; NVec3 _ddStart;

    void RunDrive(ref MoveInput mi)
    {
        if (_ctx.Me == null || !_ctx.Connected() || _ctx.Cars.Count == 0) return;
        var dir = Mode["drive:".Length..];
        if (_demoStart == 0) { _demoStart = _ctx.Now; System.IO.Directory.CreateDirectory(dir); }
        var t = _ctx.Now - _demoStart;
        if (!_demoTp)
        {
            if (t < 1.5) return; // let a few snapshots in
            _demoTp = true;
            var me0 = _ctx.Me.State.Position;
            var pick = _ctx.Cars.ById.OrderBy(c => NVec3.Distance(c.Value.Pos, me0)).First();
            _ddCar = pick.Key; _ddStart = pick.Value.Pos;
            var at = pick.Value.Pos + new NVec3(0, 0.3f, 4.5f); // behind the car's rear is +Z for yaw 0; stand there, look at it
            var yaw = pick.Value.Yaw;
            var side = new NVec3(MathF.Sin(yaw), 0, MathF.Cos(yaw)) * 4.5f; // behind the car (it faces -Z at yaw 0)
            at = pick.Value.Pos + side + new NVec3(0, 0.3f, 0);
            _ddYaw = yaw;
            GD.Print($"drive-demo: nearest vehicle {pick.Key} kind {pick.Value.Kind} at {pick.Value.Pos}, teleport to {at}, distance before {NVec3.Distance(pick.Value.Pos, me0):F0} m");
            _ctx.Send(EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_ctx.MyId, at));
        }
        if (!_ctx.Driving) mi.Yaw = _ddYaw;
        if (!_ctx.Driving) _ctx.Me.Pitch = -0.1f;
        if (_ddShot < DriveShots.Length && t >= DriveShots[_ddShot]) _ = DemoShot(dir, DriveShots[_ddShot++]);
        var pos = _ctx.Cars.TryGet(_ddCar, out var cv) ? cv.Pos : _ddStart;
        if (t is > 4.5 and < 4.6) { mi.Buttons |= MoveButtons.Use; GD.Print($"drive-demo: E pressed, hint='{Strings.F("hud.vehicle.enter", InputBindings.Name("interact"))}' driving={_ctx.Driving}"); }
        if (t > 5 && t < 12) { mi.MoveY = 1; mi.MoveX = t > 8 && t < 10 ? 0.6f : 0f; }
        if (t is > 12 and < 13.4) mi.Buttons |= MoveButtons.Jump; // handbrake
        if (t is > 14 and < 14.1) mi.Buttons |= MoveButtons.Use;  // exit
        if (t > 6 && (int)(t * 2) != _ddLog) { _ddLog = (int)(t * 2); GD.Print($"drive-demo: t={t:F1} driving={_ctx.Driving} car {pos} moved {NVec3.Distance(pos, _ddStart):F1} m kmh {(_ctx.Cars.TryGet(_ddCar, out var c2) ? c2.Kmh : 0):F0}"); }
        if (t > 16.5) _ctx.Root.GetTree().Quit();
    }
    float _ddYaw; int _ddLog;

    public void Input(ref MoveInput mi)
    {
        if (Mode.StartsWith("drive:")) { RunDrive(ref mi); return; }
        if (Mode.StartsWith("vehicle:")) { RunVehicle(ref mi); return; }
        if (Mode == "" || _ctx.Me == null) return;
        if (_demoStart == 0) { _demoStart = _ctx.Now; System.IO.Directory.CreateDirectory(Mode); }
        var t = _ctx.Now - _demoStart;
        if (!_demoTp && _ctx.Connected())
        {
            _demoTp = true; // open ground, away from the cars that some spawns sit in
            _ctx.Send(EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_ctx.MyId, new NVec3(-78f, 191.2f, 52.9f)));
        }
        _ctx.Hotbar.Want = t < 6 ? 3 : 2;
        _ctx.Me.Pitch = t < 6 ? -0.2f : -0.05f;
        if (t > 7) { mi.MoveX = t < 7.2 ? 1 : -1; mi.Yaw += (float)(t - 7) * 0.8f; } // strafe + turn: the flash must stay on the barrel
        if ((t is > 3 and < 3.1) || (t is > 7 and < 8)) mi.Buttons |= MoveButtons.Fire;
        if (_demoNext < DemoShots.Length && t >= DemoShots[_demoNext]) _ = DemoShot(Mode, DemoShots[_demoNext++]);
        // wall phase: face the nearest wall found by horizontal rays and fire an AK burst at it (bullet holes)
        var wallSpots = new[] { new NVec3(-1f, 192f, 14.9f), new NVec3(-1f, 192f, -14.9f), new NVec3(-1f, 192f, 0f) };
        for (var k = 0; k < wallSpots.Length && _wallYaw == null; k++)
        {
            if (_wallStep == 2 * k && t > 8 + 1.7 * k && _ctx.Connected())
            {
                _wallStep++; GD.Print($"fx-demo: wall spot {k}");
            _ctx.Send(EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_ctx.MyId, wallSpots[k]));
            }
            else if (_wallStep == 2 * k + 1 && t > 9.2 + 1.7 * k)
            {
                _wallStep++; var best = 1e9f; var from = _ctx.Me.Camera.GlobalPosition;
                for (var i = 0; i < 24; i++)
                {
                    var yaw = i * Mathf.Tau / 24;
                    var d = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
                    // a real wall: rays at three heights hit the same vertical plane
                    var ok = true; var dd = 0f;
                    foreach (var dy in new[] { -0.8f, 0f, 1.2f })
                    {
                        var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from + Vector3.Up * dy, from + Vector3.Up * dy + d * 25f, 1));
                        if (hit.Count == 0 || Mathf.Abs(hit["normal"].AsVector3().Y) > 0.15f) { ok = false; break; }
                        var hd = from.DistanceTo(hit["position"].AsVector3());
                        if (dd != 0 && Mathf.Abs(hd - dd) > 0.6f) { ok = false; break; }
                        dd = hd;
                    }
                    if (ok && dd > 3f && dd < best) { best = dd; _wallYaw = yaw; }
                }
                GD.Print($"fx-demo: wall yaw {_wallYaw} dist {best} at {from}");
            }
        }
        if (t > 8 && _wallYaw is { } wy)
        {
            mi.Yaw = wy; _ctx.Me.Pitch = 0;
            var o = _ctx.Me.Camera.GlobalPosition; var fw = -_ctx.Me.Camera.GlobalTransform.Basis.Z;
            var ahead = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(o, o + fw * 40f, 1));
            if (t < 12.5 && (ahead.Count == 0 || o.DistanceTo(ahead["position"].AsVector3()) > 3f)) mi.MoveY = 1;
        }
        if (_wallYaw != null && (t is > 12.5 and < 13)) mi.Buttons |= MoveButtons.Fire;
        if (t > 15) _ctx.Root.GetTree().Quit();
    }
    float? _wallYaw; int _wallStep;

    async System.Threading.Tasks.Task DemoShot(string dir, double at)
    {
        await _ctx.Root.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var path = System.IO.Path.Combine(dir, $"fx_{at:F2}.png");
        _ctx.Root.GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"fx-demo: saved {path}");
    }
}
