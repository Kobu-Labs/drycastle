using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Scud;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Scud truck on the client (GLOSSARY "Scud truck", "Launcher", "Lock point", "Scud missile", "Scud blast"). Everyone draws the truck's rack from the launcher's replicated state
/// (<see cref="ScudRules.RackAngle"/>, the model's <c>rack</c> and <c>missile</c> nodes, skipped when a model has none), the launch smoke, the missile in flight (the world entity) with its flame
/// and trail, the siren and banner for those near the target, the blast, its shake and the scorch.
/// <para>
/// The crew has the screen (<see cref="ScudScreen"/>): the operator's bird's-eye infrared camera, panned with the movement keys and zoomed with the wheel, primary fire locks the point under the
/// reticle, the confirm key confirms, the abort key withdraws or aborts; any other seat can open the same picture read-only from the operator's replicated camera. While it is up the camera is
/// <see cref="Player.Turret"/>'s, straight down from <see cref="ScudTargetingData.AltitudeM"/>, and the body's own keys are the screen's (the input to the server carries no movement), like the AC-130's turret.
/// </para>
/// <para>
/// What the remote picture shows: the whole map is loaded on every client, so terrain, buildings, props, glass, vehicles and players (all of them are replicated at every range) are there as far
/// as the camera's far plane. Animals, flocks and droppings are not: they are sent only within 800 m of the player's own body (ADR 0025), which is the truck.
/// </para>
/// </summary>
public partial class NetClient
{
    /// <summary>scud.json, replaced by the host with the file's.</summary>
    public ScudFile Scud = ScudFile.Default;

    sealed class ScudTruck
    {
        public ScudStateMsg Msg;
        public double At;
        public Node3D? Rack, MissileOnRack;
        public Basis RackRest = Basis.Identity;
        public bool Looked;
        public CpuParticles3D? Ignition;
        /// <summary>The eight wheels (<c>wheel_l1..l4</c>, <c>wheel_r1..r4</c>) and their rest poses, and the steering parents of the front two axles (<c>wheel_l1_steer</c>, <c>wheel_r1_steer</c>, <c>wheel_l2_steer</c>, <c>wheel_r2_steer</c>) with the axle each belongs to (an index into the type's <c>steer_arms</c>).</summary>
        public Node3D[] Wheels = Array.Empty<Node3D>();
        public Basis[] WheelRest = Array.Empty<Basis>();
        public Node3D[] Steers = Array.Empty<Node3D>();
        public int[] SteerAxle = Array.Empty<int>();
        public Vector3 LastPos;
        public float WheelSpin, LastYaw, Curvature;
        /// <summary>The operator's camera as last sent (the feed), and when.</summary>
        public float ViewX, ViewZ, ViewZoom = 1f;
        public double ViewAt;

        public ScudTruck(ushort id) => Msg = new ScudStateMsg(id, ScudLauncherState.Stowed, 0, float.PositiveInfinity, 0, 0, null, 0, 0);
    }

    /// <summary>One missile in the air as the launch event told it: where it lands and when, and whether its warning was given.</summary>
    sealed class ScudFlightInfo
    {
        public required ScudLaunchMsg Msg;
        public double ArriveAt;
        public bool Marked;
    }

    sealed class ScudMissileView
    {
        public required Node3D Node;
        public LoopVoice? Snd;
    }

    [Flags]
    enum ScudKey : byte { None = 0, Screen = 1, Confirm = 2, Abort = 4, Lock = 8 }

    readonly Dictionary<ushort, ScudTruck> _scudTrucks = new();
    readonly Dictionary<ushort, ScudMissileView> _scudMissiles = new();
    readonly Dictionary<ushort, ScudFlightInfo> _scudFlights = new();
    readonly ScudScreen _scudScreen = new();
    ScudOverlay? _scudOverlay;
    ScudWarning? _scudWarning;
    ScudKey _scudKeys;
    /// <summary>The operator's camera: centre (ground x, z) and zoom. The feed's is smoothed toward the one the server relays.</summary>
    Vector2 _scudCentre;
    float _scudZoom = 1f;
    double _scudViewSentAt;
    Vector2 _scudViewSent;
    float _scudZoomSent;
    ScudScreenMode _scudModeWas;
    double _scudSirenAt;
    readonly List<(double At, Vector3 Where)> _scudFelt = new();
    double _scudShakeStart = -10, _scudShakeSeconds;
    float _scudShakeAmp;
    // strings that change rarely, rebuilt on change
    Change<int> _scudStatusKey, _scudHintKey, _scudCrewKey, _scudPromptKey, _scudGridKey, _scudLockKey;
    string _scudPrompt = "";

    /// <summary>The screen is up (the operator's or the feed): the camera, the keys and the HUD are the screen's, as at a turret.</summary>
    bool ScudScreenOn => _scudScreen.Mode != ScudScreenMode.Closed;

    void InitScud()
    {
        ScudMissileRules.Register(WorldEntityRegistry.Shared);
        EventReceived += OnScudEvent;
        WorldEntities.Spawned += OnScudSpawned;
        WorldEntities.Despawned += OnScudDespawned;
        if (!ShotFx.Enabled) return;
        _scudOverlay = new ScudOverlay();
        AddChild(_scudOverlay);
        _scudWarning = new ScudWarning();
        AddChild(_scudWarning);
    }

    // ---- events ----

    void OnScudEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.ScudState when ScudCodec.DecodeState(payload) is { } st:
                var t = TruckOf(st.Car);
                t.Msg = st; t.At = Ctx.Now;
                break;
            case EventKind.ScudView when ScudCodec.DecodeView(payload) is { } v:
                var vt = TruckOf(v.Car);
                vt.ViewX = v.X; vt.ViewZ = v.Z; vt.ViewZoom = v.Zoom; vt.ViewAt = Ctx.Now;
                break;
            case EventKind.ScudLaunch when ScudCodec.DecodeLaunch(payload) is { } l:
                OnScudLaunch(l);
                break;
            case EventKind.ScudImpact when ScudCodec.DecodeImpact(payload) is { } i:
                OnScudImpact(i);
                break;
            case EventKind.ScudNotice when ScudCodec.DecodeNotice(payload) is { } n:
                UiShell.Instance?.Toast(NoticeText(n), n is ScudNoticeCode.Retarget or ScudNoticeCode.LoadStarted or ScudNoticeCode.EventFound ? ToastKind.Info : ToastKind.Warn);
                break;
        }
    }

    ScudTruck TruckOf(ushort id)
    {
        if (!_scudTrucks.TryGetValue(id, out var t)) _scudTrucks[id] = t = new ScudTruck(id);
        return t;
    }

    string NoticeText(ScudNoticeCode n) => n switch
    {
        ScudNoticeCode.TooClose => Strings.F("scud.notice.too_close", (int)Scud.Launcher.MinRangeM),
        ScudNoticeCode.OffMap => Strings.T("scud.notice.off_map"),
        ScudNoticeCode.Moving => Strings.T("scud.notice.moving"),
        ScudNoticeCode.NotOperator => Strings.T("scud.notice.not_operator"),
        ScudNoticeCode.CrewChanged => Strings.T("scud.notice.crew_changed"),
        ScudNoticeCode.Withdrawn => Strings.T("scud.notice.withdrawn"),
        ScudNoticeCode.Moved => Strings.T("scud.notice.moved"),
        ScudNoticeCode.Timeout => Strings.T("scud.notice.timeout"),
        ScudNoticeCode.Retarget => Strings.T("scud.notice.retarget"),
        ScudNoticeCode.LoadStarted => Strings.T("scud.notice.load_started"),
        ScudNoticeCode.LoadCancelled => Strings.T("scud.notice.load_cancelled"),
        ScudNoticeCode.EventFound => Strings.F("scud.notice.event_found", (int)Scud.Event.XpReward),
        _ => Strings.T("scud.notice.wrong_state"),
    };

    void SendScud(ScudCmd cmd, float x = 0, float z = 0, float zoom = 1f)
    {
        if (_peer == null) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.ScudCommand, ScudCodec.Encode(new ScudCommandMsg(cmd, x, z, zoom)))), (int)ENetPacketPeer.FlagReliable);
    }

    // ---- the missile ----

    void OnScudSpawned(WorldReplica.Entity e)
    {
        if (e.Type != ScudMissileRules.TypeId || _scudMissiles.ContainsKey(e.Id)) return;
        _scudMissiles[e.Id] = new ScudMissileView { Node = ScudFx.MakeMissile(Ctx.Owned) };
    }

    void OnScudDespawned(ushort id)
    {
        if (!_scudMissiles.Remove(id, out var m)) return;
        m.Snd?.Release();
        ScudFx.Retire(Ctx.Owned, m.Node);
    }

    void OnScudLaunch(ScudLaunchMsg m)
    {
        _scudFlights[m.Missile] = new ScudFlightInfo { Msg = m, ArriveAt = Ctx.Now + m.Seconds };
        var at = new Vector3(m.From.X, m.From.Y, m.From.Z);
        if (Ctx.Cars.TryGet(m.Car, out var car) && Vehicles != null && car.Kind < Vehicles.Vehicles.Count && Vehicles.Vehicles[car.Kind].Launcher is { } l)
            at = car.Node.GlobalTransform * new Vector3(l.Exhaust[0], l.Exhaust[1], l.Exhaust[2]);
        ScudFx.LaunchBurst(Ctx.Owned, at);
        if (Sfx.Enabled) Sfx.Play3D("scud.launch", at);
        Ctx.Shake.At(at, 25f);
    }

    /// <summary>The blast: the biggest the game has (the blast look and its Blast wave at the scud.json size), then, when the wave reaches the player, its sound and a long shake scaled by the distance; the scorch stays.</summary>
    void OnScudImpact(ScudImpactMsg m)
    {
        _scudFlights.Remove(m.Missile);
        var at = new Vector3(m.At.X, m.At.Y, m.At.Z);
        var b = Scud.Blast;
        if (_scudMissiles.Remove(m.Missile, out var view)) { view.Snd?.Release(); ScudFx.Retire(Ctx.Owned, view.Node); }
        RocketFx.Explode(Ctx.Owned, at, (float)b.FxRadiusM, (float)b.WaveSpeedMps);
        ScudFx.Scorch(Ctx.Owned, at, (float)b.ScorchRadiusM);
        var from = Ctx.Me?.GlobalPosition ?? at;
        var wait = ScudBlastRules.ArrivalSeconds(b, new Vector2(from.X - at.X, from.Z - at.Z).Length());
        if (_scudFelt.Count < 8) _scudFelt.Add((Ctx.Now + wait, at));
    }

    /// <summary>A blast's wave has reached the player: the sound and the shake (reduce-motion: the sound only).</summary>
    void FeelScud(Vector3 at)
    {
        if (Sfx.Enabled) Sfx.Play3D("scud.explosion", at);
        _blastDust.RingFrom(at, (float)Scud.Blast.DamageRadiusM);
        if (Ctx.Me == null || !ShotFx.Enabled || GameSettings.Current.ReduceMotion) return;
        var dist = new Vector2(Ctx.Me.GlobalPosition.X - at.X, Ctx.Me.GlobalPosition.Z - at.Z).Length();
        var amp = ScudBlastRules.Shake(Scud.Blast, dist);
        if (amp <= 0.002f) return;
        _scudShakeStart = Ctx.Now; _scudShakeSeconds = 2.5; _scudShakeAmp = MathF.Max(_scudShakeAmp * 0.5f, amp);
    }

    void ScudRoundStart()
    {
        ScudFx.ClearScorches();
        _scudFlights.Clear();
    }

    // ---- every frame ----

    /// <summary>Once a frame after the vehicles and the entities are placed: the trucks' racks, the missiles, the screen, the warning and the blast's late effects.</summary>
    void RenderScud(double delta)
    {
        if (Ctx.LastTick == 0) return;
        AnimateScudTrucks((float)delta);
        RenderScudMissiles();
        StepScudScreen((float)delta);
        StepScudWarning();
        StepScudFelt();
    }

    /// <summary>The current state of a truck's launcher for drawing: the last message, its clock counted on from when it arrived.</summary>
    (ScudLauncherState State, float Elapsed, float RackFrom) ScudNow(ScudTruck t) => (t.Msg.State, t.Msg.Elapsed + (float)(Ctx.Now - t.At), t.Msg.RackFrom);

    /// <summary>The seconds left of the state's clock, counted on from the last message (infinity: no clock).</summary>
    float ScudLeft(ScudTruck t) => float.IsFinite(t.Msg.Left) ? t.Msg.Left - (float)(Ctx.Now - t.At) : float.PositiveInfinity;

    void AnimateScudTrucks(float dt)
    {
        if (Vehicles == null) return;
        foreach (var v in Ctx.Cars.All)
        {
            if (v.Kind >= Vehicles.Vehicles.Count || Vehicles.Vehicles[v.Kind].Launcher is not { } launcher || v.Destroyed) continue;
            var t = TruckOf(v.Id);
            if (!ShotFx.Enabled) continue;
            if (!t.Looked)
            {
                t.Looked = true;
                // the model's nodes by name; a model without one just does not animate it
                t.Rack = v.Node.FindChild("rack", true, false) as Node3D;
                t.RackRest = t.Rack?.Basis ?? Basis.Identity;
                t.MissileOnRack = t.Rack?.FindChild("missile", true, false) as Node3D;
                t.Wheels = v.Node.FindChildren("wheel_*", "Node3D", true, false).OfType<Node3D>().Where(w => !w.Name.ToString().EndsWith("_steer")).ToArray();
                t.WheelRest = t.Wheels.Select(w => w.Basis).ToArray();
                var steers = new List<Node3D>(); var axles = new List<int>();
                for (var axle = 0; axle < launcher.SteerArms.Length; axle++)
                    foreach (var side in new[] { "l", "r" })
                        if (v.Node.FindChild("wheel_" + side + (axle + 1) + "_steer", true, false) is Node3D steer) { steers.Add(steer); axles.Add(axle); }
                t.Steers = steers.ToArray(); t.SteerAxle = axles.ToArray();
                t.LastYaw = v.Node.Rotation.Y;
                t.Ignition = ScudFx.IgnitionSmoke(v.Node, new Vector3(launcher.Exhaust[0], launcher.Exhaust[1], launcher.Exhaust[2]));
                t.LastPos = v.Node.GlobalPosition;
            }
            var (state, elapsed, from) = ScudNow(t);
            if (t.Rack != null)
            {
                var angle = ScudRules.RackAngle(state, elapsed, from, Scud.Launcher);
                t.Rack.Basis = new Basis(Vector3.Right, angle) * t.RackRest; // up about the rear hinge: positive about X lifts a rack that lies forward (-Z)
            }
            if (t.MissileOnRack != null) t.MissileOnRack.Visible = ScudRules.RocketOnRack(state, ScudLeft(t), Scud.Launcher);
            if (t.Ignition != null) ParticleFx.Run(t.Ignition, state == ScudLauncherState.Launching);
            // all eight wheels roll by the distance driven along the truck's own heading, and the front two axles steer by the path's curvature (the drawn yaw over the drawn distance; the truck is not predicted)
            var pos = v.Node.GlobalPosition;
            var yaw = v.Node.Rotation.Y;
            if (dt > 1e-4f)
            {
                var fwd = -v.Node.GlobalTransform.Basis.Z;
                var ds = (pos - t.LastPos).Dot(fwd);
                if (MathF.Abs(ds) > 5f) ds = 0f; // a jump (a return to its place) is not driving
                t.WheelSpin = Mathf.Wrap(t.WheelSpin - ds / launcher.WheelRadius, -Mathf.Pi, Mathf.Pi);
                var target = MathF.Abs(ds) > 0.005f ? Math.Clamp(FlightMath.Wrap(yaw - t.LastYaw) / ds, -0.25f, 0.25f) : 0f; // 1 / radius, positive to the left
                t.Curvature += (target - t.Curvature) * (1f - MathF.Exp(-8f * dt));
                for (var i = 0; i < t.Wheels.Length; i++) if (GodotObject.IsInstanceValid(t.Wheels[i])) t.Wheels[i].Basis = new Basis(Vector3.Right, t.WheelSpin) * t.WheelRest[i]; // about the truck's own lateral axis, over the wheel's rest pose
                for (var i = 0; i < t.Steers.Length; i++) if (GodotObject.IsInstanceValid(t.Steers[i])) t.Steers[i].Rotation = new Vector3(0, ScudRules.WheelAngle(launcher.SteerArms[t.SteerAxle[i]], t.Curvature), 0);
            }
            t.LastPos = pos; t.LastYaw = yaw;
        }
    }

    void RenderScudMissiles()
    {
        if (_scudMissiles.Count == 0) return;
        var rt = RenderTickNow();
        foreach (var (id, view) in _scudMissiles)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || !GodotObject.IsInstanceValid(view.Node)) continue;
            var (p, yaw, pitch) = e.Buf.At(rt) is { } s ? (new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), s.Yaw, s.Pitch) : (new Vector3(e.State.Position.X, e.State.Position.Y, e.State.Position.Z), e.State.YawRad, e.State.PitchRad);
            view.Node.GlobalPosition = p;
            view.Node.Rotation = new Vector3(pitch, yaw, 0); // nose along -Z: the heading HitMath.Forward gives
            if (!Sfx.Enabled) continue;
            view.Snd ??= Sfx.Loop("scud.flight", p);
            view.Snd?.Update(p);
        }
    }

    // ---- the screen ----

    /// <summary>The truck the local player sits in, with its launcher's state and their seat, or null (on foot, dead, in another vehicle, or the truck is gone).</summary>
    (CarView Car, ScudTruck Truck, VehicleDef Def, int Seat, bool IsOperator)? MyScudSeat()
    {
        if (Dead || Ctx.Cars.MySeat() is not { } ride || ride.Def.Launcher is not { } launcher || ride.Car.Destroyed) return null;
        return (ride.Car, TruckOf(ride.Car.Id), ride.Def, ride.Index, ride.Index == launcher.OperatorSeat);
    }

    /// <summary>With the screen up the movement keys and buttons are the screen's: the input sent to the server carries none (the truck stays as it is), only Use (leave the truck) and the lights.</summary>
    void ScudFilterInput(ref MoveInput mi)
    {
        if (!ScudScreenOn) return;
        mi.MoveX = 0; mi.MoveY = 0;
        mi.Buttons &= MoveButtons.Use | MoveButtons.Headlights;
    }

    /// <summary>The screen's keys, in the order the unhandled input handles them. True when the event was the screen's.</summary>
    bool HandleScudInput(InputEvent e)
    {
        if (Bot || Dead || Input.MouseMode != Input.MouseModeEnum.Captured) return false;
        if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (!ScudScreenOn) return false;
            if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                if (_scudScreen.Mode == ScudScreenMode.Operating)
                    _scudZoom = Mathf.Clamp(mb.ButtonIndex == MouseButton.WheelUp ? _scudZoom * (float)Scud.Targeting.ZoomStep : _scudZoom / (float)Scud.Targeting.ZoomStep, (float)Scud.Targeting.ZoomMin, (float)Scud.Targeting.ZoomMax);
                return true; // the wheel is the zoom here, never the weapon
            }
            if (e.IsActionPressed("fire")) { _scudKeys |= ScudKey.Lock; return true; }
            return false;
        }
        if (e is not InputEventKey { Echo: false }) return false;
        if (e.IsActionPressed("scud_screen") && MyScudSeat() != null) { _scudKeys |= ScudKey.Screen; return true; }
        if (!ScudScreenOn) return false;
        if (e.IsActionPressed("scud_confirm")) { _scudKeys |= ScudKey.Confirm; return true; }
        if (e.IsActionPressed("scud_abort")) { _scudKeys |= ScudKey.Abort; return true; }
        return false;
    }

    void StepScudScreen(float dt)
    {
        var keys = _scudKeys; _scudKeys = ScudKey.None;
        var seat = MyScudSeat();
        var state = seat?.Truck.Msg.State ?? ScudLauncherState.Stowed;
        var action = _scudScreen.Update(Ctx.Now, seat != null, seat?.IsOperator ?? false, state, keys.HasFlag(ScudKey.Screen));
        if (action == ScudScreenAction.SendOpen) SendScud(ScudCmd.Open);
        else if (action == ScudScreenAction.SendClose) SendScud(ScudCmd.Close);
        var mode = _scudScreen.Mode;
        if (mode == ScudScreenMode.Closed)
        {
            if (_scudModeWas != ScudScreenMode.Closed) EndScudScreen();
            _scudModeWas = mode;
            return;
        }
        if (Ctx.Me == null || seat is not { } s || _scudOverlay == null && ShotFx.Enabled) return;
        var truckAt = s.Car.Node.GlobalPosition;
        var tun = Scud.Targeting;
        if (_scudModeWas == ScudScreenMode.Closed)
        {
            // opening: the operator starts where the feed was, else over the truck; the feed starts where the operator is
            var fresh = Ctx.Now - s.Truck.ViewAt < Scud.Launcher.ViewStaleSeconds * 4;
            _scudCentre = fresh ? new Vector2(s.Truck.ViewX, s.Truck.ViewZ) : new Vector2(truckAt.X, truckAt.Z);
            _scudZoom = fresh ? Mathf.Clamp(s.Truck.ViewZoom, (float)tun.ZoomMin, (float)tun.ZoomMax) : 1f;
            Ctx.Me.TurretLook = true;
        }
        _scudModeWas = mode;
        var operating = mode == ScudScreenMode.Operating;
        if (operating)
        {
            // pan with the movement keys (north is up), slower the more it is zoomed in, and stay on the map
            var axis = new Vector2(Input.GetAxis("move_left", "move_right"), Input.GetAxis("move_forward", "move_back"));
            if (Input.MouseMode == Input.MouseModeEnum.Captured && axis != Vector2.Zero)
            {
                _scudCentre += axis * ((float)tun.PanSpeedMps / _scudZoom * dt);
                ClampToMap(ref _scudCentre);
            }
            if (keys.HasFlag(ScudKey.Lock) && state is ScudLauncherState.Targeting or ScudLauncherState.Proposed) SendScud(ScudCmd.Lock, _scudCentre.X, _scudCentre.Y);
            if (state is ScudLauncherState.Targeting or ScudLauncherState.Proposed && (_scudCentre != _scudViewSent || MathF.Abs(_scudZoom - _scudZoomSent) > 1e-3f) && Ctx.Now - _scudViewSentAt >= 1.0 / tun.ViewHz)
            {
                _scudViewSentAt = Ctx.Now; _scudViewSent = _scudCentre; _scudZoomSent = _scudZoom;
                SendScud(ScudCmd.View, _scudCentre.X, _scudCentre.Y, _scudZoom);
            }
        }
        else if (Ctx.Now - s.Truck.ViewAt < Scud.Launcher.ViewStaleSeconds)
        {
            // the feed: the operator's camera as relayed, eased so the five-a-second updates glide
            var k = 1f - Mathf.Exp(-12f * dt);
            _scudCentre = _scudCentre.Lerp(new Vector2(s.Truck.ViewX, s.Truck.ViewZ), k);
            _scudZoom = Mathf.Lerp(_scudZoom, s.Truck.ViewZoom, k);
        }
        // confirm, withdraw and abort (a screen of any seat)
        var myBit = 1 << s.Seat;
        var iConfirmed = (s.Truck.Msg.Confirmed & myBit) != 0;
        if (keys.HasFlag(ScudKey.Confirm) && state == ScudLauncherState.Proposed && !iConfirmed) SendScud(ScudCmd.Confirm);
        if (keys.HasFlag(ScudKey.Abort))
        {
            if (state == ScudLauncherState.Proposed && iConfirmed) SendScud(ScudCmd.Withdraw);
            else if (state == ScudLauncherState.Erecting && s.IsOperator) SendScud(ScudCmd.Abort);
        }
        // the camera: straight down, north up, from a fixed height over the truck
        var camAt = new Vector3(_scudCentre.X, truckAt.Y + (float)tun.AltitudeM, _scudCentre.Y);
        Ctx.Me.Turret(camAt, 0f, -Mathf.Pi / 2f + 0.0005f);
        Ctx.Me.ZoomFov = (float)tun.FovDeg / _scudZoom;
        Ctx.Me.TurretLook = true;
        FillScudOverlay(s, state, operating, iConfirmed, truckAt);
    }

    void ClampToMap(ref Vector2 centre)
    {
        var b = FlightProbe.MapBounds(Map);
        if (b == FlightBounds.None) return;
        centre.X = Mathf.Clamp(centre.X, b.MinX, b.MaxX);
        centre.Y = Mathf.Clamp(centre.Y, b.MinZ, b.MaxZ);
    }

    /// <summary>The screen closed: back to the body's own view, looking where the car looks.</summary>
    void EndScudScreen()
    {
        _scudOverlay?.SetOn(false);
        if (Ctx.Me == null) return;
        Ctx.Me.TurretLook = false;
        Ctx.Me.ZoomFov = 0f;
        // no EndChase: the vehicle's own camera (RenderCars, before this) already holds the frame
    }

    void FillScudOverlay(in (CarView Car, ScudTruck Truck, VehicleDef Def, int Seat, bool IsOperator) s, ScudLauncherState state, bool operating, bool iConfirmed, Vector3 truckAt)
    {
        if (_scudOverlay == null) return;
        var o = _scudOverlay.State;
        o.Operating = operating;
        o.Launcher = state;
        o.Pending = _scudScreen.Pending;
        o.Camera = Ctx.Me!.Camera;
        o.Truck = truckAt;
        o.MinRangeM = (float)Scud.Launcher.MinRangeM;
        o.Zoom = _scudZoom;
        var msg = s.Truck.Msg;
        var (_, elapsed, _) = ScudNow(s.Truck);
        o.Countdown = state is ScudLauncherState.Erecting or ScudLauncherState.Launching && float.IsFinite(msg.Left) ? Math.Max(0, (int)MathF.Ceiling(msg.Left - (elapsed - msg.Elapsed))) : -1;
        o.Lock = msg.Lock is { } l ? new Vector3(l.X, l.Y, l.Z) : null;

        // the reticle reads where it is: the grid and the range from the truck, and whether that is inside the minimum range
        var range = new Vector2(_scudCentre.X - truckAt.X, _scudCentre.Y - truckAt.Z).Length();
        o.RangeM = (int)MathF.Round(range);
        o.TooClose = range < Scud.Launcher.MinRangeM;
        var cell = GridKey(_scudCentre.X, _scudCentre.Y);
        if (_scudGridKey.Update(cell)) o.Grid = GridRef(_scudCentre.X, _scudCentre.Y);
        if (msg.Lock is { } lk)
        {
            o.LockRangeM = (int)MathF.Round(new Vector2(lk.X - truckAt.X, lk.Z - truckAt.Z).Length());
            if (_scudLockKey.Update(GridKey(lk.X, lk.Z))) o.LockGrid = GridRef(lk.X, lk.Z);
        }

        // the words: built when what they say changes
        if (_scudStatusKey.Update((int)state * 10000 + o.Countdown + 1 + (o.Pending ? 5000 : 0) + (operating ? 1000 : 0)))
        {
            o.Role = Strings.T(operating ? "scud.role.operator" : "scud.role.watch");
            o.Status = state switch
            {
                ScudLauncherState.Targeting => Strings.T(operating ? "scud.status.targeting" : "scud.status.targeting.watch"),
                ScudLauncherState.Proposed => Strings.T("scud.status.proposed"),
                ScudLauncherState.Erecting => Strings.F("scud.status.erecting", o.Countdown),
                ScudLauncherState.Launching => Strings.F("scud.status.launching", o.Countdown),
                _ => "",
            };
        }
        var confirmKey = InputBindings.Name("scud_confirm"); var abortKey = InputBindings.Name("scud_abort"); var screenKey = InputBindings.Name("scud_screen");
        if (_scudHintKey.Update((int)state * 8 + (operating ? 4 : 0) + (iConfirmed ? 2 : 0) + (s.IsOperator ? 1 : 0)))
        {
            var fire = InputBindings.Name("fire");
            var pan = InputBindings.Name("move_forward") + InputBindings.Name("move_left") + InputBindings.Name("move_back") + InputBindings.Name("move_right");
            o.Hint = (operating, state) switch
            {
                (true, ScudLauncherState.Targeting) => Strings.F("scud.hint.targeting", fire, pan, screenKey),
                (true, ScudLauncherState.Proposed) => Strings.F("scud.hint.proposed", confirmKey, abortKey, fire, screenKey),
                (true, ScudLauncherState.Erecting) => Strings.F("scud.hint.erecting", abortKey, screenKey),
                (_, ScudLauncherState.Launching) or (true, _) => Strings.F("scud.hint.launching", screenKey),
                (false, ScudLauncherState.Proposed) => Strings.F("scud.hint.watch.proposed", confirmKey, abortKey, screenKey),
                (false, ScudLauncherState.Targeting) => Strings.F("scud.hint.watch.targeting", screenKey),
                _ => Strings.F("scud.hint.watch.under_way", screenKey),
            };
        }
        // who must confirm and who has
        var seats = Ctx.Cars.Seats?.Occupants ?? Array.Empty<ushort>();
        var crewKey = (msg.Need << 8) | msg.Confirmed | ((int)state << 16) | (s.Seat << 24);
        if (_scudCrewKey.Update(crewKey))
        {
            o.Crew.Clear();
            for (var i = 0; i < s.Def.Seats.Count; i++)
            {
                if ((msg.Need & (1 << i)) == 0) continue;
                var label = Strings.T(i == 0 ? "scud.seat.driver" : i == s.Def.Launcher!.OperatorSeat ? "scud.seat.operator" : "scud.seat.other");
                if (i != 0 && i != s.Def.Launcher!.OperatorSeat) label = Strings.F("scud.seat.other", i + 1);
                if (i == s.Seat) label = Strings.F("scud.crew.you", label);
                var done = (msg.Confirmed & (1 << i)) != 0;
                o.Crew.Add(new ScudScreenState.CrewRow($"{label}   {Strings.T(done ? "scud.crew.confirmed" : "scud.crew.waiting")}", done));
            }
        }
        _scudOverlay.SetOn(true);
    }

    /// <summary>A key that changes when a ground point moves to another metre (the grid reference and range are rebuilt on it).</summary>
    static int GridKey(float x, float z) => (int)MathF.Round(x / 10f) * 7919 + (int)MathF.Round(z / 10f);

    string GridRef(float x, float z)
    {
        var b = MapBase.For(Map)?.World;
        return b == null ? $"{x:0},{z:0}" : TacticalMapMath.ReferenceOf(new NVec2(x, z), b.Origin.X, b.SouthZ, b.SideMetres);
    }

    // ---- the prompt line while the screen is closed ----

    /// <summary>The Scud seat's line for the HUD's prompt: the launcher's state and the key that opens the screen, then the vehicle's own prompt.</summary>
    string ScudPrompt(string? basePrompt)
    {
        if (ScudScreenOn || MyScudSeat() is not { } s) return basePrompt ?? "";
        var (state, elapsed, _) = ScudNow(s.Truck);
        var left = float.IsFinite(s.Truck.Msg.Left) ? Math.Max(0, (int)MathF.Ceiling(s.Truck.Msg.Left - (elapsed - s.Truck.Msg.Elapsed))) : 0;
        if (_scudPromptKey.Update((int)state * 1000 + left + (s.IsOperator ? 100000 : 0) + (s.Truck.Msg.Operator != 0 ? 200000 : 0) + (state == ScudLauncherState.Empty && ScudLoadOffer() ? 400000 : 0)))
        {
            var screen = InputBindings.Name("scud_screen");
            _scudPrompt = state switch
            {
                ScudLauncherState.Stowed => s.IsOperator ? Strings.F("scud.hint.open", screen) : Strings.T("scud.hint.watch.none"),
                ScudLauncherState.Targeting => s.IsOperator ? Strings.F("scud.hint.open", screen) : Strings.F("scud.hint.watch", screen),
                ScudLauncherState.Proposed => Strings.F("scud.line.proposed", InputBindings.Name("scud_confirm")) + "   " + Strings.F("scud.hint.watch", screen),
                ScudLauncherState.Erecting => Strings.F("scud.line.erecting", left) + "   " + Strings.F("scud.hint.watch", screen),
                ScudLauncherState.Launching => Strings.F("scud.line.launching", left),
                ScudLauncherState.Spent => Strings.T("scud.line.spent"),
                ScudLauncherState.Lowering => Strings.T("scud.line.lowering"),
                ScudLauncherState.Empty => Strings.T("scud.line.empty") + (ScudLoadOffer() ? "   " + Strings.F("scud.hint.load", InputBindings.Name("interact")) : ""),
                ScudLauncherState.Loading => Strings.F("scud.line.loading", left),
                ScudLauncherState.Reloading => Strings.F("scud.line.reloading", left),
                _ => Strings.T("scud.line.aborting"),
            };
        }
        return basePrompt is { Length: > 0 } ? _scudPrompt + "     " + basePrompt : _scudPrompt;
    }

    /// <summary>
    /// Whether the local player may start loading a rocket from a cart now: the truck they ride (any seat) or one within reach on foot has an empty rack and a loaded, live cart stands within reach of its box
    /// (<see cref="ScudReloadRules"/>, the server's own rules on the vehicles as drawn; the server decides).
    /// </summary>
    bool ScudLoadOffer()
    {
        if (Dead || Ctx.Me == null || Vehicles == null) return false;
        var mine = MyScudSeat()?.Car;
        foreach (var v in Ctx.Cars.All)
        {
            if (v.Destroyed || v.Kind >= Vehicles.Vehicles.Count || Vehicles.Vehicles[v.Kind].Launcher == null || !_scudTrucks.TryGetValue(v.Id, out var truck)) continue;
            var box = ScudBox(v);
            var p = Ctx.Me.GlobalPosition;
            if (!ScudReloadRules.MayStart(truck.Msg.State, mine == v, box, p.X, p.Z, Scud.Reload)) continue;
            var carts = Ctx.Cars.All.Where(c => c.Trailer != null && !c.Destroyed && c.Kind < Vehicles.Vehicles.Count).Select(c => new ScudCartCandidate(c.Id, ScudBox(c), c.Trailer!.Loaded, true));
            if (ScudReloadRules.NearestCart(box, carts, (float)Scud.Reload.CartReachM) != null) return true;
        }
        return false;
    }

    BodyBox ScudBox(CarView v)
    {
        var def = Vehicles!.Vehicles[v.Kind];
        var p = v.Node.GlobalPosition;
        return new BodyBox(p.X, p.Z, def.HalfWidth, def.HalfLength, v.Node.Rotation.Y);
    }

    /// <summary>The prompt of a player on foot beside a truck with an empty rack and a loaded cart: the hold that loads it, then whatever the key does anyway (getting in). Null when there is no such offer or the player rides.</summary>
    string? ScudLoadPrompt(string? basePrompt)
    {
        if (Ctx.Driving || !ScudLoadOffer()) return null;
        var hint = Strings.F("scud.hint.load", InputBindings.Name("interact"));
        return basePrompt is { Length: > 0 } ? hint + "     " + basePrompt : hint;
    }

    // ---- the warning for everyone near the target ----

    /// <summary>
    /// A Scud in its last seconds, aimed within the warning radius of the player's body: the banner (seconds, where, how far), a siren once a second and the strike on the tactical map. The nearest
    /// impact in time wins when there are two.
    /// </summary>
    void StepScudWarning()
    {
        if (_scudWarning == null) return;
        ScudFlightInfo? soonest = null; var soonLeft = double.MaxValue;
        if (!Dead && Ctx.Me != null)
        {
            var body = Ctx.Me.GlobalPosition;
            foreach (var f in _scudFlights.Values)
            {
                var left = f.ArriveAt - Ctx.Now;
                if (left > Scud.Missile.WarnSeconds || left < 0 || left >= soonLeft) continue;
                if (new Vector2(body.X - f.Msg.Target.X, body.Z - f.Msg.Target.Z).Length() > Scud.Missile.WarnRadiusM) continue;
                soonest = f; soonLeft = left;
            }
        }
        if (soonest == null) { _scudWarning.Set(-1, "", 0); return; }
        var t = soonest.Msg.Target;
        var d = (int)MathF.Round(new Vector2(Ctx.Me!.GlobalPosition.X - t.X, Ctx.Me.GlobalPosition.Z - t.Z).Length());
        _scudWarning.Set((int)Math.Ceiling(soonLeft), GridRef(t.X, t.Z), d);
        if (!soonest.Marked)
        {
            soonest.Marked = true;
            Ctx.Hud?.Strikes.Add(new NVec2(t.X, t.Z), (float)Scud.Blast.LethalRadiusM, Ctx.Now + soonLeft + 4.0);
        }
        if (Ctx.Now >= _scudSirenAt) { _scudSirenAt = Ctx.Now + 1.0; if (Sfx.Enabled) Sfx.Play2D("scud.siren"); }
    }

    /// <summary>Blasts whose wave has reached the player, and the shake that rumbles on for a couple of seconds, fading.</summary>
    void StepScudFelt()
    {
        for (var i = _scudFelt.Count - 1; i >= 0; i--)
        {
            if (_scudFelt[i].At > Ctx.Now) continue;
            FeelScud(_scudFelt[i].Where);
            _scudFelt.RemoveAt(i);
        }
        var age = Ctx.Now - _scudShakeStart;
        if (age < _scudShakeSeconds && _scudShakeAmp > 0)
            Ctx.Shake.Raise(_scudShakeAmp * (1.0 - age / _scudShakeSeconds)); // the shake applies it this frame
    }
}
