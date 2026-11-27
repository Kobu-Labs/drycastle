using System.Numerics;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// One vehicle's cargo volume as the Duffels meet it (GLOSSARY "Cargo volume"; docs/cartel.md "Cargo vehicles"): every live vehicle whose type has a vehicles.json <c>cargo_volume</c> has one, added to the
/// <see cref="DuffelBook"/> when the vehicle appears and removed when it goes. Pure: the server gives it the vehicle's pose (<see cref="VehiclePose"/>, null while the vehicle is destroyed or retired) so the tests drive
/// it with a fixed one.
/// <list type="bullet">
/// <item>It accepts a thrown Duffel when the doors are open (a type without doors is always open), a slot is free, its party is allowed (any, unless <see cref="Restrict"/>ed: the Cartel van takes only its Job's)
/// and, for a top-loaded bed (the truck), the Duffel came down through the top face.</item>
/// <item>A Duffel takes the first free slot (<see cref="Load"/>) and keeps it until it leaves (<see cref="Unload"/>).</item>
/// <item>A player takes one out, and works the doors, within the type's access radius of its access point (<see cref="InAccess"/>).</item>
/// </list>
/// The doors' state is <see cref="Doors"/> (machine <c>Cargo.Doors</c>); a reset shuts them.
/// </summary>
public sealed class VehicleCargo : ICargoVolume
{
    readonly VehicleCargoVolumeDef _def;
    readonly Func<VehiclePose?> _pose;
    readonly Duffel?[] _slots;
    IReadOnlySet<ushort>? _only;

    /// <param name="vehicle">The vehicle's world id.</param>
    /// <param name="pose">Where the vehicle stands now; null when it is gone (destroyed, retired): nothing meets, nothing is reachable.</param>
    /// <param name="marginM">The box takes a Duffel this much outside it too (a throw that lands against the doors).</param>
    public VehicleCargo(ushort vehicle, VehicleCargoVolumeDef def, Func<VehiclePose?> pose, float marginM)
    {
        Vehicle = vehicle; _def = def; _pose = pose; MarginM = marginM;
        _slots = new Duffel?[def.Capacity];
    }

    public ushort Vehicle { get; }
    public VehicleCargoVolumeDef Def => _def;
    /// <summary>Metres the box is grown by (duffels.json <c>cargo_margin_m</c>; the Cartel van's own <c>van_load_margin_m</c>).</summary>
    public float MarginM { get; set; }
    public CargoDoors Doors { get; } = new();
    /// <summary>The type has doors (the key works them); a type without (the truck) is always open and shows no door prompt.</summary>
    public bool HasDoors => _def.Doors.Count > 0;
    /// <summary>Loading and taking out work: the doors are open, or there are none.</summary>
    public bool Open => !HasDoors || Doors.IsOpen;
    public int Capacity => _slots.Length;
    /// <summary>How many Duffels are loaded now.</summary>
    public int Loaded { get; private set; }
    /// <summary>The party this volume takes Duffels of; null: any party's.</summary>
    public IReadOnlySet<ushort>? OnlyParty => _only;

    /// <summary>A Duffel was loaded (after it took its slot): the Cartel van's Job counts and tells the party.</summary>
    public event Action<Duffel>? DuffelLoaded;

    /// <summary>Only this party's Duffels load from now on (the Cartel van of a Heist: the Job's party set); null lets any party's in again.</summary>
    public void Restrict(IReadOnlySet<ushort>? party) => _only = party;

    /// <summary>The Duffel on a slot, or null.</summary>
    public Duffel? In(int slot) => slot >= 0 && slot < _slots.Length ? _slots[slot] : null;

    public bool Meets(Vector3 from, Vector3 to, out CargoMeet meet)
    {
        meet = default;
        if (_pose() is not { } p) return false;
        var a = FlightMath.Unrotate(from - p.Feet, p.Yaw, p.Pitch, p.Roll);
        var b = FlightMath.Unrotate(to - p.Feet, p.Yaw, p.Pitch, p.Roll);
        if (!CargoVolume.Enter(_def, a, b, MarginM, out var at, out var normal, out var top)) return false;
        meet = new CargoMeet(p.Feet + FlightMath.Rotate(at, p.Yaw, p.Pitch, p.Roll), FlightMath.Rotate(normal, p.Yaw, p.Pitch, p.Roll), top);
        return true;
    }

    public bool Contains(Vector3 point) => _pose() is { } p && CargoVolume.Contains(_def, p.Feet, p.Yaw, p.Pitch, p.Roll, point, MarginM);

    public bool Accepts(Duffel duffel, bool fromTop) =>
        _pose() != null && Open && Loaded < _slots.Length && (_only == null || ReferenceEquals(duffel.Party, _only)) && (!_def.FromTop || fromTop);

    public int Load(Duffel duffel)
    {
        var slot = Array.IndexOf(_slots, null);
        if (slot < 0) throw new InvalidOperationException("the cargo volume is full");
        _slots[slot] = duffel;
        Loaded++;
        DuffelLoaded?.Invoke(duffel);
        return slot;
    }

    public void Unload(Duffel duffel)
    {
        var slot = Array.IndexOf(_slots, duffel);
        if (slot < 0) return;
        _slots[slot] = null;
        Loaded--;
    }

    /// <summary>The flat distance from the player's feet to the access point while they stand within its radius, else null (the nearest vehicle wins the key).</summary>
    public float? InAccess(Vector3 feet) =>
        _pose() is { } p ? CargoVolume.AccessDistance(_def, p.Feet, p.Yaw, p.Pitch, p.Roll, feet) : null;

    /// <summary>The player at the access point may work the doors: the type has some and the vehicle stands. The flat distance, else null.</summary>
    public float? DoorsDistance(Vector3 feet) => HasDoors ? InAccess(feet) : null;

    public float? TakeOutDistance(Vector3 feet) => Open ? InAccess(feet) : null;

    /// <summary>The world point of a slot's centre-base now, or null (no slot defined, or the vehicle is gone).</summary>
    public Vector3? SlotAt(int slot) =>
        _def.Slot(slot) is { } s && _pose() is { } p ? p.Feet + FlightMath.Rotate(s.Position, p.Yaw, p.Pitch, p.Roll) : null;
}
