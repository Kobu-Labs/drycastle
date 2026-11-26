using System.Buffers.Binary;
using System.Text.Json;
using Godot;
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
    /// <summary>Most bytes of one <see cref="EventKind.HouseCells"/> payload (Contract 4); a longer mask goes as several.</summary>
    public const int MaxCellsPayload = 200;
    const int CellsHeader = 12;
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
    readonly List<Quad> _quads = new();
    /// <summary><c>/rubble</c>'s choice for this round; null = house_destruction.json's.</summary>
    bool? _rubbleStays;
    Dictionary<string, MeshInstance3D>? _collisionNodes;

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
    static Vector3 ToWorld(NVec3 m) => new(m.X, m.Z, -m.Y);

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
            _ctx.Broadcast(EventKind.HouseCarve, EncodeCarve(house.Grid.Index, house.OpSeq, op, result.Hash)); // every op, even one that took nothing: the clients count them too
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
    /// The house's collision node gets a trimesh of every face of its live cells (<see cref="MeshMode.AllFaces"/>), or its original exact shell back once it is whole.
    /// The node stays (its body, layers and place in the map), so <see cref="WorldQuery"/> and the splash's line of sight see the holes with no change.
    /// </summary>
    void RebuildCollider(int i)
    {
        if (ColliderOf(i) is not { } col) return;
        var house = _houses[i];
        if (!house.Damaged) { col.Node.Shape = col.Original; return; }
        _quads.Clear();
        HouseMesher.Surface(house, MeshMode.AllFaces, _quads);
        var toLocal = col.Node.GlobalTransform.AffineInverse();
        var g = house.Grid;
        var faces = new Vector3[_quads.Count * 6];
        var k = 0;
        foreach (var q in _quads)
        {
            var a = toLocal * ToWorld(g.ToMap(q.Corner));
            var b = toLocal * ToWorld(g.ToMap(q.Corner + q.U));
            var c = toLocal * ToWorld(g.ToMap(q.Corner + q.U + q.V));
            var d = toLocal * ToWorld(g.ToMap(q.Corner + q.V));
            faces[k++] = a; faces[k++] = b; faces[k++] = c;
            faces[k++] = a; faces[k++] = c; faces[k++] = d;
        }
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(faces);
        col.Node.Shape = shape;
    }

    /// <summary>
    /// The collision node of house <paramref name="i"/>: the CollisionShape3D MapLoader built under <c>house_&lt;bid&gt;_collision</c> (Contract 2), and its original shape.
    /// Found by node name in the loaded map for now; WP-F's <c>MapLoader.HouseNodes</c> replaces the lookup. A node the editor reloaded is looked up again.
    /// </summary>
    HouseCollider? ColliderOf(int i)
    {
        if (_colliders[i] is { } known && GodotObject.IsInstanceValid(known.Node)) return known;
        _colliders[i] = null;
        if (_map == null || _noCollider[i]) return null;
        var name = $"house_{_houses[i].Grid.Id}_collision";
        if (_collisionNodes == null || (_collisionNodes.TryGetValue(name, out var stale) && !GodotObject.IsInstanceValid(stale))) _collisionNodes = ScanCollisionNodes(_map.Root);
        var cs = _collisionNodes.TryGetValue(name, out var mi) ? mi.GetNodeOrNull<CollisionShape3D>("Body/Shape") : null;
        if (cs?.Shape == null)
        {
            _noCollider[i] = true;
            GD.PrintErr($"server: house {_houses[i].Grid.Id} has no {name} in the map; the server keeps its whole collider");
            return null;
        }
        return _colliders[i] = new HouseCollider(cs, cs.Shape);
    }

    static Dictionary<string, MeshInstance3D> ScanCollisionNodes(Node root)
    {
        var found = new Dictionary<string, MeshInstance3D>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is MeshInstance3D mi && n.Name.ToString() is var name && name.StartsWith("house_", StringComparison.Ordinal) && name.EndsWith("_collision", StringComparison.Ordinal)) found[name] = mi;
            foreach (var child in n.GetChildren()) stack.Push(child);
        }
        return found;
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
        _ctx.Broadcast(EventKind.HouseSettings, EncodeSettings(RubbleStays, reset: true));
    }

    /// <summary>The joiner's catch-up: the settings, then the removed cells of every house an op has touched this round (one whose op count is not 0, even with nothing removed: the joiner's count must match).</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (!On) return;
        _ctx.SendEvent(c, EventKind.HouseSettings, EncodeSettings(RubbleStays, reset: false));
        for (var i = 0; i < _houses.Length; i++)
            if (_houses[i].OpSeq != 0 || _houses[i].Damaged) SendCells(c, i);
    }

    /// <summary>A client's state of a house differs from the server's (u16 house): it gets the house's removed cells.</summary>
    public void OnResync(ServerClient c, byte[] payload)
    {
        if (!On || payload.Length != 2) return;
        var i = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (i < _houses.Length) SendCells(c, i);
    }

    void SendCells(ServerClient c, int i)
    {
        var h = _houses[i];
        foreach (var m in EncodeCells(h.Grid.Index, h.OpSeq, h.Hash(), h.EncodeRemoved())) _ctx.SendEvent(c, EventKind.HouseCells, m);
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
        if (On) _ctx.Broadcast(EventKind.HouseSettings, EncodeSettings(RubbleStays, reset: false));
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} set rubble to {(RubbleStays ? "stay" : "fade")} for this round.");
    }

    // ---- wire (Contract 4). shortcut: HouseCodec (WP-A) is empty at WP-0; these move there once it lands. ----

    /// <summary>242: u16 house, u16 op seq (after), i16 cx, cy, cz, u16 radius (quarter-cells), u32 hash after Settle. Little-endian.</summary>
    internal static byte[] EncodeCarve(ushort house, ushort opSeq, CarveOp op, uint hash)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(b, house);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), opSeq);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), op.Cx);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), op.Cy);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), op.Cz);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), op.RadiusQ);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), hash);
        return b;
    }

    /// <summary>244: u8 flags (bit 0 rubble stays), u8 reset (1 = every house whole).</summary>
    internal static byte[] EncodeSettings(bool rubbleStays, bool reset) => new[] { rubbleStays ? (byte)1 : (byte)0, reset ? (byte)1 : (byte)0 };

    /// <summary>
    /// 243: u16 house, u16 op seq, u32 hash, u32 first run (the index of this message's first run in the whole list), then whole varint runs, at most
    /// <see cref="MaxCellsPayload"/> bytes a message. No runs at all is one message with none.
    /// </summary>
    internal static List<byte[]> EncodeCells(ushort house, ushort opSeq, uint hash, ReadOnlySpan<byte> runs)
    {
        var messages = new List<byte[]>();
        const int room = MaxCellsPayload - CellsHeader;
        int p = 0, run = 0;
        do
        {
            int start = p, firstRun = run, end = p;
            while (end < runs.Length)
            {
                var q = end;
                while (q < runs.Length && runs[q] >= 0x80) q++;
                q++; // past the varint's last byte
                if (q - start > room) break;
                end = q;
                run++;
            }
            var m = new byte[CellsHeader + end - start];
            BinaryPrimitives.WriteUInt16LittleEndian(m, house);
            BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(2), opSeq);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(4), hash);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), (uint)firstRun);
            runs[start..end].CopyTo(m.AsSpan(CellsHeader));
            messages.Add(m);
            p = end;
        } while (p < runs.Length);
        return messages;
    }
}
