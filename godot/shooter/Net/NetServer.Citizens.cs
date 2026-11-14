using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Citizens of the Holy Mass on the server: world entities (type 51) with a pure brain (<see cref="Citizen"/>), run by a
/// <see cref="CitizenCrowd"/>. They are not bots: they are not in the match roster, and the bots never see them (<see cref="_botContacts"/>
/// holds players only). They are hit targets: <see cref="LiveTargets"/> lists the living ones under ids of their own
/// (<see cref="CitizenRules.FirstHitId"/> up), so every weapon path reaches them, and <see cref="MatchSim.ApplyDamage"/> hands those ids to the crowd.
/// A kill scores nothing and is not in the kill feed; <see cref="CitizenKilled"/> reports it to the event.
/// <para>
/// The church comes from <c>church.json</c> in the map folder (<see cref="Church"/>); without it, or without holy_mass.json, citizens are unavailable.
/// Houses: a parcel of kind building_plot with a building, between the data's minimum and the radius from the church door; its spawn point is
/// the point of the navigation mesh nearest to the parcel's anchor (a point inside the plot), accepted when it is within <see cref="DoorSnapMaxM"/>.
/// The server has no door positions of the buildings. They walk the same mesh as the bots (<see cref="BotNav"/>), built when first needed.
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>holy_mass.json (set by the host); the citizens section.</summary>
    public HolyMassFile HolyMass = new();

    /// <summary>The Mass event's entry point: spawn citizens for given seats, clear them. Null until the church is loaded (<see cref="EnsureCitizens"/>).</summary>
    public CitizenCrowd? Citizens => _crowd;

    /// <summary>A citizen was killed by a player (the event's sinner rule reads this).</summary>
    public event Action<CitizenKilled>? CitizenKilled;

    CitizenCrowd? _crowd;
    Church? _church;
    bool _churchTried;

    /// <summary>A house's spawn point is dropped when the mesh point nearest its anchor is further than this (the plot has no walkable ground).</summary>
    const float DoorSnapMaxM = 25f, DoorSnapMaxDy = 8f;

    sealed class CitizenWorld : ICitizenWorld
    {
        readonly NetServer _s;
        public CitizenWorld(NetServer server) => _s = server;
        public IReadOnlyList<NVec3> HouseDoors(NVec3 around, float minM, float radiusM) => _s.HouseDoors(around, minM, radiusM);
        public IReadOnlyList<NVec3> Route(NVec3 from, NVec3 to) => _s.CitizenRoute(from, to);
    }

    /// <summary>Loads the church and starts the crowd the first time; returns why citizens are unavailable, or null when they are ready.</summary>
    string? EnsureCitizens()
    {
        if (_crowd != null) return null;
        if (ArenaMode || MapDir == "") return "Citizens need the village map.";
        if (_churchTried) return "Citizens are unavailable: this map has no usable church.json (see the server log).";
        _churchTried = true;
        var path = Path.Combine(MapDir, "church.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}; citizens are unavailable"); return "Citizens are unavailable: this map has no church.json yet."; }
        try { _church = Church.Load(path); }
        catch (Exception e) when (e is DataFileException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            GD.PrintErr($"server: {path}: {e.Message}; citizens are unavailable");
            return "Citizens are unavailable: church.json is broken (see the server log).";
        }
        _crowd = new CitizenCrowd(HolyMass.Citizens, _church, new CitizenWorld(this), _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        _crowd.Killed += k =>
        {
            GD.Print($"server: #{k.Killer} killed a citizen (seat {k.SeatId}{(k.WasSeated ? ", seated" : "")})");
            CitizenKilled?.Invoke(k);
        };
        _match.Citizens = _crowd;
        GD.Print($"server: church loaded, {_church.Seats.Count} seats ({_church.Seats.Count(s => s.Floor == 1)} in the gallery)");
        return null;
    }

    /// <summary>Spawn points of the houses between <paramref name="minM"/> and <paramref name="radiusM"/> of <paramref name="around"/>, on the navigation mesh.</summary>
    List<NVec3> HouseDoors(NVec3 around, float minM, float radiusM)
    {
        var doors = new List<NVec3>();
        if (_nav == null || Map == null) return doors;
        foreach (var p in Map.Parcels.Values)
        {
            if (p.Kind != "building_plot" || p.BuildingIds.Count == 0) continue;
            var a = Map.AnchorOnTerrain(p.Id);
            var d = MathF.Sqrt((a.X - around.X) * (a.X - around.X) + (a.Z - around.Z) * (a.Z - around.Z));
            if (d < minM || d > radiusM) continue;
            var snap = _nav.Snap(a);
            if (MathF.Sqrt((snap.X - a.X) * (snap.X - a.X) + (snap.Z - a.Z) * (snap.Z - a.Z)) > DoorSnapMaxM || MathF.Abs(snap.Y - a.Y) > DoorSnapMaxDy) continue;
            doors.Add(N(snap));
        }
        return doors;
    }

    IReadOnlyList<NVec3> CitizenRoute(NVec3 from, NVec3 to)
    {
        if (_nav == null) return Array.Empty<NVec3>();
        var route = _nav.Route(new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
        return route.Length < 2 ? Array.Empty<NVec3>() : route.Select(N).ToArray();
    }

    /// <summary>"/citizens n" sends n citizens to free seats; "/citizens clear" removes them all.</summary>
    void ChatCitizens(Client c, IReadOnlyList<string> args)
    {
        if (args[0] == "clear")
        {
            if (_crowd == null || _crowd.Count + _crowd.Waiting == 0) { ChatTo(c, ChatLineKind.Info, "There are no citizens."); return; }
            var n = _crowd.Count + _crowd.Waiting;
            _crowd.Clear();
            ChatTo(c, ChatLineKind.Info, $"Removed {n} citizens.");
            return;
        }
        if (EnsureCitizens() is { } why) { ChatTo(c, ChatLineKind.Error, why); return; }
        if (_nav == null && !_navTried)
        {
            ChatTo(c, ChatLineKind.Info, "Building the navigation for the first time. The server pauses for a few seconds.");
            _host.Flush();
            BuildBotNav(force: true);
        }
        if (_nav == null) { ChatTo(c, ChatLineKind.Error, "Citizens need the navigation mesh, which failed to build. See the server log."); return; }
        var want = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        var sent = _crowd!.Spawn(want);
        if (sent == 0)
            ChatTo(c, ChatLineKind.Error, _crowd.FreeSeats.Any() ? $"Nobody can leave: no house within {HolyMass.Citizens.HouseRadiusM:0} m of the church door, or the limit of {HolyMass.Citizens.MaxCount} is reached." : "Every seat has a citizen.");
        else
            ChatTo(c, ChatLineKind.Info, $"{sent} citizens leave their houses over {HolyMass.Citizens.StaggerSeconds:0} s{(sent < want ? $" (only {sent} could be sent)" : "")}.");
    }

    /// <summary>Once a tick, after the entities: citizens whose time has come leave their houses.</summary>
    void CitizensTick(float dt) => _crowd?.Update(dt);
}
