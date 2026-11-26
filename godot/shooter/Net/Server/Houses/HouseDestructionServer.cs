using System.Text.Json;
using Godot;
using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// The voxel destruction of the hollow houses on the server (docs/house-destruction.md, WP-E). The map's house_voxels.bin (manifest <c>files.house_voxels</c>; a map without it,
/// the arena, or house_destruction.json <c>enabled: false</c>: nothing here does anything) gives every hollow house its hidden grid. A blast (<see cref="Combat.WorldBlasts"/>) carves every
/// house its sphere touches: <see cref="CarveOp.From"/>, <see cref="HouseCells.Apply"/> (carve and settle, the same pure maths every client runs), and the op goes to everyone
/// (<see cref="EventKind.HouseCarve"/>). The server never sends what collapsed. A house that lost cells gets a new server collider once a tick at most (<see cref="Flush"/>), so the
/// holes are real for movement, shots and a blast's line of sight. A joiner gets the settings and the removed cells of every house that was touched; a client that finds itself
/// out of step asks for a house again (<see cref="EventKind.HouseResync"/>). A new round, or <c>/houses restore</c>, makes every house whole.
/// </summary>
internal sealed class HouseDestructionServer
{
    /// <summary>How far ahead <c>/carve</c> looks for the surface under the crosshair, metres.</summary>
    const float CarveReachM = 500f;

    readonly ServerContext _ctx;
    readonly DestructionConfig _cfg;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<WorldQuery> _query;
    readonly HouseCells[] _houses;
    /// <summary>Each house's bounding sphere, map local (z up): a blast far from it skips the exact test.</summary>
    readonly (NVec3 Centre, float Radius)[] _bounds;
    readonly bool[] _dirty;
    readonly HouseCollider?[] _colliders;
    readonly bool[] _noCollider;
    readonly HouseColliderBuilder _collider = new();
    /// <summary><c>/rubble</c>'s choice for this round; null = house_destruction.json's.</summary>
    bool? _rubbleStays;

    sealed record HouseCollider(CollisionShape3D Node, Shape3D Original);

    public HouseDestructionServer(ServerContext ctx, DestructionConfig cfg, bool arenaMode, string mapDir, Drycastle.MapLoader.MapLoader? map, Func<WorldQuery> query)
    {
        _ctx = ctx; _cfg = cfg; _map = map; _query = query;
        var grids = arenaMode || !cfg.Enabled || mapDir == "" ? Array.Empty<HouseGrid>() : Load(mapDir);
        _houses = grids.Select(g => new HouseCells(g)).ToArray();
        _bounds = grids.Select(Bounds).ToArray();
        _dirty = new bool[_houses.Length];
        _colliders = new HouseCollider?[_houses.Length];
        _noCollider = new bool[_houses.Length];
        if (_houses.Length > 0) GD.Print($"server: house destruction on, {_houses.Length} hollow houses");
    }

    /// <summary>Whether any house can be carved on this map.</summary>
    public bool On => _houses.Length > 0;

    bool RubbleStays => _rubbleStays ?? _cfg.Debris.Rubble == RubbleMode.Stay;

    /// <summary>The grids of the map's house_voxels.bin; none when the manifest lists no file or it is missing. A broken file is logged and counts as none.</summary>
    static IReadOnlyList<HouseGrid> Load(string mapDir)
    {
        try
        {
            var manifest = Path.Combine(mapDir, "manifest.json");
            if (!File.Exists(manifest)) return Array.Empty<HouseGrid>();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty("house_voxels", out var f) || f.GetString() is not { } name) return Array.Empty<HouseGrid>();
            var path = Path.Combine(mapDir, name);
            if (!File.Exists(path)) { GD.PrintErr($"server: {path} is listed in the manifest but missing; houses are not destructible"); return Array.Empty<HouseGrid>(); }
            using var s = File.OpenRead(path);
            return HouseVoxelFile.Read(s);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            GD.PrintErr($"server: {e.Message}; houses are not destructible");
            return Array.Empty<HouseGrid>();
        }
    }

    static (NVec3, float) Bounds(HouseGrid g) =>
        (g.ToMap(new NVec3(g.Nx, g.Ny, g.Nz) * 0.5f), 0.5f * g.Cell * MathF.Sqrt((float)g.Nx * g.Nx + (float)g.Ny * g.Ny + (float)g.Nz * g.Nz));

    /// <summary>Godot world (y up, -z north) to map local (x east, y north, z up), as interiors.json.</summary>
    static NVec3 ToMap(Vector3 v) => new(v.X, -v.Z, v.Y);

    // ---- what carves ----

    /// <summary>A blast at <paramref name="at"/> (Godot world) of <paramref name="splashRadius"/> from <paramref name="source"/>: it carves its radius of house_destruction.json (0 = none).</summary>
    public void Blast(Vector3 at, float splashRadius, BlastSource source)
    {
        if (!On) return;
        var r = _cfg.CarveRadius(source, splashRadius);
        if (r > 0) Carve(ToMap(at), r);
    }

    /// <summary>Carves a sphere of <paramref name="radiusM"/> at <paramref name="map"/> (map local) out of every house it touches, one op each, sent to everyone. Returns how many houses it reached.</summary>
    int Carve(NVec3 map, float radiusM)
    {
        var reached = 0;
        for (var i = 0; i < _houses.Length; i++)
        {
            var (centre, radius) = _bounds[i];
            var reach = radius + radiusM;
            if (NVec3.DistanceSquared(centre, map) > reach * reach) continue;
            var house = _houses[i];
            if (CarveOp.From(house.Grid, map, radiusM) is not { } op) continue;
            var result = house.Apply(op, _cfg);
            _ctx.Broadcast(EventKind.HouseCarve, HouseCodec.EncodeCarve(new HouseCarveMsg(house.Grid.Index, house.OpSeq, op, result.Hash))); // every op, even one that took nothing: the clients count them too
            if (result.Carved.Length > 0 || result.Crumbled.Length > 0 || result.Fallen.Count > 0) _dirty[i] = true;
            reached++;
        }
        return reached;
    }

    /// <summary>The houses that changed this tick get their new collider, each once (called once at the end of the tick).</summary>
    public void Flush()
    {
        for (var i = 0; i < _houses.Length; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            RebuildCollider(i);
        }
    }

    // ---- the server's collider ----

    /// <summary>
    /// The house's collision node gets the voxel trimesh of its live cells (<see cref="HouseColliderBuilder"/>, the same as every client's), or its original exact shell back
    /// once it is whole. The node stays (its body, layers and place in the map), so <see cref="WorldQuery"/> and the splash's line of sight see the holes with no change.
    /// </summary>
    void RebuildCollider(int i)
    {
        if (ColliderOf(i) is not { } col) return;
        var house = _houses[i];
        col.Node.Shape = house.Damaged ? _collider.Build(house, col.Node, HouseColliderBuilder.MapToWorld(_map!.Root)) : col.Original;
    }

    /// <summary>
    /// The collision node of house <paramref name="i"/> (MapLoader's <c>HouseNodes</c>, Contract 2) and its original shape. A node freed by a reload is looked up again.
    /// </summary>
    HouseCollider? ColliderOf(int i)
    {
        if (_colliders[i] is { } known && GodotObject.IsInstanceValid(known.Node)) return known;
        _colliders[i] = null;
        if (_map == null || _noCollider[i]) return null;
        var id = _houses[i].Grid.Id;
        var cs = _map.HouseNodes.GetValueOrDefault(id)?.Collision;
        if (cs == null || !GodotObject.IsInstanceValid(cs) || cs.Shape == null)
        {
            _noCollider[i] = true;
            GD.PrintErr($"server: house {id} has no house_{id}_collision in the map; the server keeps its whole collider");
            return null;
        }
        return _colliders[i] = new HouseCollider(cs, cs.Shape);
    }

    // ---- rounds and catch-up ----

    /// <summary>A new round: every house whole, <c>/rubble</c>'s choice forgotten, and everyone told.</summary>
    public void RoundStart()
    {
        if (!On) return;
        _rubbleStays = null;
        RestoreAll();
    }

    void RestoreAll()
    {
        for (var i = 0; i < _houses.Length; i++)
        {
            if (_houses[i].Damaged) _dirty[i] = true; // back to the original shell at the flush
            _houses[i].Restore();
        }
        _ctx.Broadcast(EventKind.HouseSettings, HouseCodec.EncodeSettings(new HouseSettingsMsg(RubbleStays, Reset: true)));
    }

    /// <summary>The joiner's catch-up: the settings, then the removed cells of every house an op has touched this round (one whose op count is not 0, even with nothing removed: the joiner's count must match).</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (!On) return;
        _ctx.SendEvent(c, EventKind.HouseSettings, HouseCodec.EncodeSettings(new HouseSettingsMsg(RubbleStays, Reset: false)));
        for (var i = 0; i < _houses.Length; i++)
            if (_houses[i].OpSeq != 0 || _houses[i].Damaged) SendCells(c, i);
    }

    /// <summary>A client's state of a house differs from the server's (u16 house): it gets the house's removed cells.</summary>
    public void OnResync(ServerClient c, byte[] payload)
    {
        if (On && HouseCodec.DecodeResync(payload) is { } i && i < _houses.Length) SendCells(c, i);
    }

    void SendCells(ServerClient c, int i)
    {
        foreach (var m in HouseCodec.EncodeCells(_houses[i])) _ctx.SendEvent(c, EventKind.HouseCells, m);
    }

    // ---- admin commands ----

    /// <summary>"/carve &lt;r&gt;": a carve of r metres where the admin's crosshair meets the world (no damage, no glass, no blast).</summary>
    public void ChatCarve(ServerClient c, IReadOnlyList<string> args)
    {
        if (!On) { _ctx.ChatTo(c, ChatLineKind.Error, "House destruction is off: this map has no house_voxels.bin, or house_destruction.json turns it off."); return; }
        var r = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        var eye = c.Body.GlobalPosition + Vector3.Up * MovementStep.EyeHeight(c.Body.State.Stance);
        var f = HitMath.Forward(c.Body.Yaw, c.Body.Pitch);
        var dir = new Vector3(f.X, f.Y, f.Z);
        var stop = _query().Shot(eye, dir, CarveReachM);
        if (!stop.Hit) { _ctx.ChatTo(c, ChatLineKind.Error, $"Your crosshair meets nothing within {CarveReachM:0} m."); return; }
        var n = Carve(ToMap(eye + dir * stop.Distance), r);
        _ctx.ChatTo(c, n > 0 ? ChatLineKind.Info : ChatLineKind.Error, n > 0 ? $"Carved {r} m out of {n} house{(n == 1 ? "" : "s")}." : "No hollow house under your crosshair.");
    }

    /// <summary>"/houses restore": every house whole now, for everyone.</summary>
    public void ChatHouses(ServerClient c, IReadOnlyList<string> args)
    {
        if (!On) { _ctx.ChatTo(c, ChatLineKind.Error, "House destruction is off on this map."); return; }
        RestoreAll();
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} restored every house.");
    }

    /// <summary>"/rubble [fade|stay]": the rubble mode for the rest of the round (D8), sent to everyone; no argument tells it.</summary>
    public void ChatRubble(ServerClient c, IReadOnlyList<string> args)
    {
        if (args.Count == 0) { _ctx.ChatTo(c, ChatLineKind.Info, $"Rubble {(RubbleStays ? "stays" : "fades")} this round."); return; }
        _rubbleStays = args[0] == "stay";
        if (On) _ctx.Broadcast(EventKind.HouseSettings, HouseCodec.EncodeSettings(new HouseSettingsMsg(RubbleStays, Reset: false)));
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} set rubble to {(RubbleStays ? "stay" : "fade")} for this round.");
    }
}
