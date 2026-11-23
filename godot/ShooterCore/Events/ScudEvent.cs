using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.ShooterCore.Events;

/// <summary>Where the Scud event is: nothing out, the truck hidden in its search area, found (somebody boarded it), or the truck is gone.</summary>
public enum ScudEventPhase : byte { Idle, Searching, Found, Over }

/// <summary>What one tick of the event came to (<see cref="ScudEventRun.Tick"/>).</summary>
public enum ScudEventOutcome : byte { None, Found, TimedOut, TruckLost }

/// <summary>A circle on the ground: the centre (x, z in the game's axes) and the radius in metres.</summary>
public readonly record struct SearchArea(Vector2 Centre, float Radius)
{
    public bool Contains(Vector2 p) => Vector2.Distance(p, Centre) <= Radius;
}

/// <summary>
/// The search area of the Scud event (GLOSSARY "Search area"): a circle of <c>search_radius_cells</c> grid cells (the cell size the tactical map works out for the world sheet, <see cref="TacticalMapMath"/>)
/// whose centre is the truck's place moved by a random vector, so the truck is somewhere inside, anywhere inside as likely as anywhere else, and the centre says nothing about where. Pure.
/// </summary>
public static class ScudSearch
{
    /// <summary>The offset of the centre from the truck is at most this share of the radius, so the truck is always strictly inside, never on the edge.</summary>
    public const float MaxOffsetShare = 0.97f;

    /// <summary>The radius in metres of <paramref name="cells"/> grid cells on a sheet of <paramref name="extentMetres"/>.</summary>
    public static float RadiusM(int cells, float extentMetres) => cells * TacticalMapMath.CellSize(extentMetres);

    /// <summary>
    /// The area for a truck at <paramref name="truck"/>: the centre is the truck plus a vector whose direction is uniform and whose length is drawn so that the offset is uniform over a disc
    /// of <see cref="MaxOffsetShare"/> of the radius (length = share x radius x sqrt(u)); the truck then stands uniformly over a disc of that size round the centre.
    /// </summary>
    public static SearchArea Make(Vector2 truck, float radiusM, Random rng)
    {
        var angle = rng.NextDouble() * Math.PI * 2;
        var length = MaxOffsetShare * radiusM * Math.Sqrt(rng.NextDouble());
        var offset = new Vector2((float)(Math.Cos(angle) * length), (float)(Math.Sin(angle) * length));
        return new SearchArea(truck + offset, radiusM);
    }

    /// <summary>The radius as a whole number of grid cells on this sheet (what the intel screen says).</summary>
    public static int Cells(float radiusM, float extentMetres) => (int)MathF.Round(radiusM / TacticalMapMath.CellSize(extentMetres));
}

/// <summary>
/// The run of the Scud event itself (the machine of <see cref="ScudEventPhase"/>): searching until somebody boards the truck (found: the event is won) or the time is up, the truck is lost
/// or it is cancelled (over, no reward). After it is found the truck is an ordinary vehicle and the event is as good as done; the machine stays Found until the truck is destroyed (the rocket
/// carts at the bunkers live that long, see <see cref="ScudCartSlot"/>) or the round ends. Pure: the host feeds it who boards and whether the truck lives.
/// </summary>
public sealed class ScudEventRun
{
    public static readonly StateMachineSpec<ScudEventPhase> Spec = StateMachineSpec<ScudEventPhase>
        .Define("Scud.Event", "The Scud event: the truck hidden in its search area, found when a player boards it, or gone.", ScudEventPhase.Idle)
        .Allow(ScudEventPhase.Idle, ScudEventPhase.Searching)
        .Allow(ScudEventPhase.Searching, ScudEventPhase.Found, ScudEventPhase.Over)
        .Allow(ScudEventPhase.Found, ScudEventPhase.Over)
        .Allow(ScudEventPhase.Over, ScudEventPhase.Searching)
        .Note(ScudEventPhase.Idle, "No Scud event has run on this server yet.")
        .Note(ScudEventPhase.Searching, "The truck stands hidden and the search area is on the intel board and the tactical map (clock: the despawn time). The first player to board it finds it; nobody within the time, a destroyed truck, a cancel or a new round ends it with no reward.")
        .Note(ScudEventPhase.Found, "A player boarded the truck: the reward is paid, the intel entry and the search circle are gone and the event slot is free. The truck stays as an ordinary vehicle and the rocket carts keep standing at the bunkers until it is destroyed (or the round ends).")
        .Note(ScudEventPhase.Over, "The truck is gone (destroyed, despawned, or retired by a new round) and the carts with it. A new event may begin.")
        .Build();

    readonly StateMachine<ScudEventPhase> _fsm = new(Spec);

    public ScudEventPhase Phase => _fsm.Current;
    public bool Searching => _fsm.Is(ScudEventPhase.Searching);
    /// <summary>The truck is out (hidden or found): a new event cannot begin.</summary>
    public bool TruckOut => _fsm.IsAny(ScudEventPhase.Searching, ScudEventPhase.Found);
    /// <summary>Seconds of the search left (infinity when not searching).</summary>
    public float Left => Searching ? _fsm.Left : float.PositiveInfinity;
    public SearchArea? Area { get; private set; }
    /// <summary>The player who found the truck (0 until then, and when an admin ended the event).</summary>
    public ushort Finder { get; private set; }

    /// <summary>The truck stands hidden; the search runs for <paramref name="despawnSeconds"/>. False when a truck is already out.</summary>
    public bool Begin(SearchArea area, float despawnSeconds)
    {
        if (TruckOut) return false;
        Area = area; Finder = 0;
        return _fsm.Enter(ScudEventPhase.Searching, despawnSeconds);
    }

    /// <summary>
    /// One tick while the truck is out. Searching: <paramref name="boarder"/> (the first human in a seat, 0 none) finds it; no truck (<paramref name="truckAlive"/> false) loses it; the clock running out
    /// despawns it. Found: only the loss of the truck ends the machine. Returns what happened this tick.
    /// </summary>
    public ScudEventOutcome Tick(float dt, ushort boarder, bool truckAlive)
    {
        switch (_fsm.Current)
        {
            case ScudEventPhase.Searching:
                if (!truckAlive) { _fsm.Enter(ScudEventPhase.Over); return ScudEventOutcome.TruckLost; }
                if (boarder != 0) { Finder = boarder; _fsm.Enter(ScudEventPhase.Found); return ScudEventOutcome.Found; }
                if (_fsm.Tick(dt)) { _fsm.Enter(ScudEventPhase.Over); return ScudEventOutcome.TimedOut; }
                return ScudEventOutcome.None;
            case ScudEventPhase.Found:
                if (!truckAlive) { _fsm.Enter(ScudEventPhase.Over); return ScudEventOutcome.TruckLost; }
                return ScudEventOutcome.None;
            default:
                return ScudEventOutcome.None;
        }
    }

    /// <summary>An admin's <c>/event current end</c>: found, for <paramref name="finder"/>, whether anybody boarded it or not. False when not searching.</summary>
    public bool Win(ushort finder)
    {
        if (!Searching) return false;
        Finder = finder;
        return _fsm.Enter(ScudEventPhase.Found);
    }

    /// <summary>An admin's cancel, or a new round: over with no reward. False when no truck is out.</summary>
    public bool Despawn() => TruckOut && _fsm.Enter(ScudEventPhase.Over);

    /// <summary>The intel board's entry while the truck is hidden: the headline, a few sentences and the risks, the search area's centre and radius as the place.</summary>
    public static IntelEvent Intel(double now, SearchArea area) => new(
        IntelId, "scud", "Top secret weapon located",
        "Intelligence reports a mobile missile launcher hidden somewhere inside the marked search area. Find it before anyone else does. Rocket carts wait at the bunkers; one rocket flattens a village block.",
        new[] { "Exact position unknown: sweep the search area", "One missile levels everything within 60 m", "Armed rocket carts wait at the bunkers", "A shot loaded cart detonates like a missile" },
        now, area.Centre, Radius: area.Radius);

    /// <summary>The id of the event's entry on the intel board.</summary>
    public const string IntelId = "scud";
}

/// <summary>Whether a bunker's rocket cart is standing, or will be put there again. The machine is <see cref="ScudCartSlot.Spec"/>.</summary>
public enum CartSlotState : byte { Closed, Stocked, Restocking }

/// <summary>What the host must do after a <see cref="ScudCartSlot.Tick"/>.</summary>
public enum CartSlotAction : byte { None, Place }

/// <summary>
/// One bunker's rocket cart over the life of a Scud truck (GLOSSARY "Rocket cart"): it stands loaded; once that cart is emptied (a truck took its rocket), destroyed or removed the slot waits a cooldown
/// (<c>cart_cooldown_seconds</c>) and then asks the host to put a new loaded one at the bunker; the truck's end closes it. A cart that is only towed away, still loaded, is still the slot's. Pure.
/// </summary>
public sealed class ScudCartSlot
{
    public static readonly StateMachineSpec<CartSlotState> Spec = StateMachineSpec<CartSlotState>
        .Define("Scud.CartSlot", "One bunker's rocket cart during a Scud event: standing loaded, waiting to be replaced, or closed with the truck.", CartSlotState.Closed)
        .Allow(CartSlotState.Closed, CartSlotState.Stocked, CartSlotState.Restocking)
        .Allow(CartSlotState.Stocked, CartSlotState.Restocking, CartSlotState.Closed)
        .Allow(CartSlotState.Restocking, CartSlotState.Stocked, CartSlotState.Closed)
        .Note(CartSlotState.Closed, "No truck is out: no cart is put at the bunker.")
        .Note(CartSlotState.Stocked, "A loaded cart was put at the bunker. It stays the slot's until it is emptied, destroyed or removed, wherever it is towed to.")
        .Note(CartSlotState.Restocking, "The cart is gone or empty: the slot counts the cooldown (clock: cart_cooldown_seconds), then the host puts a new loaded cart at the bunker. A blocked place is tried again after a short wait.")
        .Build();

    /// <summary>The wait before the host tries again when the bunker had no free place for the new cart, seconds.</summary>
    public const float RetrySeconds = 20f;

    readonly StateMachine<CartSlotState> _fsm = new(Spec);

    public CartSlotState State => _fsm.Current;
    /// <summary>Seconds until a new cart is put (while restocking), else infinity.</summary>
    public float Left => State == CartSlotState.Restocking ? _fsm.Left : float.PositiveInfinity;

    /// <summary>The truck is out and the host tried to put the first cart: <paramref name="placed"/> stocks the slot, else it waits <see cref="RetrySeconds"/> and asks again.</summary>
    public void Open(bool placed = true)
    {
        if (placed) _fsm.Enter(CartSlotState.Stocked);
        else _fsm.Enter(CartSlotState.Restocking, RetrySeconds);
    }

    /// <summary>The truck is gone: no more carts.</summary>
    public void Close() => _fsm.Enter(CartSlotState.Closed);

    /// <summary>
    /// One tick. <paramref name="cartLoaded"/>: the slot's cart stands, alive and loaded. When it stopped being so, the cooldown of <paramref name="cooldownSeconds"/> starts; when it ran out the answer is
    /// <see cref="CartSlotAction.Place"/> (the slot stays Restocking until the host says how that went: <see cref="Placed"/>).
    /// </summary>
    public CartSlotAction Tick(float dt, bool cartLoaded, float cooldownSeconds)
    {
        switch (State)
        {
            case CartSlotState.Stocked:
                if (!cartLoaded) _fsm.Enter(CartSlotState.Restocking, cooldownSeconds);
                return CartSlotAction.None;
            case CartSlotState.Restocking:
                return _fsm.Tick(dt) ? CartSlotAction.Place : CartSlotAction.None;
            default:
                return CartSlotAction.None;
        }
    }

    /// <summary>The host tried to put the cart: <paramref name="ok"/> stocks the slot, else it waits <see cref="RetrySeconds"/> and asks again.</summary>
    public void Placed(bool ok)
    {
        if (State != CartSlotState.Restocking) return;
        if (ok) _fsm.Enter(CartSlotState.Stocked);
        else _fsm.Restart(RetrySeconds);
    }
}

/// <summary>Where a rocket cart may stand at a bunker: an origin on the ground (x, z) and the heading (Godot's: 0 faces -Z).</summary>
public readonly record struct CartSpot(Vector2 Origin, float Yaw);

/// <summary>
/// The places to try for a bunker's rocket cart, best first. The cart stands outside the bunker's footprint (<paramref name="bunkerRadius"/> from its centre) with its rear
/// <paramref name="standoff"/> metres off it, its long axis radial and its drawbar end pointing away from the bunker, so a vehicle can reverse straight onto the eye from the open ground. First
/// along the doorway's bearing (<paramref name="doorBearing"/>, radians, a direction in ground x, z; null when it is not known), then fanned either side of it and a little further out, then the other
/// directions all round. The host takes the first that is clear. Pure.
/// </summary>
public static class ScudCartSpots
{
    /// <summary>The fan either side of the doorway, degrees, and the extra distances tried along each direction, metres.</summary>
    static readonly float[] FanDeg = { 0, 25, -25, 50, -50, 80, -80 };
    static readonly float[] Further = { 0, 5, 10 };

    public static IReadOnlyList<CartSpot> Candidates(Vector2 bunkerCentre, float bunkerRadius, float? doorBearing, float standoff, float cartHalfLength)
    {
        var list = new List<CartSpot>();
        void Add(float bearing, float extra)
        {
            var u = new Vector2(MathF.Cos(bearing), MathF.Sin(bearing));
            var distance = bunkerRadius + standoff + extra + cartHalfLength;
            list.Add(new CartSpot(bunkerCentre + u * distance, RoadHeading.YawOf(u.X, u.Y))); // the drawbar (-Z) points away from the bunker
        }
        if (doorBearing is { } door)
            foreach (var extra in Further)
                foreach (var f in FanDeg) Add(door + f * MathF.PI / 180f, extra);
        for (var extra = 0; extra < Further.Length; extra++)
            for (var a = 0; a < 12; a++) Add(a * MathF.PI / 6f, Further[extra]);
        return list;
    }
}
