using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>
/// The Delivery rework's cargo vehicles (docs/cartel.md "Cargo vehicles"): what a cargo volume accepts (doors, room, party, a bed's top face), the slots, taking a Duffel out, the loss of a vehicle's load, the
/// doors' key choice at the access point, and the wire (cargo doors, the take-out, a loaded Duffel's vehicle and slot).
/// </summary>
public class CargoLoadingTests
{
    static readonly DuffelsFile Data = DuffelsFile.Default;
    const float Dt = 1f / 30f;
    static readonly Vector3 Eye = new(0, 1.65f, 0);
    static readonly Vector3 AtAccess = new(0, 0, -3.5f); // the access point of the standard box

    sealed class Flat : IDuffelWorld
    {
        public bool Blocked(Vector3 a, Vector3 b, out Vector3 at, out Vector3 normal)
        {
            if (a.Y >= 0 && b.Y < 0) { at = Vector3.Lerp(a, b, a.Y / (a.Y - b.Y)); normal = Vector3.UnitY; return true; }
            at = default; normal = default;
            return false;
        }

        public float? GroundY(Vector3 at, float up, float down) => at.Y - down <= 0 && 0 <= at.Y + up ? 0 : null;
    }

    /// <summary>A box 2 wide and <paramref name="height"/> tall, 4 long, its middle 6 m in front of the origin (z -8 to -4), two slots, the access point 2.5 m behind its middle. Doors: one swinging node, or none.</summary>
    static VehicleCargo Bay(IReadOnlySet<ushort>? party = null, bool doors = true, bool fromTop = false, float height = 1.5f, int capacity = 2, ushort vehicle = 0x4001, Func<VehiclePose?>? pose = null)
    {
        var def = new VehicleCargoVolumeDef
        {
            Centre = new[] { 0f, height / 2, 0f }, Size = new[] { 2f, height, 4f }, Capacity = capacity, FromTop = fromTop,
            Access = new[] { 0f, 0f, 2.5f }, AccessRadiusM = 1.5f,
            Slots = Enumerable.Range(0, capacity).Select(i => new[] { i - 0.5f, 0f, 0f, 0f }).ToList(),
        };
        if (doors) def.Doors.Add(new VehicleCargoDoorDef { Node = "cargo_door" });
        var bay = new VehicleCargo(vehicle, def, pose ?? (() => new VehiclePose(new Vector3(0, 0, -6), 0, 0, 0, 0)), (float)Data.CargoMarginM);
        bay.Restrict(party);
        if (doors) bay.Doors.Toggle(); // open unless a test shuts it
        return bay;
    }

    static (DuffelBook Book, WorldEntityManager Entities) NewBook(params ICargoVolume[] volumes)
    {
        var entities = new WorldEntityManager();
        var book = new DuffelBook(Data, entities, new Flat());
        foreach (var v in volumes) book.AddVolume(v);
        return (book, entities);
    }

    static void Fly(WorldEntityManager e, int ticks = 300) { for (var i = 0; i < ticks; i++) e.Tick(Dt); }

    /// <summary>The player gives a Duffel to themself and throws it level, 8 m along -Z (into the standard box); returns after it came down.</summary>
    static Duffel Throw(DuffelBook book, WorldEntityManager entities, IReadOnlySet<ushort> party, ushort player = 1, DuffelKind kind = DuffelKind.Weed)
    {
        var d = book.Give(kind, party, player, Vector3.Zero)!;
        book.Throw(player, Eye, 0, 0);
        Fly(entities);
        return d;
    }

    static readonly IReadOnlySet<ushort> Party = new HashSet<ushort> { 1, 2 };

    // ---- what a volume accepts ----

    [Fact]
    public void Shut_doors_refuse_a_throw_and_it_falls_beside_open_doors_load_it()
    {
        var bay = Bay();
        bay.Doors.Toggle(); // shut
        var (book, entities) = NewBook(bay);
        var refused = Throw(book, entities, Party);
        Assert.Equal(DuffelState.Lying, refused.State);
        Assert.Equal(0, bay.Loaded);
        Assert.True(MathF.Abs(refused.Position.X) >= 1.3f, "it met the shut lid from above and slid off its side: it lies beside the box, not in it");
        Assert.Equal(0, refused.Position.Y, 3);

        bay.Doors.Toggle(); // open
        var taken = Throw(book, entities, Party);
        Assert.Equal(DuffelState.Loaded, taken.State);
        Assert.Equal(1, bay.Loaded);
    }

    [Fact]
    public void A_type_without_doors_is_always_open()
    {
        var bay = Bay(doors: false);
        Assert.True(bay.Open);
        Assert.False(bay.HasDoors);
        Assert.Null(bay.DoorsDistance(AtAccess)); // no door prompt, the key is not the doors'
        Assert.NotNull(bay.TakeOutDistance(AtAccess)); // but a Duffel comes out of it
        var (book, entities) = NewBook(bay);
        Assert.Equal(DuffelState.Loaded, Throw(book, entities, Party).State);
    }

    [Fact]
    public void A_full_volume_refuses_the_next_and_it_falls_beside()
    {
        var bay = Bay(capacity: 1);
        var (book, entities) = NewBook(bay);
        Assert.Equal(DuffelState.Loaded, Throw(book, entities, Party).State);
        var next = Throw(book, entities, Party);
        Assert.Equal(DuffelState.Lying, next.State);
        Assert.Equal(1, bay.Loaded);
    }

    [Fact]
    public void Any_party_loads_into_an_ordinary_vehicle_but_the_Cartel_van_takes_only_its_Jobs_own_party()
    {
        var ordinary = Bay();
        var (book, entities) = NewBook(ordinary);
        Assert.Equal(DuffelState.Loaded, Throw(book, entities, new HashSet<ushort> { 1 }).State);

        var job = new HashSet<ushort> { 1 };
        var van = Bay(job);
        var (vbook, ventities) = NewBook(van);
        var other = Throw(vbook, ventities, new HashSet<ushort> { 1 }); // an equal set, but another Job's party
        Assert.Equal(DuffelState.Lying, other.State);
        Assert.Equal(DuffelState.Loaded, Throw(vbook, ventities, job).State);
        Assert.Equal(1, van.Loaded);
        van.Restrict(null); // an ordinary one again
        Assert.Equal(DuffelState.Loaded, Throw(vbook, ventities, new HashSet<ushort> { 1 }).State);
    }

    [Fact]
    public void A_top_loaded_bed_takes_only_what_comes_down_through_its_top_face()
    {
        // the level throw's arc comes down through y 1.85 (the bed's 1.5 and the margin) at about 5 m, inside the box's length (z -4.65 to -8.35): from above
        var bed = Bay(doors: false, fromTop: true);
        var (book, entities) = NewBook(bed);
        Assert.Equal(DuffelState.Loaded, Throw(book, entities, Party).State);

        // the same bed 3 m tall (boards): the arc meets its near face at the side, so it falls beside
        var boards = Bay(doors: false, fromTop: true, height: 3f);
        var (b2, e2) = NewBook(boards);
        var side = Throw(b2, e2, Party);
        Assert.Equal(DuffelState.Lying, side.State);
        Assert.Equal(0, boards.Loaded);
        Assert.InRange(side.Position.Z, -4.7f, 0f); // beside the near face, not past it

        // an ordinary box of the same size takes it from the side
        var open = Bay(doors: false, height: 3f);
        var (b3, e3) = NewBook(open);
        Assert.Equal(DuffelState.Loaded, Throw(b3, e3, Party).State);
    }

    [Fact]
    public void A_bed_is_never_loaded_from_below_or_the_side_by_a_duffel_that_lands_against_it()
    {
        var (book, _) = NewBook();
        var lying = book.Place(DuffelKind.Cash, Party, Vector3.Zero)!;
        Assert.True(Bay(doors: false).Accepts(lying, fromTop: false)); // an ordinary box takes a bag that lands against its doors
        Assert.False(Bay(doors: false, fromTop: true).Accepts(lying, fromTop: false)); // a bed does not
        Assert.True(Bay(doors: false, fromTop: true).Accepts(lying, fromTop: true));
    }

    [Fact]
    public void A_gone_vehicle_takes_nothing()
    {
        VehiclePose? pose = new VehiclePose(new Vector3(0, 0, -6), 0, 0, 0, 0);
        var bay = Bay(pose: () => pose);
        var (book, entities) = NewBook(bay);
        pose = null; // destroyed
        Assert.Equal(DuffelState.Lying, Throw(book, entities, Party).State);
        Assert.Null(bay.TakeOutDistance(AtAccess));
        Assert.Null(bay.DoorsDistance(AtAccess));
    }

    [Fact]
    public void A_moving_vehicle_is_met_where_it_is_now()
    {
        var pose = new VehiclePose(new Vector3(30, 0, -6), 0, 0, 0, 0); // far to the side
        var current = pose;
        var bay = Bay(pose: () => current);
        var (book, entities) = NewBook(bay);
        Assert.Equal(DuffelState.Lying, Throw(book, entities, Party).State);
        current = new VehiclePose(new Vector3(0, 0, -6), 0, 0, 0, 0);
        Assert.Equal(DuffelState.Loaded, Throw(book, entities, Party).State);
    }

    // ---- slots ----

    [Fact]
    public void Each_load_takes_the_first_free_slot_and_a_freed_slot_is_taken_again()
    {
        var bay = Bay();
        var (book, entities) = NewBook(bay);
        var a = Throw(book, entities, Party);
        var b = Throw(book, entities, Party);
        Assert.Equal((0, 1), (a.Slot, b.Slot));
        Assert.Same(a, bay.In(0)); Assert.Same(b, bay.In(1));
        Assert.Equal(1, DuffelRules.SlotOf(entities.StateOf(b.Id)!.Value.State));
        Assert.Equal(bay.Vehicle, entities.StateOf(b.Id)!.Value.Owner);

        Assert.Same(b, book.TakeOut(1, AtAccess)); // the last loaded comes out; slot 1 is free
        Assert.Null(bay.In(1));
        var c = Throw(book, entities, Party, player: 2);
        Assert.Equal(1, c.Slot);
        Assert.Equal(2, bay.Loaded);

        book.Remove(a); // a removed one frees its slot too
        Assert.Equal(1, bay.Loaded);
        Assert.Null(bay.In(0));
        Assert.Equal(0, Throw(book, entities, Party, player: 2, kind: DuffelKind.Cash).Slot);
    }

    // ---- taking out ----

    [Fact]
    public void A_party_player_with_empty_hands_at_the_open_cargo_area_takes_the_last_loaded_out()
    {
        var bay = Bay();
        var (book, entities) = NewBook(bay);
        var first = Throw(book, entities, Party, kind: DuffelKind.Weed);
        var last = Throw(book, entities, Party, kind: DuffelKind.Cash);
        Assert.Same(last, book.Takeable(1, AtAccess));
        var carriers = new List<(ushort, Duffel?)>();
        book.CarrierChanged += (p, d) => carriers.Add((p, d));
        Assert.Same(last, book.TakeOut(2, AtAccess + new Vector3(0.5f, 0, 0))); // a party mate within the radius
        Assert.Equal(DuffelState.Carried, last.State);
        Assert.Equal((ushort)2, last.Carrier);
        Assert.Null(last.Volume);
        Assert.Equal(DuffelState.Loaded, first.State);
        Assert.Equal(1, bay.Loaded);
        Assert.Equal(new[] { ((ushort)2, (Duffel?)last) }, carriers); // the server burdens the body from this
        Assert.Equal((ushort)2, entities.StateOf(last.Id)!.Value.Owner); // the owner field is the carrier again
        Assert.Null(book.Takeable(2, AtAccess)); // hands full
    }

    [Fact]
    public void Nothing_comes_out_for_another_party_a_shut_vehicle_or_a_player_away_from_the_access_point()
    {
        var bay = Bay();
        var (book, entities) = NewBook(bay);
        Throw(book, entities, Party);
        Assert.Null(book.Takeable(3, AtAccess)); // not their party's
        Assert.Null(book.Takeable(1, AtAccess + new Vector3(0, 0, 3))); // 3 m from the access point (radius 1.5)
        Assert.Null(book.Takeable(1, AtAccess + new Vector3(0, 3, 0))); // on a roof
        Assert.Null(book.TakeOut(3, AtAccess));
        bay.Doors.Toggle(); // shut
        Assert.Null(book.Takeable(1, AtAccess));
        bay.Doors.Toggle();
        Assert.NotNull(book.Takeable(1, AtAccess));
    }

    [Fact]
    public void A_bed_without_doors_gives_a_bag_out_at_once_and_the_nearer_vehicle_wins()
    {
        var near = Bay(doors: false, vehicle: 0x4002, pose: () => new VehiclePose(new Vector3(0, 0, -6), 0, 0, 0, 0));
        var far = Bay(doors: false, vehicle: 0x4003, pose: () => new VehiclePose(new Vector3(1.8f, 0, -6), 0, 0, 0, 0)); // its access point 1.8 m beside, both within the radius of the player
        var (book, entities) = NewBook(near, far);
        Throw(book, entities, Party); // the nearest volume on the way takes it: the near one
        var d = book.Give(DuffelKind.Weed, Party, 1, Vector3.Zero)!;
        book.Drop(1, Vector3.Zero);
        d.PutDown(Vector3.Zero);
        Assert.NotNull(book.Takeable(1, AtAccess));
        Assert.Equal((ushort)0x4002, book.Takeable(1, AtAccess)!.Owner);
    }

    // ---- losing the vehicle ----

    [Fact]
    public void A_lost_vehicle_destroys_the_duffels_it_held_and_their_slots_are_free()
    {
        var bay = Bay();
        var (book, entities) = NewBook(bay);
        var a = Throw(book, entities, Party);
        var b = Throw(book, entities, Party);
        var lying = book.Place(DuffelKind.Cash, Party, new Vector3(5, 0, 5))!;
        Assert.Equal(2, book.RemoveLoadedIn(bay));
        Assert.Null(book.Find(a.Id));
        Assert.Null(book.Find(b.Id));
        Assert.Same(lying, book.Find(lying.Id)); // only what was loaded
        Assert.Equal(1, entities.Count);
        Assert.Equal(0, bay.Loaded);
        Assert.Equal(0, book.LoadedIn(bay));
        Assert.Equal(0, book.RemoveLoadedIn(bay));
    }

    [Fact]
    public void A_removed_volume_takes_no_more()
    {
        var bay = Bay();
        var (book, entities) = NewBook(bay);
        Assert.True(book.RemoveVolume(bay));
        Assert.Equal(DuffelState.Lying, Throw(book, entities, Party).State);
    }

    [Fact]
    public void The_loaded_event_fires_once_per_load_with_the_count_already_up()
    {
        var bay = Bay();
        var seen = new List<int>();
        bay.DuffelLoaded += _ => seen.Add(bay.Loaded);
        var (book, entities) = NewBook(bay);
        Throw(book, entities, Party);
        Throw(book, entities, Party);
        Assert.Equal(new[] { 1, 2 }, seen);
    }

    // ---- the doors and the key ----

    [Fact]
    public void The_doors_are_shut_at_first_toggle_and_stay_as_left_and_a_reset_shuts_them()
    {
        var doors = new CargoDoors();
        Assert.Equal(CargoDoorState.Closed, doors.State);
        Assert.True(doors.Toggle());
        Assert.True(doors.IsOpen);
        Assert.False(doors.Toggle());
        doors.Toggle();
        Assert.True(doors.Close()); // a spawn, a reset, a respawn
        Assert.False(doors.IsOpen);
        Assert.False(doors.Close());
    }

    [Fact]
    public void The_doors_own_the_key_only_at_the_access_point_and_the_nearest_vehicle_wins()
    {
        var bay = Bay();
        Assert.NotNull(bay.DoorsDistance(AtAccess));
        Assert.NotNull(bay.DoorsDistance(AtAccess + new Vector3(1.4f, 0, 0)));
        Assert.Null(bay.DoorsDistance(AtAccess + new Vector3(1.6f, 0, 0))); // elsewhere round the vehicle E enters it
        Assert.Null(bay.DoorsDistance(new Vector3(2f, 0, -6))); // beside the cab
        Assert.Null(bay.DoorsDistance(AtAccess + new Vector3(0, 2.5f, 0))); // not on the roof
        Assert.True(bay.DoorsDistance(AtAccess)! < bay.DoorsDistance(AtAccess + new Vector3(1f, 0, 0))!);
    }

    [Fact]
    public void The_access_point_turns_with_the_vehicle()
    {
        var def = Bay().Def;
        var yaw = MathF.PI / 2; // turned a quarter: the vehicle's back is on another side, and so is its access point
        var p = FlightMath.Rotate(new Vector3(0, 0, 2.5f), yaw, 0, 0);
        Assert.NotNull(CargoVolume.AccessDistance(def, Vector3.Zero, yaw, 0, 0, p));
        Assert.Null(CargoVolume.AccessDistance(def, Vector3.Zero, yaw, 0, 0, new Vector3(0, 0, 2.5f)));
    }

    [Fact]
    public void The_doors_key_competes_with_a_flag_of_its_own_and_every_flag_is_a_distinct_bit()
    {
        var flags = Enum.GetValues<KeyCompetitor>().Where(k => k != KeyCompetitor.None).ToList();
        Assert.Contains(KeyCompetitor.CargoDoors, flags);
        foreach (var f in flags) Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)f));
        Assert.Equal(flags.Count, flags.Distinct().Count());
        // the plantation's hold gives way to a vehicle's doors like to everything else
        Assert.False(PlantationRules.HoldCounts(true, true, true, KeyCompetitor.CargoDoors));
    }

    // ---- the box geometry ----

    [Fact]
    public void A_segment_meets_the_grown_box_from_the_top_the_side_or_from_inside_and_a_miss_does_not()
    {
        var def = Bay().Def; // centre (0, 0.75, 0), 2 x 1.5 x 4
        const float m = 0.35f;
        Assert.True(CargoVolume.Enter(def, new Vector3(0, 3, 0), new Vector3(0, 0, 0), m, out var at, out _, out var top));
        Assert.True(top);
        Assert.Equal(1.5f + m, at.Y, 3);

        Assert.True(CargoVolume.Enter(def, new Vector3(0, 0.8f, 5), new Vector3(0, 0.8f, 0), m, out at, out var normal, out top));
        Assert.False(top);
        Assert.Equal(2f + m, at.Z, 3);
        Assert.Equal(new Vector3(0, 0, 1), normal); // out the way it came in

        Assert.True(CargoVolume.Enter(def, new Vector3(0, 0.8f, 0), new Vector3(0, 0.8f, 1), m, out at, out _, out top)); // starts inside
        Assert.False(top);
        Assert.True(CargoVolume.Enter(def, new Vector3(0, 1f, 0), new Vector3(0, 0f, 0), m, out _, out _, out top));
        Assert.True(top); // inside and moving down

        Assert.False(CargoVolume.Enter(def, new Vector3(5, 3, 0), new Vector3(5, 0, 0), m, out _, out _, out _)); // beside
        Assert.False(CargoVolume.Enter(def, new Vector3(0, 3, 5), new Vector3(0, 3, 4), m, out _, out _, out _)); // short of it
        Assert.False(CargoVolume.Enter(def, new Vector3(0, 3, 5), new Vector3(0, 3, -5), m, out _, out _, out _)); // over it
    }

    [Fact]
    public void A_refusal_on_the_top_face_is_put_aside_by_the_nearest_side_never_left_on_top()
    {
        var def = Bay().Def;
        Assert.True(CargoVolume.Enter(def, new Vector3(0.8f, 3, 0), new Vector3(0.8f, 0, 0), 0.35f, out var at, out var normal, out var top));
        Assert.True(top);
        Assert.Equal(new Vector3(1, 0, 0), normal); // 0.55 from the x face against 2.35 from the z face
        Assert.Equal(1f + 0.35f, at.X, 3);
        Assert.Equal(0f, normal.Y);
    }

    // ---- the wire ----

    [Fact]
    public void The_cargo_doors_and_take_out_payloads_and_the_slot_byte_round_trip()
    {
        Assert.True(CargoDoorsCodec.TryDecode(CargoDoorsCodec.Encode(0x4123, true), out var v, out var open));
        Assert.Equal((ushort)0x4123, v); Assert.True(open);
        Assert.True(CargoDoorsCodec.TryDecode(CargoDoorsCodec.Encode(0x4001, false), out v, out open));
        Assert.False(open);
        Assert.False(CargoDoorsCodec.TryDecode(new byte[] { 1, 2 }, out _, out _));
        Assert.False(CargoDoorsCodec.TryDecode(new byte[] { 1, 2, 9 }, out _, out _));

        Assert.True(DuffelCodec.TryDecodeTake(DuffelCodec.EncodeTake(0x4005, DuffelKind.Product), out v, out var kind));
        Assert.Equal(((ushort)0x4005, DuffelKind.Product), (v, kind));
        Assert.True(DuffelCodec.TryDecodeTake(DuffelCodec.EncodeTake(0, DuffelKind.Weed), out v, out _));
        Assert.Equal((ushort)0, v);
        Assert.False(DuffelCodec.TryDecodeTake(new byte[] { 1, 0, 7 }, out _, out _));
        Assert.False(DuffelCodec.TryDecodeTake(new byte[] { 1, 0 }, out _, out _));

        for (var slot = 0; slot < 15; slot++)
            foreach (var kind2 in DuffelRules.AllKinds)
            {
                var b = DuffelRules.Pack(DuffelState.Loaded, kind2, slot);
                Assert.Equal((DuffelState.Loaded, kind2, slot), (DuffelRules.StateOf(b), DuffelRules.KindOf(b), DuffelRules.SlotOf(b)));
            }
        Assert.Equal(0, DuffelRules.SlotOf(DuffelRules.Pack(DuffelState.Carried, DuffelKind.Cash))); // the slot is 0 unless loaded
    }

    [Fact]
    public void The_event_kinds_are_unique_and_the_new_ones_are_where_the_doc_says()
    {
        var all = Enum.GetValues<EventKind>().Select(k => (byte)k).ToList();
        Assert.Equal(all.Count, all.Distinct().Count()); // C# allows a duplicate value silently
        Assert.Equal((byte)44, (byte)EventKind.DuffelTake);
        Assert.Equal((byte)45, (byte)EventKind.CargoDoors);
        Assert.True(Wire.ProtocolVersion >= 92);
        Assert.Equal(14, (int)JobStartRefusal.PalletFull);
    }

    [Fact]
    public void The_shipped_vehicles_declare_a_label_for_the_doors_they_have_and_the_margin_parses()
    {
        var vehicles = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
        foreach (var v in vehicles.Vehicles.Where(v => v.CargoVolume != null))
        {
            var cargo = v.CargoVolume!;
            Assert.True(cargo.Doors.Count == 0 || cargo.Label.Length > 0, $"{v.Id}: a type with doors names them for the prompt");
            Assert.NotNull(cargo.Access); // every cargo type has a place to stand at
        }
        Assert.Equal(0.35, DuffelsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "duffels.json")).CargoMarginM);
    }
}
