using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>Run-over damage (ticket #77): above the minimum speed, a hit player takes damage per m/s over it, capped.</summary>
public static class RunOver
{
    /// <summary>
    /// The speed (m/s) from which a vehicle's contact moves a body at all. Between it and <see cref="VehiclesFile.RunOverMinSpeed"/> the body is pushed (a player) or knocked over (an animal)
    /// and not hurt, so a slow vehicle does not drive through a body that stands in it.
    /// </summary>
    public const float ContactMinSpeed = 1.5f;

    /// <summary>Whether a vehicle at <paramref name="speed"/> moves the bodies it touches (<see cref="ContactMinSpeed"/>).</summary>
    public static bool Contacts(float speed) => MathF.Abs(speed) >= ContactMinSpeed;

    /// <summary>
    /// Seconds a player a vehicle threw alive is treated as thrown by the server (#411), as one the Bigfoot launched: no shooting while in the air, no grenade, and the landing
    /// of the throw costs no fall damage.
    /// </summary>
    public const float ThrownGraceSeconds = 3f;

    /// <param name="massScale">A type's <see cref="VehiclesFile.RunOverScale"/> (1 for the v1 numbers).</param>
    public static float Damage(VehiclesFile f, float speed, float massScale = 1f)
    {
        var over = MathF.Abs(speed) - f.RunOverMinSpeed;
        return over <= 0 ? 0 : MathF.Min(f.RunOverMaxDamage, over * f.RunOverDamagePerSpeed * massScale);
    }
}

/// <summary>
/// Where a vehicle hits a body: the car's oriented footprint against the body's footprint (a circle, or a box for an animal; see <see cref="RunOverFootprint"/>), swept along the path the car drove this tick so
/// a fast or small vehicle cannot step over a body between two ticks. Every victim shares it.
/// </summary>
public static class CarFootprint
{
    /// <summary>The path is sampled at most this far apart (metres); a body circle is wider than this, so no gap is left.</summary>
    public const float StepMetres = 0.5f;
    /// <summary>A longer jump than this in one tick is a teleport (reset, respawn), not driving: only the end point counts.</summary>
    public const float MaxSweepMetres = 20f;

    /// <summary>The car at <paramref name="p"/> with <paramref name="yaw"/> touches a body standing at <paramref name="body"/> (feet) with <paramref name="radius"/>: the footprint's nearest point within the radius, the feet between 0.6 m below and 2 m above the car's origin.</summary>
    public static bool Overlaps(Vector3 p, float yaw, float halfWidth, float halfLength, Vector3 body, float radius)
    {
        var d = body - p;
        if (d.Y < -0.6f || d.Y > 2f) return false;
        var lx = d.X * MathF.Cos(yaw) - d.Z * MathF.Sin(yaw);
        var lz = -d.X * MathF.Sin(yaw) - d.Z * MathF.Cos(yaw);
        var dx = MathF.Max(MathF.Abs(lx) - halfWidth, 0);
        var dz = MathF.Max(MathF.Abs(lz) - halfLength, 0);
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>The same test along the path from <paramref name="from"/> to <paramref name="to"/> (the car's origin at the start and end of the tick; the end yaw is used all along).</summary>
    public static bool Hits(Vector3 from, Vector3 to, float yaw, float halfWidth, float halfLength, Vector3 body, float radius)
    {
        var steps = Steps(from, to, out var jump);
        if (jump) return Overlaps(to, yaw, halfWidth, halfLength, body, radius);
        for (var i = 0; i <= steps; i++)
            if (Overlaps(Vector3.Lerp(from, to, (float)i / steps), yaw, halfWidth, halfLength, body, radius)) return true;
        return false;
    }

    /// <summary>The car at <paramref name="p"/> with <paramref name="yaw"/> touches a standing box (an animal's footprint, its feet at <paramref name="feetY"/>): the two rectangles overlap, the feet within the car's height band as for a circle.</summary>
    public static bool Overlaps(Vector3 p, float yaw, float halfWidth, float halfLength, in BodyBox body, float feetY)
    {
        var dy = feetY - p.Y;
        if (dy < -0.6f || dy > 2f) return false;
        return new BodyBox(p.X, p.Z, halfWidth, halfLength, yaw).Overlaps(body);
    }

    /// <summary>The box test along the path of the tick, as for a circle.</summary>
    public static bool Hits(Vector3 from, Vector3 to, float yaw, float halfWidth, float halfLength, in BodyBox body, float feetY)
    {
        var steps = Steps(from, to, out var jump);
        if (jump) return Overlaps(to, yaw, halfWidth, halfLength, body, feetY);
        for (var i = 0; i <= steps; i++)
            if (Overlaps(Vector3.Lerp(from, to, (float)i / steps), yaw, halfWidth, halfLength, body, feetY)) return true;
        return false;
    }

    /// <summary>How many samples cover the path; <paramref name="jump"/> when it is a teleport (only the end point counts).</summary>
    static int Steps(Vector3 from, Vector3 to, out bool jump)
    {
        var travel = Vector3.Distance(from, to);
        jump = travel > MaxSweepMetres;
        return Math.Max(1, (int)MathF.Ceiling(travel / StepMetres));
    }
}

/// <summary>Hidden vehicle HP (ticket #103): smoke stage from the remaining fraction, blast damage falloff and the box distance of a blast to a vehicle.</summary>
public static class VehicleDamage
{
    /// <summary>0 = fine, 1 = light smoke, 2 = dark smoke, 3 = smoke and sparks; at 0 HP the vehicle is destroyed (not a stage).</summary>
    public static int Stage(float hp, float maxHp, IReadOnlyList<float> fractions)
    {
        var f = hp / MathF.Max(1e-4f, maxHp);
        var s = 0;
        for (var i = 0; i < fractions.Count; i++) if (f <= fractions[i]) s = i + 1;
        return s;
    }

    /// <summary>Blast damage at surface distance <paramref name="distance"/>: full damage inside the full-damage radius, linear to the edge damage at the radius, none beyond.</summary>
    public static float BlastDamage(VehicleExplosionDef e, float distance)
    {
        var s = e.Splash;
        if (distance >= s.Radius) return 0;
        if (distance <= s.FullDamageRadius) return e.Damage;
        var t = (distance - s.FullDamageRadius) / MathF.Max(1e-4f, s.Radius - s.FullDamageRadius);
        return MathF.Max(1f, MathF.Round(e.Damage + (s.EdgeDamage - e.Damage) * t));
    }

    /// <summary>Distance from a blast centre to the surface of an upright box (the vehicle's <see cref="VehicleBox"/>: centre on the ground, yaw as in <see cref="CarState"/>, <paramref name="height"/> tall).</summary>
    public static float DistanceToBox(Vector3 blast, Vector3 centre, float yaw, float halfWidth, float halfLength, float height)
    {
        var d = blast - centre;
        var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var lx = d.X * right.X + d.Z * right.Z; var lz = d.X * fwd.X + d.Z * fwd.Z;
        var dx = MathF.Max(0, MathF.Abs(lx) - halfWidth);
        var dz = MathF.Max(0, MathF.Abs(lz) - halfLength);
        var dy = d.Y < 0 ? -d.Y : MathF.Max(0, d.Y - height);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>Blast distance to a vehicle's hull as its type has it (<see cref="VehicleDef.Hull"/> boxes, tilted by the attitude; else the one upright box).</summary>
public static class HullDistance
{
    public static float Of(Vector3 blast, Vector3 feet, float yaw, float pitch, float roll, VehicleDef def) =>
        def.Tilts ? VehicleBox.DistanceToHull(FlightMath.Unrotate(blast - feet, yaw, pitch, roll), def.HullBoxes)
                  : VehicleDamage.DistanceToBox(blast, feet, yaw, def.HalfWidth, def.HalfLength, def.Height);
}

/// <summary>
/// Lifetime of a destroyed vehicle's blast visuals (client, <c>VehicleFx.Explode</c>), in seconds from the blast. The burning spot's
/// fire and black smoke column stop emitting after <see cref="EmitSeconds"/>; the last column puff dies <see cref="ColumnLife"/> later;
/// the whole effect node is freed at <see cref="FreeAfter"/>. Nothing of the blast outlives that, so no smoke stands where the car was.
/// </summary>
public static class VehicleBlastFx
{
    public const float EmitSeconds = 1.2f, ColumnLife = 2.0f, FreeAfter = 3.5f;
}

/// <summary>
/// Seat state of one vehicle: one occupant per seat, seat 0 the driver (player ids start at 1, 0 = empty). The seat framework for
/// every type: a type's seats come from its <see cref="VehicleDef.Seats"/> list, and <see cref="NearestFreeSeat"/> picks the seat for
/// someone who presses enter. Pure logic; the server places the occupant at <see cref="SeatPosition"/>.
/// </summary>
public sealed class VehicleSeat
{
    public const int MaxSeats = 5;
    readonly ushort[] _occupant;

    public VehicleSeat(int seats = 1)
    {
        if (seats < 0 || seats > MaxSeats) throw new ArgumentOutOfRangeException(nameof(seats)); // 0 seats: a trailer, which nobody can board
        _occupant = new ushort[seats];
        _blocked = new bool[seats];
    }

    /// <summary>Seats a dead rider's corpse still sits in (ADR 0026): nobody can take or switch to one. A blocked seat has no occupant, so it does not keep the vehicle occupied.</summary>
    readonly bool[] _blocked;

    /// <summary>
    /// The players this vehicle is reserved for (the Cartel van of a Heist: the Job's players); null when anybody may board. Who already sits in it keeps the seat when the list
    /// changes (the owner of the reservation takes a rider out if it must). Not cleared by <see cref="Clear()"/>: the vehicle keeps it through a round's seat clearing.
    /// </summary>
    HashSet<ushort>? _reserved;

    /// <summary>Only <paramref name="riders"/> may board from now on (<see cref="Admits"/>); an empty list lets nobody in.</summary>
    public void Reserve(IEnumerable<ushort> riders) => _reserved = new HashSet<ushort>(riders.Where(p => p != 0));

    /// <summary>Anybody may board again.</summary>
    public void Unreserve() => _reserved = null;

    /// <summary>Whether the vehicle is reserved for some players (<see cref="Reserve"/>).</summary>
    public bool IsReserved => _reserved != null;

    /// <summary>Whether <paramref name="player"/> may take a seat: anybody unless the vehicle is reserved, then only the reserved riders.</summary>
    public bool Admits(ushort player) => player != 0 && (_reserved == null || _reserved.Contains(player));

    /// <summary>What a player who is not admitted is told (the interact key on a reserved vehicle).</summary>
    public const string RefusedText = "This vehicle is reserved for its crew.";

    public bool IsBlocked(int seat) => _blocked[seat];
    public void Block(int seat) => _blocked[seat] = true;
    public void Unblock(int seat) => _blocked[seat] = false;

    public int Count => _occupant.Length;
    /// <summary>Player id of the driver (seat 0), 0 when empty (and for a type with no seats).</summary>
    public ushort Driver => _occupant.Length > 0 ? _occupant[0] : (ushort)0;
    /// <summary>Player id in <paramref name="seat"/>, 0 when empty.</summary>
    public ushort Occupant(int seat) => _occupant[seat];
    public bool Occupied => _occupant.Any(p => p != 0);
    public bool HasFree { get { for (var i = 0; i < _occupant.Length; i++) if (IsFree(i)) return true; return false; } }
    public bool IsFree(int seat) => _occupant[seat] == 0 && !_blocked[seat];
    /// <summary>Nobody sits in <paramref name="seat"/>: free, or held by a dead rider's corpse, which a boarder kicks out (#330).</summary>
    public bool IsBoardable(int seat) => _occupant[seat] == 0;
    /// <summary>A corpse (and no player) holds <paramref name="seat"/>.</summary>
    public bool HoldsCorpse(int seat) => _occupant[seat] == 0 && _blocked[seat];
    public bool HasBoardable { get { for (var i = 0; i < _occupant.Length; i++) if (IsBoardable(i)) return true; return false; } }

    /// <summary>The seat <paramref name="player"/> sits in, or -1.</summary>
    public int SeatOf(ushort player)
    {
        if (player == 0) return -1;
        for (var i = 0; i < _occupant.Length; i++) if (_occupant[i] == player) return i;
        return -1;
    }

    /// <summary>Takes <paramref name="seat"/> (-1: the first free one). False for player 0, a player the vehicle is not reserved for (<see cref="Admits"/>), a taken seat, a player already seated or no free seat.</summary>
    public bool TryEnter(ushort player, int seat = -1)
    {
        if (!Admits(player) || SeatOf(player) >= 0) return false;
        if (seat < 0) { for (var i = 0; i < _occupant.Length && seat < 0; i++) if (IsFree(i)) seat = i; }
        if (seat < 0 || seat >= _occupant.Length || !IsFree(seat)) return false;
        _occupant[seat] = player;
        return true;
    }

    /// <summary>
    /// Boards <paramref name="seat"/> as <see cref="TryEnter"/> does, and also a seat a corpse holds (#330): <paramref name="kicked"/> is then true, the seat is
    /// unblocked and taken at once; the caller flings the corpse out. A taken seat or a refused player changes nothing.
    /// </summary>
    public bool TryBoard(ushort player, int seat, out bool kicked)
    {
        kicked = false;
        if (!Admits(player) || SeatOf(player) >= 0 || seat < 0 || seat >= _occupant.Length || !IsBoardable(seat)) return false;
        kicked = _blocked[seat];
        _blocked[seat] = false;
        _occupant[seat] = player;
        return true;
    }

    /// <summary>The player leaves (E, death, disconnect, round restart). False if <paramref name="player"/> is not seated here.</summary>
    public bool Exit(ushort player)
    {
        var s = SeatOf(player);
        if (s < 0) return false;
        _occupant[s] = 0;
        return true;
    }

    /// <summary>
    /// The seated <paramref name="player"/> moves to <paramref name="seat"/> without leaving the vehicle (the seat switch): the old seat is freed and the
    /// new one taken in one step, so the vehicle is never empty in between. False (nothing changes) when the player is not seated here, the seat does not
    /// exist, it is their own seat or another player has it.
    /// </summary>
    public bool TrySwitch(ushort player, int seat)
    {
        var from = SeatOf(player);
        if (from < 0 || seat < 0 || seat >= _occupant.Length || seat == from || !IsFree(seat)) return false;
        _occupant[from] = 0;
        _occupant[seat] = player;
        return true;
    }

    public void Clear(int seat) => _occupant[seat] = 0;
    public void Clear() { Array.Clear(_occupant); Array.Clear(_blocked); }

    /// <summary>World position of a seat: <paramref name="offset"/> in the vehicle's frame (forward -Z, yaw as in <see cref="CarState"/>) from the car's ground centre.</summary>
    public static Vector3 SeatPosition(Vector3 carPos, float yaw, float[] offset)
    {
        var c = MathF.Cos(yaw); var s = MathF.Sin(yaw);
        return new Vector3(carPos.X + offset[0] * c + offset[2] * s, carPos.Y + offset[1], carPos.Z - offset[0] * s + offset[2] * c);
    }

    /// <summary>
    /// The free seat of <paramref name="def"/> nearest to <paramref name="from"/> (horizontal distance, metres), within <paramref name="range"/>;
    /// -1 if none. An empty vehicle always gives the driver's seat; ties go to the lower seat index. With <paramref name="kickCorpses"/> a seat held by a corpse counts as free (#330).
    /// </summary>
    public int NearestFreeSeat(VehicleDef def, Vector3 carPos, float yaw, Vector3 from, float range, bool kickCorpses = false)
    {
        if (!Occupied && _occupant.Length > 0 && (kickCorpses || !_blocked[0])) return 0; // an empty vehicle: whoever gets in first is the driver, wherever they stand (a corpse there is kicked out with kickCorpses, else it blocks)
        var best = -1; var bd = range;
        for (var i = 0; i < _occupant.Length && i < def.Seats.Count; i++)
        {
            if (kickCorpses ? !IsBoardable(i) : !IsFree(i)) continue;
            var p = SeatPosition(carPos, yaw, def.Seats[i].Offset);
            var d = MathF.Sqrt((p.X - from.X) * (p.X - from.X) + (p.Z - from.Z) * (p.Z - from.Z));
            if (d <= bd && (best < 0 || d < bd)) { best = i; bd = d; }
        }
        return best;
    }
}

/// <summary>Whether a vehicle gun can fire. The machine is <see cref="VehicleGun.Spec"/>.</summary>
public enum VehicleGunState : byte { Ready, Reloading }

/// <summary>A vehicle gun's magazine, reserve and reload. Hitscan rounds are resolved by the server; this only says when a round leaves the barrel.</summary>
public sealed class VehicleGun
{
    /// <summary>A reload starts on request (not when full, empty of reserve or already reloading) or when a trigger pull finds the magazine empty, and ends on the first <see cref="TryFire"/> after its time.</summary>
    public static readonly StateMachineSpec<VehicleGunState> Spec = StateMachineSpec<VehicleGunState>.Define("Vehicle.Gun", "A vehicle gun: ready to fire, or reloading.", VehicleGunState.Ready)
        .Allow(VehicleGunState.Ready, VehicleGunState.Reloading)
        .Allow(VehicleGunState.Reloading, VehicleGunState.Ready)
        .Note(VehicleGunState.Ready, "Fires one round per FireInterval while the trigger is held and the magazine has one.")
        .Note(VehicleGunState.Reloading, "No rounds until the reload time is over; the magazine is topped up from the reserve when it ends. The end is noticed by the next trigger update, not by a timer.")
        .Build();

    readonly StateMachine<VehicleGunState> _state = new(Spec);
    public VehicleGunDef Def { get; }
    public int Magazine { get; private set; }
    public int Reserve { get; private set; }
    double _nextShot;
    /// <summary>When the running reload ends (the server's absolute clock); only meaningful while reloading.</summary>
    double _reloadDone;

    public VehicleGun(VehicleGunDef def)
    {
        Def = def;
        Magazine = def.Magazine;
        Reserve = def.Reserve;
    }

    public bool Reloading => _state.Is(VehicleGunState.Reloading);

    /// <summary>Seconds of reload left at <paramref name="now"/> (0 when not reloading).</summary>
    public float ReloadRemaining(double now) => Reloading ? (float)Math.Max(0, _reloadDone - now) : 0f;

    /// <summary>Starts a reload: not when full, empty of reserve or already reloading.</summary>
    public bool TryReload(double now)
    {
        if (Reloading || Magazine == Def.Magazine || Reserve == 0) return false;
        _reloadDone = now + Def.ReloadTime;
        _state.Enter(VehicleGunState.Reloading);
        return true;
    }

    /// <summary>
    /// True when a round fires this tick: trigger held, not reloading, the fire interval has passed and the magazine has a round.
    /// An empty magazine starts a reload instead.
    /// </summary>
    public bool TryFire(double now, bool trigger)
    {
        if (Reloading && now >= _reloadDone)
        {
            var take = Math.Min(Def.Magazine - Magazine, Reserve);
            Magazine += take; Reserve -= take;
            _state.Enter(VehicleGunState.Ready);
        }
        if (!trigger || Reloading || now < _nextShot) return false;
        if (Magazine == 0) { TryReload(now); return false; }
        Magazine--;
        _nextShot = now + Def.FireInterval;
        return true;
    }
}

public static class VehicleColours
{
    /// <summary>Default colour variants (sRGB) of a type with no skins, paints or tint: how many there are (the colour index of the record). No model is tinted with them any more.</summary>
    public static readonly (float R, float G, float B)[] Palette =
    {
        (0.90f, 0.20f, 0.18f), (0.20f, 0.45f, 0.90f), (0.95f, 0.80f, 0.15f), (0.25f, 0.75f, 0.35f),
        (0.95f, 0.50f, 0.12f), (0.95f, 0.95f, 0.95f), (0.55f, 0.30f, 0.75f), (0.12f, 0.12f, 0.14f),
    };

    /// <summary>A random colour index below <paramref name="variants"/> (default: the car palette), preferring ones not in <paramref name="taken"/> (all taken: any).</summary>
    public static byte Pick(Random rng, IEnumerable<byte> taken, int variants = 0)
    {
        var n = variants > 0 ? variants : Palette.Length;
        var t = new HashSet<byte>(taken);
        var free = Enumerable.Range(0, n).Where(i => !t.Contains((byte)i)).ToList();
        return free.Count > 0 ? (byte)free[rng.Next(free.Count)] : (byte)rng.Next(n);
    }
}

/// <summary>How a vehicle travels in a snapshot entity record: yaw as usual, driver id in the (otherwise unused) pitch field, colour index (low 4 bits) and type index into vehicles.json (high 4 bits) in the weapon byte.</summary>
public static class VehicleRecord
{
    public const ushort FirstId = 0x4000;

    /// <summary>Health byte bit 0x80: headlights on (one bit, see <see cref="Headlights"/>); bit 0x40 on a driven vehicle: its brakes are on (<see cref="BrakeBit"/>, protocol 71). Bits 0..1 are the damage stage, bits 2..5 the wheels the Thieves have taken (<see cref="WheelsMask"/>), bit 6 is a trailer's loaded flag (<see cref="LoadedBit"/>, <see cref="MakeTrailer"/>).</summary>
    public const byte LightsBit = 0x80, BrakeBit = 0x40, StageMask = 0x03, WheelsMask = 0x3C;
    const int WheelsShift = 2;

    /// <summary>The Health byte of a vehicle record carries the damage stage (0..3), the wheels taken (bit per wheel: front left, front right, back left, back right) and the headlights bit; a destroyed vehicle is State Dead.</summary>
    public static EntityRecord Make(ushort id, Vector3 pos, float yaw, ushort driver, byte colourAndKind, int stage = 0, bool destroyed = false, bool lights = false, byte wheelsOff = 0, bool braking = false)
    {
        var health = (stage & StageMask) | ((wheelsOff & 0xF) << WheelsShift) | (lights ? LightsBit : 0) | (braking ? BrakeBit : 0);
        var r = EntityRecord.Make(id, EntityType.Vehicle, pos, yaw, 0, health, destroyed ? EntityState.Dead : driver != 0 ? EntityState.InVehicle : EntityState.Alive, colourAndKind);
        r.Pitch = unchecked((short)driver);
        return r;
    }

    /// <summary>Weapon byte of a vehicle record: type index in the high nibble, colour variant in the low one.</summary>
    public static byte Pack(int kind, byte colour) => (byte)((kind << 4) | (colour & 0xF));

    /// <summary>
    /// A trailer's record (a type with a <c>trailer</c> block; protocol 67). It has no seats, so the pitch field that carries a driver's id carries the id of the tractor it is hitched to
    /// (0: loose), and bit 6 of the health byte, free until then, says it is <see cref="LoadedBit">loaded</see> (the rocket cart's missile). Position and yaw are the trailer's own, as
    /// any vehicle's; a client draws a hitched one from its tractor as it draws it (<see cref="TowModel.Pose"/>) and the articulation, which is the two yaws' difference.
    /// </summary>
    public static EntityRecord MakeTrailer(ushort id, Vector3 pos, float yaw, ushort tractor, byte colourAndKind, bool loaded, int stage = 0, bool destroyed = false)
    {
        var health = (stage & StageMask) | (loaded ? LoadedBit : 0);
        var r = EntityRecord.Make(id, EntityType.Vehicle, pos, yaw, 0, health, destroyed ? EntityState.Dead : EntityState.Alive, colourAndKind);
        r.Pitch = unchecked((short)tractor);
        return r;
    }

    /// <summary>Health byte bit 6 of a trailer's record: the load is on it. Bit 7 (<see cref="LightsBit"/>) and the wheel bits are a driven vehicle's; a trailer has neither.</summary>
    public const byte LoadedBit = 0x40;

    public static ushort Driver(in EntityRecord r) => unchecked((ushort)r.Pitch);
    /// <summary>The vehicle a trailer's record says it is hitched to, 0 when loose (the same field as <see cref="Driver"/>: a trailer has none).</summary>
    public static ushort Tractor(in EntityRecord r) => unchecked((ushort)r.Pitch);
    /// <summary>Whether a trailer's record says it carries its load.</summary>
    public static bool Loaded(in EntityRecord r) => (r.Health & LoadedBit) != 0;
    public static int Stage(in EntityRecord r) => r.Health & StageMask;
    public static bool Lights(in EntityRecord r) => (r.Health & LightsBit) != 0;
    /// <summary>The brake bit of a driven vehicle's record (bit 6, which a trailer uses for its load): the server's brakes were on in its last step.</summary>
    public static bool Braking(in EntityRecord r) => (r.Health & BrakeBit) != 0;
    /// <summary>The wheels taken, one bit each (0 front left, 1 front right, 2 back left, 3 back right).</summary>
    public static byte Wheels(in EntityRecord r) => (byte)((r.Health & WheelsMask) >> WheelsShift);
    public static byte Colour(in EntityRecord r) => (byte)(r.Weapon & 0xF);
    public static int Kind(in EntityRecord r) => r.Weapon >> 4;
}

/// <summary>
/// Planar arcade vehicle model (car, motorcycle: same model, different constants in vehicles.json), Godot-free.
/// Only the server runs it: clients render snapshots, so there is no client-side mirror to keep in step.
/// The host supplies collision (the real speed after the physics move is fed back).
/// </summary>
public struct CarState
{
    /// <summary>Yaw in radians, Godot convention (0 faces -Z, positive turns left).</summary>
    public float Yaw;
    /// <summary>Signed speed along the facing direction, m/s (negative = reversing).</summary>
    public float Speed;
    /// <summary>Current yaw rate, rad/s, eased toward the steering target at <see cref="VehicleHandling.YawSlew"/> so the nose never snaps round.</summary>
    public float YawRate;
    /// <summary>The brakes bit in the last step (the handbrake, or the key against the travel, while it moved); the brake lamps' state (#409).</summary>
    public bool Braking;
}

public static class CarModel
{
    /// <summary>
    /// Turn radius (m) at a speed: the steering lock radius at a standstill, widening linearly with the fraction of top speed
    /// to <see cref="VehicleDef.HighSpeedRadiusFactor"/> times that at top speed. The yaw rate (v / radius) then eases up with
    /// speed instead of collapsing, so the car stays responsive when fast.
    /// </summary>
    public static float TurnRadiusAt(VehicleDef def, float speed)
    {
        var t = Math.Clamp(MathF.Abs(speed) / MathF.Max(0.1f, def.TopSpeed), 0f, 1f);
        return def.TurnRadius * (1f + (def.HighSpeedRadiusFactor - 1f) * t);
    }

    /// <summary>
    /// One tick. <paramref name="throttle"/> and <paramref name="steer"/> are -1..1 (forward, right), <paramref name="handbrake"/> is Jump.
    /// Longitudinal: acceleration to top speed, boosted near a standstill (<see cref="VehicleHandling.LaunchBoost"/>) (reverse capped by the handling), brake against travel, coast when idle.
    /// Lateral: yaw rate is speed / <see cref="TurnRadiusAt"/>, so it is zero when standing still, tight at walking pace and
    /// only moderately wider at top speed; the actual yaw rate chases that target at a limited slew rate.
    /// <paramref name="onWater"/> is set while the vehicle floats: an amphibious then drives at its water speed.
    /// </summary>
    /// <param name="tow">What a trailer on the hook does to the step (<see cref="TowLoad"/>, <see cref="TowBlock"/>); the default is no trailer and changes nothing.</param>
    public static void Step(ref CarState s, VehicleDef def, float throttle, float steer, bool handbrake, float dt, bool onWater = false, in TowEffect tow = default)
    {
        var h = def.Handling;
        var v = s.Speed;
        var top = def.TopSpeed;
        var accel = def.Acceleration;
        var coast = h.CoastDecel;
        if (onWater && def.Medium == VehicleMedium.Amphibious)
        {
            top = def.WaterTopSpeed; accel = def.WaterAcceleration;
        }
        var brake = def.Brake;
        if (tow.Hitched) { top *= tow.Load.TopSpeedScale; accel *= tow.Load.AccelScale; brake *= tow.Load.BrakeScale; }
        // the launch boost: more thrust near a standstill, fading to the plain acceleration at h.LaunchSpeed (either direction of travel)
        accel *= 1f + (h.LaunchBoost - 1f) * MathF.Max(0f, 1f - MathF.Abs(v) / MathF.Max(0.1f, h.LaunchSpeed));
        s.Braking = MathF.Abs(v) > 1e-3f && (handbrake || throttle > 0.05f && v < 0 || throttle < -0.05f && v > 0);
        if (handbrake) v = FlightMath.Approach(v, 0, h.HandbrakeDecel * dt);
        else if (throttle > 0.05f)
            v = v < 0 ? FlightMath.Approach(v, 0, brake * dt) : tow.Hitched && v > top ? FlightMath.Approach(v, top, brake * dt) : MathF.Min(top, v + accel * throttle * dt);
        else if (throttle < -0.05f)
            v = v > 0 ? FlightMath.Approach(v, 0, brake * dt) : MathF.Max(-top * h.ReverseTopFraction, v + accel * throttle * dt);
        else v = FlightMath.Approach(v, 0, coast * dt);
        s.Speed = v;

        // steering: the wheel input sets the curvature; the handbrake sharpens it for a slide
        var target = -steer * v / TurnRadiusAt(def, v) * (handbrake ? h.HandbrakeSteerBoost : 1f);
        if (tow.Hitched && MathF.Abs(v) > 0.5f) { var hold = def.MaxLateralAccel * tow.Load.LateralScale / MathF.Abs(v); target = Math.Clamp(target, -hold, hold); } // the trailer's weight: the bend must be wider
        s.YawRate = FlightMath.Approach(s.YawRate, target, h.YawSlew * dt);
        if (tow.Block.Active) { var slip = 0f; tow.Block.Clamp(ref s.Speed, ref slip, ref s.YawRate); } // the trailer is at its limit: what would fold it further is taken
        s.Yaw += s.YawRate * dt;
    }
}
