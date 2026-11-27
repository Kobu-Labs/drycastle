using System.Text.Json;
using Godot;
using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.Shooter.Vehicles;
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
internal sealed class HouseDestructionServer : IHousePlough
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
    readonly HouseVoxelCollider?[] _colliders;
    readonly bool[] _noCollider;
    /// <summary>A house lost cells (after the op went out): its panes may have lost their frame (<see cref="Panes.GlassServer.HouseChanged"/>).</summary>
    public Action<HouseCells>? Changed { get; set; }
    /// <summary>Every house was made whole (a new round, <c>/houses restore</c>): what hung in their openings goes back (<see cref="HouseDoorServer.RehangAll"/>).</summary>
    public Action? Restored { get; set; }
    /// <summary><c>/rubble</c>'s choice for this round; null = house_destruction.json's.</summary>
    bool? _rubbleStays;


    public HouseDestructionServer(ServerContext ctx, DestructionConfig cfg, bool arenaMode, string mapDir, Drycastle.MapLoader.MapLoader? map, Func<WorldQuery> query)
    {
        _ctx = ctx; _cfg = cfg; _map = map; _query = query;
        var grids = arenaMode || !cfg.Enabled || mapDir == "" ? Array.Empty<HouseGrid>() : Load(mapDir);
        _houses = grids.Select(g => new HouseCells(g)).ToArray();
        _bounds = grids.Select(Bounds).ToArray();
        _dirty = new bool[_houses.Length];
        _colliders = new HouseVoxelCollider?[_houses.Length];
        _noCollider = new bool[_houses.Length];
        _unsettledSince = new uint?[_houses.Length];
        _cutTick = new uint[_houses.Length];
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
            if (CarveOp.From(_houses[i].Grid, map, radiusM) is not { } op) continue;
            ApplyOp(i, op);
            reached++;
        }
        return reached;
    }

    /// <summary>Applies <paramref name="op"/> to house <paramref name="i"/> and sends it to everyone.</summary>
    void ApplyOp(int i, CarveOp op)
    {
        var house = _houses[i];
        var result = house.Apply(op, _cfg);
        _ctx.Broadcast(EventKind.HouseCarve, HouseCodec.EncodeCarve(new HouseCarveMsg(house.Grid.Index, house.OpSeq, op, result.Hash))); // every op, even one that took nothing: the clients count them too
        if (result.Carved.Length > 0) _unsettledSince[i] = null; // it ran the support pass
        MarkChanged(i, result);
    }

    // ---- rams ----

    readonly List<PloughCell> _cut = new();
    readonly List<int> _near = new();
    readonly List<int> _group = new();
    /// <summary>Per house: the tick of its first cut since its last support pass (null: settled), and the tick of its last cut.</summary>
    readonly uint?[] _unsettledSince;
    readonly uint[] _cutTick;

    /// <summary>
    /// A driven vehicle's drive step (<see cref="HousePlough"/>, house_destruction.json <c>ram</c>): the cells it sweeps next are cut, one op (254) for each house,
    /// before it moves, and the speed they took is returned. The colliders follow at the end of the tick (<see cref="Flush"/>), a step before it gets there.
    /// </summary>
    public float Plough(in PloughQuery q)
    {
        var rc = _cfg.Ram;
        if (!On || !rc.Enabled || q.Speed < rc.KeepSpeed) return 0;
        var plan = PlanNear(q);
        if (plan.Count == 0) return 0;
        ApplyCut(q.Car);
        return plan.Loss;
    }

    PloughPlan PlanNear(in PloughQuery q)
    {
        var rc = _cfg.Ram;
        var centre = HousePlough.Box(q, rc.MarginM).Centre;
        var reach = HousePlough.ReachRadius(q, rc);
        _near.Clear();
        for (var i = 0; i < _houses.Length; i++)
        {
            var (c, r) = _bounds[i];
            if (NVec3.DistanceSquared(c, centre) <= (r + reach) * (r + reach)) _near.Add(i);
        }
        if (_near.Count == 0) { _cut.Clear(); return default; }
        return HousePlough.Plan(q, _cfg, _near, i => _houses[i], (h, i) => _houses[h].Solid(i), _cut);
    }

    /// <summary>The planned cells (<see cref="_cut"/>, by house then cell) cut and sent, one op a house; each one's support pass waits (<see cref="Flush"/>) unless it is due.</summary>
    void ApplyCut(ushort car)
    {
        var tick = _ctx.Tick;
        for (var k = 0; k < _cut.Count;)
        {
            var h = _cut[k].House;
            _group.Clear();
            for (; k < _cut.Count && _cut[k].House == h; k++) _group.Add(_cut[k].Cell);
            var settle = _unsettledSince[h] is { } since && tick - since >= _cfg.Ram.SettleTicks;
            SendCut(h, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_group), settle, car);
            _unsettledSince[h] = settle ? null : _unsettledSince[h] ?? tick;
            _cutTick[h] = tick;
        }
    }

    void SendCut(int i, ReadOnlySpan<int> cells, bool settle, ushort car)
    {
        var house = _houses[i];
        var result = house.Cut(cells, settle, _cfg);
        _ctx.Broadcast(EventKind.HouseCut, HouseCodec.EncodeCut(new HouseCutMsg(house.Grid.Index, house.OpSeq, result.Hash, settle, _ctx.Tick, car, result.Carved)));
        MarkChanged(i, result);
    }

    void MarkChanged(int i, CarveResult result)
    {
        if (result.Carved.Length == 0 && result.Crumbled.Length == 0 && result.Fallen.Count == 0) return;
        _dirty[i] = true;
        ColliderOf(i)?.Mark(result);
        Changed?.Invoke(_houses[i]);
    }

    /// <summary>
    /// A vehicle that is not driven by the drive model (an aircraft) lost <see cref="ServerCar.LastImpact"/> m/s to something this tick, from
    /// <see cref="ServerCar.LastImpactVelocity"/>: below <c>ram.min_speed</c>, or no hollow house cell within <c>ram.probe_m</c> of its box along that velocity,
    /// null (a crash like any). Else the cells its box sweeps are cut as a driven vehicle's would be, with the momentum the wall took (mass times that speed):
    /// all of them, it goes on slowed by them and the speed that cost is returned; short of it, what it could afford is cut and it stops (null).
    /// A driven vehicle cuts in its drive step (<see cref="Plough"/>) and never stops at a house.
    /// </summary>
    public float? Ram(ServerCar car)
    {
        var rc = _cfg.Ram;
        if (!On || !rc.Enabled || car.Drives || car.LastImpact < rc.MinSpeed) return null;
        var v = car.LastImpactVelocity;
        var speed = v.Length();
        if (speed < 1e-3f) return null;
        var s = car.ContactShape();
        var mass = car.BumpBody().Mass;
        var q = new PloughQuery(s.Feet, s.Yaw, v / speed * (rc.ProbeM / HousePlough.LookaheadSteps), car.LastImpact, mass, s.HalfWidth, s.HalfLength, s.Height, 0f, Car: car.Id);
        var plan = PlanNear(q);
        if (plan.Count == 0) return null;
        ApplyCut(car.Id);
        if (!plan.Through) return null;
        var kept = MathF.Max(0, speed - plan.Loss);
        car.Rammed(v * (kept / speed));
        return plan.Loss;
    }

    /// <summary>
    /// The end of the tick: a house a plough stopped cutting this tick gets its support pass (a settle-only op, 254 with no cells), then every house that
    /// changed gets its new collider, each once.
    /// </summary>
    public void Flush()
    {
        for (var i = 0; i < _houses.Length; i++)
        {
            if (_unsettledSince[i] == null || _cutTick[i] == _ctx.Tick) continue;
            _unsettledSince[i] = null;
            SendCut(i, [], true, 0);
        }
        for (var i = 0; i < _houses.Length; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            RebuildCollider(i);
        }
    }

    // ---- the server's collider ----

    /// <summary>
    /// The house's collider follows its live cells (<see cref="HouseVoxelCollider"/>, the same as every client's): the changed columns of voxel trimeshes, or its
    /// exact shell back once it is whole. They sit in the house's body, so <see cref="WorldQuery"/> and the splash's line of sight see the holes with no change.
    /// </summary>
    void RebuildCollider(int i) => ColliderOf(i)?.Update(_houses[i], HouseVoxelCollider.MapToWorld(_map!.Root));

    /// <summary>
    /// The collider of house <paramref name="i"/> on its collision node (MapLoader's <c>HouseNodes</c>, Contract 2). A node freed by a reload is looked up again (its columns went with it):
    /// every column is rebuilt.
    /// </summary>
    HouseVoxelCollider? ColliderOf(int i)
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
        var col = _colliders[i] = new HouseVoxelCollider(_houses[i].Grid, cs);
        col.MarkAll();
        return col;
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
            _unsettledSince[i] = null;
        }
        _ctx.Broadcast(EventKind.HouseSettings, HouseCodec.EncodeSettings(new HouseSettingsMsg(RubbleStays, Reset: true)));
        Restored?.Invoke();
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
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} restored every house.");
    }

    /// <summary>"/rubble [fade|stay]": the rubble mode for the rest of the round (D8), sent to everyone; no argument tells it.</summary>
    public void ChatRubble(ServerClient c, IReadOnlyList<string> args)
    {
        if (args.Count == 0) { _ctx.ChatTo(c, ChatLineKind.Info, $"Rubble {(RubbleStays ? "stays" : "fades")} this round."); return; }
        _rubbleStays = args[0] == "stay";
        if (On) _ctx.Broadcast(EventKind.HouseSettings, HouseCodec.EncodeSettings(new HouseSettingsMsg(RubbleStays, Reset: false)));
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} set rubble to {(RubbleStays ? "stay" : "fade")} for this round.");
    }
}
