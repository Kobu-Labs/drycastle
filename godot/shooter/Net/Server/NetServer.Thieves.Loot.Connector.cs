using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The connector kind of Loot on the server (docs/events/thieves/07): the poles of the map that carry wires as the pure <see cref="ConnectorSource"/> sees them (<see cref="ServerPoles"/>), and the
/// electrocution's effect (event <see cref="EventKind.ThievesFx"/>, <see cref="ThievesFxKind.Shock"/>). A pole's climb base is a point of the navigation mesh beside the column (<see cref="ConnectorLootConfig.BaseOffsetM"/>
/// away: the closest of eight points around it that the mesh really has), its top is straight above the base at the height of the highest wire seat (or <see cref="ConnectorLootConfig.TopFraction"/> of the pole's
/// height). A pole with no mesh point near is no target (no Thief could walk to it).
/// </summary>
public partial class NetServer
{
    /// <summary>The farthest the mesh may be from a wanted base point for the point to count (metres).</summary>
    const float PoleBaseSnapM = 1.5f;

    sealed class ServerPoles : IPoles
    {
        readonly NetServer _s;
        List<PoleSpot>? _spots;
        readonly Dictionary<int, PoleSpot> _byId = new();

        public ServerPoles(NetServer server) => _s = server;

        /// <summary>Built on first use, once the map and the mesh exist (nothing is cached while they do not).</summary>
        List<PoleSpot> Built()
        {
            if (_spots != null) return _spots;
            if (_s.Map == null || _s._nav == null) return new List<PoleSpot>();
            var cfg = _s.ThievesData.Loot.Connector;
            var spots = new List<PoleSpot>();
            foreach (var site in _s.Map.PoleSites())
            {
                var foot = site.Foot;
                if (BaseBeside(foot, cfg.BaseOffsetM) is not { } b) continue;
                var topY = (site.TopSeat?.Y ?? foot.Y + site.Height * cfg.TopFraction) - cfg.FeetBelowTopM;
                if (topY < b.Y + 1.5f) continue;
                spots.Add(new PoleSpot(spots.Count, foot, b, new NVec3(b.X, topY, b.Z)));
            }
            _spots = spots;
            foreach (var p in spots) _byId[p.Id] = p;
            GD.Print($"server: Thieves: {spots.Count} poles with wires are connector targets");
            return spots;
        }

        /// <summary>The mesh point nearest to one of eight points <paramref name="r"/> around the column (not inside the column's reach), or null when the mesh is farther than <see cref="PoleBaseSnapM"/> from all of them.</summary>
        NVec3? BaseBeside(NVec3 foot, float r)
        {
            NVec3? best = null;
            var bestErr = PoleBaseSnapM;
            for (var i = 0; i < 8; i++)
            {
                var a = i * MathF.PI / 4f;
                var want = new Vector3(foot.X + MathF.Cos(a) * r, foot.Y, foot.Z + MathF.Sin(a) * r);
                var got = _s._nav!.Snap(want);
                var err = new Vector2(got.X - want.X, got.Z - want.Z).Length();
                var fromColumn = new Vector2(got.X - foot.X, got.Z - foot.Z).Length();
                if (err >= bestErr || fromColumn < r * 0.5f) continue;
                bestErr = err; best = N(got);
            }
            return best;
        }

        public IReadOnlyList<PoleSpot> All() => Built();

        public bool TryGet(int id, out PoleSpot pole)
        {
            Built();
            return _byId.TryGetValue(id, out pole);
        }
    }

    /// <summary>Called by the marked line of <see cref="InitLoot"/>: registers the connector source (on the server's clock) and sends the shock's effect when a Thief is electrocuted.</summary>
    void RegisterConnectorLoot(LootRegistry registry)
    {
        registry.Register(new ConnectorSource(ThievesData.Loot.Connector, new ServerPoles(this), () => (float)_elapsed));
        _loot!.Shocked += OnThiefShocked;
    }

    /// <summary>A Thief was electrocuted at the top of a pole: everyone hears and sees it (the beams run for the shock's seconds), the id being the member's world entity.</summary>
    void OnThiefShocked(GangMember m, StealTarget pole)
    {
        foreach (var (id, entity) in _match.Entities.OfType(GangRules.TypeId))
            if (entity == m) { BroadcastEvent(EventKind.ThievesFx, ThievesFxCodec.Encode(new ThievesFxMsg(ThievesFxKind.Shock, m.Position, id))); return; }
    }
}
