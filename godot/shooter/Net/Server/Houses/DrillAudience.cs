using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// Who is told of each Drill (<see cref="DrillServer"/>): the humans near its safe (trap_house.json <c>drill.near_m</c>), each with the party flag they were told last. A human who comes near
/// is told its state (<see cref="EventKind.DrillState"/>), one who goes far (past <see cref="FarFactor"/> times it) or the Drill going clears it (<see cref="EventKind.DrillCleared"/>);
/// a human who joins or leaves its party near the safe is told again, so the bar and the holds follow the squad.
/// </summary>
internal sealed class DrillAudience
{
    /// <summary>Hysteresis of the nearness: a player told of a Drill is cleared only past this times <c>near_m</c>.</summary>
    const float FarFactor = 1.2f;

    readonly ServerContext _ctx;
    /// <summary>Per house, the players told of its Drill and whether they were told they are of its party.</summary>
    readonly Dictionary<string, Dictionary<ushort, bool>> _told = new();

    public DrillAudience(ServerContext ctx) => _ctx = ctx;

    /// <summary>A Drill was armed on <paramref name="house"/>: nobody is told of it yet.</summary>
    public void Open(string house) => _told[house] = new Dictionary<ushort, bool>();

    /// <summary>House <paramref name="house"/>'s Drill went: everyone told of it is cleared.</summary>
    public void Close(string house)
    {
        if (!_told.Remove(house, out var told)) return;
        foreach (var id in told.Keys) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.SendEvent(c, EventKind.DrillCleared, DrillCodec.EncodeCleared(house));
    }

    /// <summary>Every player told of the Drill hears its change (with the party flag they hold; a change of it is <see cref="Tick"/>'s).</summary>
    public void Tell(Drill d)
    {
        if (!_told.TryGetValue(d.House, out var told)) return;
        foreach (var (id, party) in told) if (_ctx.Players.TryGetValue(id, out var c)) Send(c, d, party);
    }

    void Send(ServerClient c, Drill d, bool party) => _ctx.SendEvent(c, EventKind.DrillState, DrillCodec.EncodeState(new DrillMsg(d.House, d.State, party, d.Progress, d.RunSeconds)));

    /// <summary>Every tick: a human who came near a safe is told its Drill, one who went far is cleared, one whose party changed is told again; one who left is forgotten.</summary>
    public void Tick(Dictionary<string, Drill> drills, IReadOnlyDictionary<string, TrapHouseDressing> dressed, float nearM)
    {
        foreach (var (house, drill) in drills)
        {
            if (!dressed.TryGetValue(house, out var d) || d.Safe is not { } safe || !_told.TryGetValue(house, out var told)) continue;
            foreach (var id in told.Keys) if (!_ctx.Players.ContainsKey(id)) told.Remove(id); // a removal does not end the walk
            foreach (var c in _ctx.Roster)
            {
                if (c.IsBot || !c.Joined) continue;
                var dist = NVec3.Distance(c.Body.GlobalPosition.ToNumerics(), safe.Pos);
                var party = drill.Party.Contains(c.Id);
                if (told.TryGetValue(c.Id, out var was))
                {
                    if (dist > nearM * FarFactor) { told.Remove(c.Id); _ctx.SendEvent(c, EventKind.DrillCleared, DrillCodec.EncodeCleared(house)); }
                    else if (was != party) { told[c.Id] = party; Send(c, drill, party); }
                }
                else if (dist <= nearM) { told[c.Id] = party; Send(c, drill, party); }
            }
        }
    }
}
