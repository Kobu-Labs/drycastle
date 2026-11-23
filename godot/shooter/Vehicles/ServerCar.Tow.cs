using Godot;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Vehicles;

/// <summary>
/// Towing on the server's vehicle (docs/shooter-v2.md "Towing and the rocket cart"). A trailer (a type with a <c>trailer</c> block) is a vehicle like any other here: in <c>_cars</c>, with
/// an id, a record, hit points, a hit box and a solid; it has no seats and no driver. What moves it is its <see cref="TowLink"/> (the model in ShooterCore), not the car model or the
/// physics body: <see cref="PlaceTrailer"/> puts the node where the link's body says, on the ground under it. A tractor (a type with a <c>tow</c> block) steps the trailer hitched to it from
/// its own step (<see cref="StepTowed"/>), so the trailer follows the tractor whoever drives it (a client's input, hands off, parked).
/// </summary>
public partial class ServerCar
{
    /// <summary>A trailer: no seats, pulled by a tractor, moved by <see cref="Tow"/>.</summary>
    public bool IsTrailer => Def.Trailer != null;

    /// <summary>A trailer's link: the machine (loose, hitched, released) and the body the model steps. Null for every other type.</summary>
    public TowLink? Tow { get; private set; }

    /// <summary>A trailer's load (the rocket cart's missile): server-owned, replicated in the record's health byte. Null for every other type.</summary>
    public CartCargo? Cargo { get; private set; }

    /// <summary>A trailer's view of the world for its own step (the world's solids under its box, the ground under it); null for every other type.</summary>
    public TrailerProbe? TrailerWorld { get; private set; }

    /// <summary>How the trailer lies on the ground as of its last placing (<see cref="TrailerProbe.Attitude"/>, the same fit the clients draw it by); null before the first placing and after a throw.</summary>
    public TrailerAttitude.Result? TrailerTilt { get; private set; }

    /// <summary>The tractor this trailer is hitched to; null when it is loose.</summary>
    public ServerCar? Tractor { get; private set; }

    /// <summary>The trailer hitched to this tractor (a tractor pulls at most one); null when none.</summary>
    public ServerCar? Towing { get; private set; }

    /// <summary>The numbers of the pair while hitched; the trailer's own (<see cref="TowGeometry.ForTrailer"/>) otherwise.</summary>
    public TowGeometry TowGeo;

    /// <summary>The tractor's driver's interact key while a trailer is on the hook: a tap leaves, a hold unhitches (<see cref="UnhitchKey"/>).</summary>
    public readonly UnhitchKey UnhitchKey = new();

    /// <summary>Removed from the world for good (<see cref="Retire"/>): destroyed with no return, not in the snapshot, skipped by every loop.</summary>
    public bool Retired { get; private set; }

    /// <summary>Builds what a trailer needs; called once from <c>_Ready</c> before the first <see cref="Reset"/>.</summary>
    void ReadyTrailer()
    {
        Cargo = new CartCargo();
        Tow = new TowLink(OriginPos.ToNumerics(), OriginYaw);
        TowGeo = TowGeometry.ForTrailer(Def);
        TrailerWorld = TrailerProbe.Make(Def);
        AddChild(TrailerWorld);
    }

    /// <summary>
    /// A trailer back at its origin, loose: any hitch is dropped without a lockout (it was not the driver's doing). Called from <see cref="Reset"/>. A return after destruction (<paramref name="repair"/>) loads it
    /// again; a fall out of the world does not: a cart that was emptied (a Scud truck took its missile) stays empty.
    /// </summary>
    void ResetTrailer(bool repair)
    {
        DropLinks();
        if (repair) Cargo!.Reset();
        Life.SetMaxHp(Cargo.MaxHp(Def));
        Tow = new TowLink(OriginPos.ToNumerics(), OriginYaw);
        TowGeo = TowGeometry.ForTrailer(Def);
        TrailerTilt = null;
    }

    /// <summary>The vehicle's own end of a hitch goes (a reset, a blast): a tractor lets its trailer go (with the lockout), a trailer lets go of its tractor.</summary>
    void DropLinks()
    {
        Towing?.Unhitch();
        Unhitch();
    }

    /// <summary>The tractor's step is told what the trailer on it does (its load and the jackknife block); none when nothing is hitched.</summary>
    public TowEffect TowEffectNow()
    {
        if (Towing is not { Tow: { } link } trailer) return TowEffect.None;
        var e = link.Effect(Def, trailer.Def, trailer.TowGeo, trailer.Cargo?.Loaded ?? true);
        var now = Time.GetTicksMsec() / 1000.0;
        if (e.Block.Active && now - _blockLogS >= 1) { _blockLogS = now; GD.Print($"server: {Def.Id} {Id} jackknife block active, trailer {trailer.Id} at its fold limit (phi {link.Body.Phi * 180f / MathF.PI:F0} deg)"); }
        return e;
    }
    double _blockLogS = -10;

    /// <summary>What the tractor did this tick, as the trailer needs it: the drive state's, or the car state with the physics body's place.</summary>
    public TractorMotion TowMotion()
    {
        if (Drives) return TractorMotion.From(Drive, Def);
        var p = GlobalPosition;
        return TractorMotion.From(new NVec3(p.X, p.Y, p.Z), State);
    }

    /// <summary>
    /// The hitched trailer's tick, after this tractor's own step: false when the world blocks the trailer's new place (the trailer stays and the caller holds the tractor, as a ledge
    /// too high stops it). The trailer's node follows its body.
    /// </summary>
    public bool StepTowed(float dt)
    {
        var trailer = Towing!;
        var ok = trailer.Tow!.StepHitched(trailer.TowGeo, TowMotion(), dt, trailer.TrailerWorld);
        trailer.PlaceTrailer();
        return ok;
    }

    /// <summary>
    /// Latches this loose trailer to <paramref name="tractor"/> when the auto-latch rule says so (<see cref="TowRules.CheckLatch"/>: the tractor reverses its hitch onto the eye, close and lined up) and the
    /// tractor has no trailer yet. True when it coupled.
    /// </summary>
    public bool TryLatchTo(ServerCar tractor)
    {
        if (Tow == null || Tractor != null || tractor.Towing != null || Destroyed || tractor.Destroyed || !TowGeometry.CanTow(tractor.Def, Def)) return false;
        var g = TowGeometry.Of(tractor.Def, Def);
        if (!Tow.TryLatch(g, tractor.TowMotion())) return false;
        TowGeo = g; Tractor = tractor; tractor.Towing = this;
        PlaceTrailer();
        Wake(); tractor.Wake();
        return true;
    }

    /// <summary>
    /// The trailer is let go of its tractor (the driver's unhitch, a destroyed tractor or trailer, a reset): the lockout starts, it rolls on at the speed it has. False when it was not hitched.
    /// </summary>
    public bool Unhitch()
    {
        if (Tractor is not { } tractor || Tow == null) return false;
        tractor.Towing = null; Tractor = null;
        Tow.Unhitch(TowGeo);
        TowGeo = TowGeometry.ForTrailer(Def);
        Wake(); tractor.Wake();
        return true;
    }

    /// <summary>A trailer nobody pulls for one tick: the lockout runs, a released one rolls to a stop (stopping dead at a wall), and the node follows.</summary>
    public void StepFree(float dt)
    {
        Tow!.StepFree(TowGeo, dt, TrailerWorld);
        PlaceTrailer();
        RestCount = ParkedRules.NextRestCount(RestCount, Tow.Body.Speed == 0f && !Tow.Hitched);
        Asleep = ParkedRules.Sleeps(RestCount);
    }

    /// <summary>
    /// The node and the planar state the other rules read (the hit box, the run-over sweep, the snapshot) follow the link's body: on the ground under the trailer, turned to its heading. The body's
    /// height is the ground's too, so the next step's world query starts from it.
    /// </summary>
    public void PlaceTrailer()
    {
        var b = Tow!.Body;
        var gy = TrailerWorld!.GroundY(b.Position.X, b.Position.Z, b.Position.Y);
        Tow.Body.Position = new NVec3(b.Position.X, gy, b.Position.Z);
        GlobalPosition = new Vector3(b.Position.X, gy + 0.02f, b.Position.Z);
        Rotation = new Vector3(0, b.Yaw, 0);
        State.Yaw = b.Yaw; State.Speed = b.Speed; State.YawRate = 0;
        Velocity = Vector3.Zero;
        if (Def.Tilts) TrailerTilt = TrailerWorld.Attitude(TowGeo, b.Position.X, b.Position.Z, gy, b.Yaw, Tractor is { } t ? t.GlobalPosition.Y + TowGeo.HitchY : null); // the node stays upright; the hull turns (HullTilt)
    }

    /// <summary>The body takes the node's place (a trailer the Bigfoot threw and set down): it stands there, still.</summary>
    public void SyncTrailerFromNode()
    {
        var p = GlobalPosition;
        Tow!.Body = TrailerBody.At(new NVec3(p.X, p.Y, p.Z), State.Yaw);
        TrailerTilt = null; // set down somewhere else: upright until the next placing fits it
    }

    /// <summary>Puts the load on or takes it off: its hit points follow (<see cref="CartCargo.MaxHp"/>), keeping their share. True when that changed it.</summary>
    public bool SetLoaded(bool loaded)
    {
        if (Cargo == null || !Cargo.Set(loaded)) return false;
        Life.SetMaxHp(Cargo.MaxHp(Def));
        Wake();
        return true;
    }

    /// <summary>
    /// Takes the vehicle out of the world for good, silently: unhitched, destroyed with no return time, gone from the snapshot (clients drop it) and from every loop. The slot stays in the vehicle
    /// list (ids are the list's indices) and is never reused. A round reset does not bring it back.
    /// </summary>
    public void Retire()
    {
        if (Retired) return;
        DropLinks();
        Life.Destroy(double.PositiveInfinity);
        Retired = true;
        CollisionLayer = 0;
        Velocity = Vector3.Zero;
        PlaceSolid(); // its solid box goes with it
    }

    /// <summary>A trailer as one side of a bump (<see cref="ApplyBump"/>): hitched it is pinned to its tractor and does not move; loose it is shoved and takes the speed along its heading.</summary>
    void BumpTrailer(NVec3 move, NVec3 deltaV)
    {
        Wake();
        if (Tractor != null) return; // the tractor's pull holds it: the other vehicle takes the whole bump's push back next tick
        var b = Tow!.Body;
        b.Position += new NVec3(move.X, 0, move.Z);
        var fwd = new NVec3(-MathF.Sin(b.Yaw), 0, -MathF.Cos(b.Yaw));
        b.Speed += deltaV.X * fwd.X + deltaV.Z * fwd.Z;
        Tow.Body = b;
        PlaceTrailer();
    }
}

static class NumericsExtensions
{
    public static NVec3 ToNumerics(this Vector3 v) => new(v.X, v.Y, v.Z);
}
