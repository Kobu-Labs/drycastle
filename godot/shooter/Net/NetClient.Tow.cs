using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Trailers on the client (the rocket cart; docs/shooter-v2.md "Towing and the rocket cart"). A trailer is a vehicle in the snapshot like any other (<c>OnVehicleState</c>), whose record says which tractor
/// it is hitched to (<see cref="VehicleRecord.Tractor"/>) and whether it carries its load (<see cref="VehicleRecord.Loaded"/>). It is <b>drawn from its tractor as the tractor is drawn</b>, never from its own raw
/// record: the pose is the hitch of the drawn tractor and the articulation (<see cref="TowModel.OriginAt"/>), so it cannot lag or sway against the tractor whatever the tractor's own drawing does (interpolated
/// for a <see cref="CarModel"/> type such as the truck or the flak truck; predicted for its driver for a <see cref="DriveModel"/> type, whose trailer the driver's client then steps itself,
/// <see cref="DrivePrediction.AttachTrailer"/>). The articulation of a trailer drawn from the snapshots is the difference of the two interpolated yaws (the same tick, so the same tick's tractor); a
/// loose one is drawn at its record. On top comes the ground fit (follow the terrain under the axle: pitch, roll and height), the wheels rolling with the drawn speed and the missile shown or hidden.
/// </summary>
public partial class NetClient
{
    /// <summary>What the client keeps of a trailer besides the vehicle: its tractor and load as the record says, the model's moving nodes, and what is drawn.</summary>
    sealed class TrailerView
    {
        /// <summary>The vehicle it is hitched to (0: loose) and whether it carries its load, as the newest record says. <see cref="Seen"/>: a record has been applied (the first is not a coupling).</summary>
        public ushort TractorId;
        public bool Loaded = true, Seen;
        /// <summary>The model's nodes by name (null where it has none, and on a headless client): wheels roll, the missile is shown while loaded.</summary>
        public Node3D? WheelL, WheelR, Missile;
        public float RadiusL = 0.45f, RadiusR = 0.45f;
        /// <summary>The ground fit as drawn (smoothed), the drawn speed along the heading (from the drawn motion) and where it was drawn last frame.</summary>
        public float Pitch, Roll, Speed;
        /// <summary>How it lies on the ground as the latest record says (unsmoothed, the server's own fit: <see cref="TrailerProbe.Attitude"/>); its hit box and solid box turn by it (<see cref="HullTiltOf"/>).</summary>
        public TrailerAttitude.Result? Hull;
        public Vector3 LastPos;
        public bool HasLast;
    }

    /// <summary>Half the first vehicle id (<see cref="VehicleRecord.FirstId"/>): an interpolated tractor-id slot above it is hitched.</summary>
    const float TractorIdHalf = VehicleRecord.FirstId / 2f;

    /// <summary>How fast the drawn ground fit follows the ground, per second (rough ground does not shake the picture).</summary>
    const float TrailerFitResponse = 12f;

    /// <summary>The ground probe of every trailer drawn here (a ray for the height under a point), and each type's own world view for the driver's predicted trailer.</summary>
    TrailerProbe? _trailerGround;
    readonly Dictionary<(int Tractor, int Trailer), TowGeometry> _towGeo = new();

    /// <summary>The geometry of a tractor type pulling a trailer type (built once).</summary>
    TowGeometry GeometryOf(int tractorKind, int trailerKind)
    {
        if (_towGeo.TryGetValue((tractorKind, trailerKind), out var g)) return g;
        return _towGeo[(tractorKind, trailerKind)] = TowGeometry.Of(Vehicles!.Vehicles[tractorKind], Vehicles.Vehicles[trailerKind]);
    }

    /// <summary>A new trailer's view: its model's nodes (<c>wheel_l</c>, <c>wheel_r</c>, <c>missile</c>; a model without one just does not have that part).</summary>
    TrailerView MakeTrailerView(CarView v)
    {
        var t = new TrailerView();
        _trailerGround ??= TrailerProbe.Make(Vehicles!.Vehicles[v.Kind]); // a headless client fits the hull too
        if (_trailerGround.GetParent() == null) Owned.AddChild(_trailerGround);
        if (!ShotFx.Enabled) return t;
        t.WheelL = v.Node.FindChild("wheel_l", true, false) as Node3D;
        t.WheelR = v.Node.FindChild("wheel_r", true, false) as Node3D;
        t.Missile = v.Node.FindChild("missile", true, false) as Node3D;
        if (t.WheelL is { Position.Y: > 0.05f }) t.RadiusL = t.WheelL.Position.Y; // the wheel's centre is at its axle: its height is the radius (car-design section 3)
        if (t.WheelR is { Position.Y: > 0.05f }) t.RadiusR = t.WheelR.Position.Y;
        return t;
    }

    /// <summary>
    /// A trailer's record: its tractor and its load. A change of tractor after the first record is a coupling or an uncoupling: a clunk where the hitch is (through <see cref="PlayCarSound"/>, so it is
    /// heard from the cab as from outside, car-design section 9) and, for the tractor's driver, a toast. The missile follows the load bit.
    /// </summary>
    void OnTrailerRecord(CarView v, in EntityRecord rec)
    {
        var t = v.Trailer!;
        var tractor = VehicleRecord.Tractor(rec);
        var loaded = VehicleRecord.Loaded(rec);
        if (t.Seen && tractor != t.TractorId) TowChanged(v, t, tractor);
        if (!t.Seen || loaded != t.Loaded)
        {
            t.Loaded = loaded;
            if (t.Missile != null && GodotObject.IsInstanceValid(t.Missile)) t.Missile.Visible = loaded;
        }
        t.TractorId = tractor; t.Seen = true;
    }

    /// <summary>
    /// Fits a trailer's hull to the ground at the place its latest record gives, as the server does for its own (<see cref="ServerCar.PlaceTrailer"/>: the same <see cref="TrailerProbe.Attitude"/>, pulled by the
    /// hitch of the tractor's record or loose), so a shot, a blast or a step on foot meets the box where the server has it without the tilt travelling on the wire (#482).
    /// </summary>
    void RefreshHull(CarView v)
    {
        if (v.Trailer is not { } t || Vehicles == null || _trailerGround == null || !Vehicles.Vehicles[v.Kind].Tilts) return;
        var def = Vehicles.Vehicles[v.Kind];
        TowGeometry g; float? hitchY = null;
        if (t.TractorId != 0 && _cars.TryGetValue(t.TractorId, out var tractor) && Vehicles.Vehicles[tractor.Kind].Tow != null) { g = GeometryOf(tractor.Kind, v.Kind); hitchY = tractor.Pos.Y + g.HitchY; }
        else g = TowGeometry.ForTrailer(def);
        t.Hull = _trailerGround.Attitude(g, v.Pos.X, v.Pos.Z, v.Pos.Y, v.Yaw, hitchY);
    }

    void TowChanged(CarView trailer, TrailerView t, ushort now)
    {
        var tractorId = now != 0 ? now : t.TractorId;
        _cars.TryGetValue(tractorId, out var tractor);
        var at = (tractor ?? trailer).Node.GlobalPosition;
        PlayCarSound("tow.clunk", at, tractor);
        if (tractor != null && tractor.Driver == _myId && _driving && !Dead) UiShell.Instance?.Toast(Strings.T(now != 0 ? "toast.tow.hitched" : "toast.tow.unhitched"), ToastKind.Info);
    }

    /// <summary>The trailer hitched to <paramref name="tractor"/> as the records say, or null (a tractor pulls at most one).</summary>
    CarView? TrailerOf(ushort tractor)
    {
        if (tractor == 0) return null;
        foreach (var c in _cars.Values) if (c.Trailer is { } t && t.TractorId == tractor && !c.Destroyed) return c;
        return null;
    }

    /// <summary>
    /// The hint line for the driver who has a trailer on the hook: hold the interact key to unhitch (a tap still leaves the vehicle). Appended to the vehicle's own prompt; null when nothing is hitched.
    /// </summary>
    string? TowHint(CarView tractor) => TrailerOf(tractor.Id) == null ? null : Strings.F("hud.tow.unhitch", InputBindings.Name("interact"));

    /// <summary>
    /// Draws every trailer once the vehicles' own poses are set this frame (a trailer is drawn from its tractor's drawn pose, so the tractors go first): hitched, from the hitch of the drawn tractor and the
    /// articulation; loose, at its record; then the ground fit, the wheels and (headless excepted) the node.
    /// </summary>
    void RenderTrailers(double rt, float dt)
    {
        if (Vehicles == null) return;
        foreach (var v in _cars.Values)
        {
            if (v.Trailer is not { } t || v.Destroyed || v.Buf.At(rt) is not { } s) continue;
            var def = Vehicles.Vehicles[v.Kind];
            // hitched at the render tick (100 ms behind the newest record): the interpolated sample's id slot is the tractor's id on both sides of a hitch, and between a loose and a hitched sample it
            // passes the half way mark (ids start at 0x4000) where the hitch happens, so the trailer is pinned to its tractor from the sample that says so, not 100 ms early
            var tractorAt = s.Pitch > TractorIdHalf ? (ushort)MathF.Round(s.Pitch) : (ushort)0;
            var tractor = tractorAt != 0 && _cars.TryGetValue(tractorAt, out var found) && !found.Destroyed && Vehicles.Vehicles[found.Kind].Tow != null ? found : null;
            v.SnapYaw = s.Yaw;
            Vector3 pos; Vector3 rot;
            if (!ShotFx.Enabled)
            {
                // a headless client draws nothing: the node only stands where the record says (the shots and the solid read the record)
                v.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z); v.Node.Rotation = new Vector3(0, s.Yaw, 0);
                continue;
            }
            if (tractor != null) PoseHitched(v, t, def, tractor, s.Yaw, dt, out pos, out rot);
            else PoseLoose(t, def, s, dt, out pos, out rot);
            v.Node.GlobalPosition = pos; v.Node.Rotation = rot;
            Roll(v, t, pos, rot, dt);
            v.Smoke?.Near(Sfx.ListenerDistance(pos) < 90f);
        }
    }

    /// <summary>The articulation to draw: the driver's own predicted trailer's when this client predicts the tractor's trailer, else the snapshots' (the trailer's yaw less the tractor's, both as interpolated at the render tick).</summary>
    float PhiOf(CarView tractor, float trailerSnapYaw) =>
        _drive != null && _drive.CarId == tractor.Id && _drive.Predict.HasTrailer ? _drive.DrawnPhi : FlightMath.Wrap(trailerSnapYaw - tractor.SnapYaw);

    void PoseHitched(CarView v, TrailerView t, VehicleDef def, CarView tractor, float trailerSnapYaw, float dt, out Vector3 pos, out Vector3 rot)
    {
        var g = GeometryOf(tractor.Kind, v.Kind);
        var phi = Math.Clamp(PhiOf(tractor, trailerSnapYaw), -g.Limit, g.Limit);
        var yaw = FlightMath.Wrap(tractor.Node.Rotation.Y + phi);
        var hitch = tractor.Node.GlobalTransform * new Vector3(g.HitchX, g.HitchY, g.HitchZ); // on the tractor as drawn: it leans with its slope
        var flat = TowModel.OriginAt(g, NV(hitch), yaw);
        var fit = _trailerGround!.Attitude(g, flat.X, flat.Z, flat.Y, yaw, hitch.Y); // the same fit as the server's hull (#482)
        FitSmooth(t, fit.Pitch, fit.Roll, dt);
        rot = new Vector3(t.Pitch, yaw, t.Roll);
        pos = hitch - Basis.FromEuler(rot) * new Vector3(0, g.EyeY, g.EyeZ); // the eye is on the hitch, exactly
    }

    void PoseLoose(TrailerView t, VehicleDef def, Interpolator.Sample s, float dt, out Vector3 pos, out Vector3 rot)
    {
        var g = TowGeometry.ForTrailer(def);
        var fit = _trailerGround!.Attitude(g, s.Pos.X, s.Pos.Z, s.Pos.Y, s.Yaw, null);
        FitSmooth(t, fit.Pitch, fit.Roll, dt);
        rot = new Vector3(t.Pitch, s.Yaw, t.Roll);
        pos = new Vector3(fit.Axle.X, fit.Axle.Y, fit.Axle.Z) - Basis.FromEuler(rot) * new Vector3(0, 0, g.EyeZ + g.Drawbar);
    }

    static void FitSmooth(TrailerView t, float pitch, float roll, float dt)
    {
        var take = 1f - MathF.Exp(-TrailerFitResponse * dt);
        t.Pitch += (pitch - t.Pitch) * take;
        t.Roll += (Math.Clamp(roll, -0.45f, 0.45f) - t.Roll) * take;
    }

    /// <summary>The wheels roll with the drawn speed (from how far the drawn trailer moved along its heading this frame): about the parent's X, whichever way the model's own wheel is turned.</summary>
    void Roll(CarView v, TrailerView t, Vector3 pos, Vector3 rot, float dt)
    {
        if (t.HasLast && dt > 1e-4f)
        {
            var fwd = -Basis.FromEuler(rot).Z;
            var moved = pos - t.LastPos;
            var speed = moved.Length() > 5f ? 0f : moved.Dot(fwd) / dt; // a jump is a return to its place, not a speed
            t.Speed += (Math.Clamp(speed, -40f, 40f) - t.Speed) * (1f - MathF.Exp(-dt / 0.08f));
            Spin(t.WheelL, -t.Speed / t.RadiusL * dt);
            Spin(t.WheelR, -t.Speed / t.RadiusR * dt);
        }
        t.LastPos = pos; t.HasLast = true;
    }

    static void Spin(Node3D? wheel, float angle)
    {
        if (wheel == null || !GodotObject.IsInstanceValid(wheel)) return;
        wheel.Basis = new Basis(Vector3.Right, angle) * wheel.Basis;
    }
}
