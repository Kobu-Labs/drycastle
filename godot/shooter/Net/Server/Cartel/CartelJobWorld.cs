using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Thieves;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The world the simple Jobs run in (#494 Delivery, #497 Hit; docs/cartel.md "Simple jobs"): the Duffels (a Delivery places its own on the pallet; <see cref="DuffelServer"/>), the Rival crew (<see cref="RivalCrewServer"/>) and the players' feet and
/// interact key (the last input's <see cref="ServerClient.UseKey"/>). The rules are ShooterCore's (<see cref="DeliveryJobKind"/>, <see cref="HitJobKind"/>).
/// </summary>
internal sealed class CartelJobWorld : IDeliveryWorld, IHitWorld
{
    /// <summary>A guard's ring point snapped to the navigation mesh is used when it moved no more than this; else he stands at the dealer's.</summary>
    const float SnapM = 3f;

    readonly ServerContext _ctx;
    readonly DuffelServer _duffels;
    readonly RivalCrewServer _rivals;
    readonly Random _rng;

    public CartelJobWorld(ServerContext ctx, DuffelServer duffels, RivalCrewServer rivals, Random rng)
    {
        _ctx = ctx; _duffels = duffels; _rivals = rivals; _rng = rng;
    }

    public DuffelBook Duffels => _duffels.Book;

    public NVec3? Feet(ushort player) =>
        _ctx.Players.TryGetValue(player, out var c) && c.Car == null && _ctx.IsAlive(player) ? c.Body.GlobalPosition.ToNumerics() : null;

    public bool UseHeld(ushort player) => _ctx.Players.TryGetValue(player, out var c) && c.UseKey.Held;

    public IHitCrew? Spawn(HitCrewOrder order, Func<ushort, bool> hostileTo)
    {
        var centre = _rivals.Snap(order.Spot);
        if (NVec3.Distance(centre, order.Spot) > SnapM) centre = order.Spot;
        var crew = _rivals.NewCrew(centre, hostileTo);
        var dealer = crew.Add(GangMemberKind.GangBoss, centre, (float)_rng.NextDouble() * MathF.Tau - MathF.PI);
        if (dealer == null) { _rivals.Remove(crew); return null; }
        var kinds = CrewRules.Order(order.Gunmen, order.Enforcers); // Gunmen and Enforcers in turn round the ring, so each side has both
        var guards = kinds.Count;
        var turn = (float)_rng.NextDouble() * MathF.Tau;
        for (var i = 0; i < guards; i++)
        {
            var kind = kinds[i];
            var a = turn + MathF.Tau * i / guards;
            var raw = centre + new NVec3(MathF.Cos(a), 0, MathF.Sin(a)) * order.RingM;
            var snapped = _rivals.Snap(raw);
            var at = NVec3.Distance(snapped, raw) <= SnapM ? snapped : centre;
            crew.Add(kind, at, MathF.Atan2(-(raw.X - centre.X), -(raw.Z - centre.Z))); // facing out from the dealer
        }
        GD.Print($"server: a Rival dealer at ({centre.X:F1}, {centre.Z:F1}) with {crew.Count - 1} of {guards} guards");
        return new HitCrew(_rivals, crew, dealer);
    }

    /// <summary>The crew of one Hit; its marked member is the dealer.</summary>
    sealed class HitCrew(RivalCrewServer rivals, ThiefGang crew, GangMember dealer) : IHitCrew
    {
        bool _removed;

        public bool DealerPresent => !_removed && crew.Members.Contains(dealer);
        public bool DealerAlive => dealer.Alive;
        public NVec3 DealerAt => dealer.Position;

        public void Remove()
        {
            if (_removed) return;
            _removed = true;
            rivals.Remove(crew);
        }
    }
}
