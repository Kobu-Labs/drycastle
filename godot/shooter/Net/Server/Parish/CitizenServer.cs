using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Parish;

/// <summary>
/// Citizens of the Holy Mass on the server: world entities (type 51) with a pure brain (<see cref="Citizen"/>), run by a
/// <see cref="CitizenCrowd"/>. They are not bots: they are not in the match roster, and the bots never see them. They are hit targets: <see cref="CitizenCrowd.AddTargets"/> lists the
/// living ones under ids of their own (<see cref="CitizenRules.FirstHitId"/> up), so every weapon path reaches them, and <see cref="MatchSim.ApplyDamage"/> hands those ids to the crowd
/// (through <see cref="Hits"/>). A kill scores nothing and is not in the kill feed; the Mass reads it from the crowd.
/// <para>
/// The church comes from <c>church.json</c> in the map folder (<see cref="Church"/>); without it, or without holy_mass.json, citizens are unavailable.
/// Houses: a parcel of kind building_plot with a building, between the data's minimum and the radius from the church door; its spawn point is
/// the point of the navigation mesh nearest to the parcel's anchor (a point inside the plot), accepted when it is within <see cref="DoorSnapMaxM"/>.
/// The server has no door positions of the buildings. They walk the same mesh as the bots (<see cref="BotNav"/>), built when first needed.
/// </para>
/// </summary>
internal sealed class CitizenServer : ICitizenWorld
{
    /// <summary>A house's spawn point is dropped when the mesh point nearest its anchor is further than this (the plot has no walkable ground).</summary>
    const float DoorSnapMaxM = 25f, DoorSnapMaxDy = 8f;

    readonly ServerContext _ctx;
    readonly HolyMassFile _holyMass;
    readonly bool _arenaMode;
    readonly string _mapDir;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<BotNav?> _nav;
    readonly Func<IWalkerGround> _walk;
    readonly Func<ServerClient, bool> _ensureNav;
    CitizenHitRouter? _hits;
    bool _churchTried;

    /// <param name="ensureNav">The navigation mesh, built when first needed (the server pauses for a few seconds); false when it could not be built.</param>
    public CitizenServer(ServerContext ctx, HolyMassFile holyMass, bool arenaMode, string mapDir, Drycastle.MapLoader.MapLoader? map, Func<BotNav?> nav, Func<IWalkerGround> walk, Func<ServerClient, bool> ensureNav)
    {
        _ctx = ctx; _holyMass = holyMass; _arenaMode = arenaMode; _mapDir = mapDir; _map = map; _nav = nav; _walk = walk; _ensureNav = ensureNav;
    }

    /// <summary>The crowd; null until the church is loaded (<see cref="Ensure"/>).</summary>
    public CitizenCrowd? Crowd { get; private set; }

    /// <summary>The church plan (seats, pastor, exit); null until <see cref="Ensure"/> has loaded it.</summary>
    public Church? Church { get; private set; }

    /// <summary>The router the match asks about citizen hit ids: the Mass crowd and the Urad workers are each added when they exist.</summary>
    public CitizenHitRouter Hits()
    {
        if (_hits == null) _ctx.Match.Citizens = _hits = new CitizenHitRouter();
        return _hits;
    }

    /// <summary>Once a tick, after the entities: citizens whose time has come leave their houses.</summary>
    public void Tick(float dt) => Crowd?.Update(dt);

    /// <summary>Loads the church and starts the crowd the first time; returns why citizens are unavailable, or null when they are ready.</summary>
    public string? Ensure()
    {
        if (Crowd != null) return null;
        if (_arenaMode || _mapDir == "") return "Citizens need the village map.";
        if (_churchTried) return "Citizens are unavailable: this map has no usable church.json (see the server log).";
        _churchTried = true;
        var path = Path.Combine(_mapDir, "church.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}; citizens are unavailable"); return "Citizens are unavailable: this map has no church.json yet."; }
        try { Church = Church.Load(path); }
        catch (Exception e) when (e is DataFileException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            GD.PrintErr($"server: {path}: {e.Message}; citizens are unavailable");
            return "Citizens are unavailable: church.json is broken (see the server log).";
        }
        Crowd = new CitizenCrowd(_holyMass.Citizens, Church, this, _ctx.Match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        Crowd.Killed += k => GD.Print($"server: #{k.Killer} killed a citizen (seat {k.SeatId}{(k.WasSeated ? ", seated" : "")})");
        Hits().Add(Crowd);
        GD.Print($"server: church loaded, {Church.Seats.Count} seats ({Church.Seats.Count(s => s.Floor == 1)} in the gallery)");
        return null;
    }

    // ---- ICitizenWorld ----

    public IWalkerGround? Walk => _walk();

    /// <summary>Spawn points of the houses between <paramref name="minM"/> and <paramref name="radiusM"/> of <paramref name="around"/>, on the navigation mesh.</summary>
    public IReadOnlyList<NVec3> HouseDoors(NVec3 around, float minM, float radiusM)
    {
        var doors = new List<NVec3>();
        if (_nav() is not { } nav || _map == null) return doors;
        foreach (var p in _map.Parcels.Values)
        {
            if (p.Kind != "building_plot" || p.BuildingIds.Count == 0) continue;
            var a = _map.AnchorOnTerrain(p.Id);
            var d = MathF.Sqrt((a.X - around.X) * (a.X - around.X) + (a.Z - around.Z) * (a.Z - around.Z));
            if (d < minM || d > radiusM) continue;
            var snap = nav.Snap(a);
            if (MathF.Sqrt((snap.X - a.X) * (snap.X - a.X) + (snap.Z - a.Z) * (snap.Z - a.Z)) > DoorSnapMaxM || MathF.Abs(snap.Y - a.Y) > DoorSnapMaxDy) continue;
            doors.Add(snap.ToNumerics());
        }
        return doors;
    }

    public IReadOnlyList<NVec3> Route(NVec3 from, NVec3 to)
    {
        if (_nav() is not { } nav) return Array.Empty<NVec3>();
        var route = nav.Route(new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
        return route.Length < 2 ? Array.Empty<NVec3>() : route.Select(v => v.ToNumerics()).ToArray();
    }

    /// <summary>"/citizens n" sends n citizens to free seats; "/citizens clear" removes them all.</summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        if (args[0] == "clear")
        {
            if (Crowd == null || Crowd.Count + Crowd.Waiting == 0) { _ctx.ChatTo(c, ChatLineKind.Info, "There are no citizens."); return; }
            var n = Crowd.Count + Crowd.Waiting;
            Crowd.Clear();
            _ctx.ChatTo(c, ChatLineKind.Info, $"Removed {n} citizens.");
            return;
        }
        if (Ensure() is { } why) { _ctx.ChatTo(c, ChatLineKind.Error, why); return; }
        if (!_ensureNav(c)) { _ctx.ChatTo(c, ChatLineKind.Error, "Citizens need the navigation mesh, which failed to build. See the server log."); return; }
        var want = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        var sent = Crowd!.Spawn(want);
        if (sent == 0)
            _ctx.ChatTo(c, ChatLineKind.Error, Crowd.FreeSeats.Any() ? $"Nobody can leave: no house within {_holyMass.Citizens.HouseRadiusM:0} m of the church door, or the limit of {_holyMass.Citizens.MaxCount} is reached." : "Every seat has a citizen.");
        else
            _ctx.ChatTo(c, ChatLineKind.Info, $"{sent} citizens leave their houses over {_holyMass.Citizens.StaggerSeconds:0} s{(sent < want ? $" (only {sent} could be sent)" : "")}.");
    }
}
