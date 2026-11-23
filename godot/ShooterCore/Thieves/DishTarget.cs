using System.Numerics;
using System.Text;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// The <c>loot.dish</c> sub-section of thieves.json. A dish is a placed object (<see cref="DishSource.AssetId"/>) on a house wall; the Thief climbs to it from a base on the ground in front of
/// the wall: <see cref="BaseOutM"/> out from the dish's origin along the way the wall faces, on the ground, snapped to the navigation mesh. A base that the mesh is not within
/// <see cref="SnapMaxM"/> (flat) and <see cref="SnapMaxDyM"/> (height) of is no base: the dish is no target. The Thief works with its feet <see cref="ReachBelowM"/> below the dish's origin,
/// but never less than <see cref="MinClimbM"/> above the base (a low dish is still climbed to).
/// </summary>
public sealed class DishLootConfig : LootKindConfig
{
    public DishLootConfig() { StealSeconds = 8f; }

    public float BaseOutM { get; set; } = 0.9f;
    public float SnapMaxM { get; set; } = 1.2f;
    public float SnapMaxDyM { get; set; } = 1f;
    public float ReachBelowM { get; set; } = 1.3f;
    public float MinClimbM { get; set; } = 0.4f;

    public override void Validate(string file, string kind)
    {
        base.Validate(file, kind);
        if (!(BaseOutM > 0) || !(SnapMaxM > 0) || !(SnapMaxDyM > 0) || !(ReachBelowM >= 0) || !(MinClimbM >= 0)) throw new DataFileException($"{file}: loot.dish base_out_m, snap_max_m and snap_max_dy_m must be positive, reach_below_m and min_climb_m not negative");
    }
}

/// <summary>One dish on a wall: the object's id (objects.json), where its origin is (Godot axes: x east, y up, z south; the origin is on the wall at the arm's root) and the way the wall faces (the object's rotation about Y in degrees).</summary>
public readonly record struct DishSite(string ObjectId, Vector3 Origin, float YawDeg)
{
    /// <summary>The unit horizontal direction out of the wall: the model's +X, turned by Godot's Y rotation.</summary>
    public Vector3 Out
    {
        get
        {
            var r = YawDeg * MathF.PI / 180f;
            return new Vector3(MathF.Cos(r), 0, -MathF.Sin(r));
        }
    }
}

/// <summary>The world a dish source needs (the server's adapter over the map and the navigation mesh): the dishes the map has, the ground and the mesh.</summary>
public interface IDishWorld
{
    /// <summary>Every dish of the map, in a fixed order (by object id, ordinal): a dish's source id is its index here. Empty when the map has none.</summary>
    IReadOnlyList<DishSite> Sites { get; }

    /// <summary>The ground height at (x, z), NaN when there is none.</summary>
    float GroundAt(float x, float z);

    /// <summary>The closest point of the navigation mesh to <paramref name="p"/>; false when there is no mesh (yet).</summary>
    bool TrySnap(Vector3 p, out Vector3 snapped);
}

/// <summary>
/// The dish target (docs/events/thieves/06): one target per dish object of the map. Id = the dish's index in <see cref="IDishWorld.Sites"/>. Taking one puts it in the taken set (<see cref="Taken"/>,
/// replicated by the server as <see cref="DishCodec"/>, the client hides the object's node); <see cref="Return"/> and <see cref="Reset"/> take it out. <see cref="TakenChanged"/> fires on every change.
/// The target's <see cref="StealTarget.WorkSpot"/> is the base, its <see cref="StealTarget.Top"/> straight above it. A dish whose base is off the navigation mesh is no target (and not a place for
/// the van). The dishes have no collision; they are not otherwise in the world.
/// </summary>
public sealed class DishSource : IStealSource
{
    /// <summary>The asset of a dish: the generated model (scripts/models/make_satellite_dish_model.py).</summary>
    public const string AssetId = "pack:drycastle-generated-models/satellite_dish";

    readonly DishLootConfig _cfg;
    readonly IDishWorld _world;
    readonly HashSet<int> _taken = new();
    // computed once a dish is known to stand right or wrong (the map and the mesh do not change); a dish asked while there is no mesh yet is not cached
    readonly Dictionary<int, StealTarget?> _targets = new();

    public DishSource(DishLootConfig cfg, IDishWorld world) { _cfg = cfg; _world = world; }

    public LootKind Kind => LootKind.Dish;

    /// <summary>The set of dishes taken, changed (a dish was taken or returned, or a reset emptied a set that was not empty).</summary>
    public event Action? TakenChanged;

    /// <summary>The object ids of the dishes taken, sorted (ordinal): what the wire carries.</summary>
    public IReadOnlyList<string> TakenObjectIds
    {
        get
        {
            var sites = _world.Sites;
            var ids = _taken.Where(i => i >= 0 && i < sites.Count).Select(i => sites[i].ObjectId).ToList();
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
    }

    public int TakenCount => _taken.Count;

    public bool IsTaken(int id) => _taken.Contains(id);

    /// <summary>The target of a dish, whatever its state, or null when its base is off the navigation mesh (or there is no mesh, no ground).</summary>
    StealTarget? Build(int id)
    {
        var sites = _world.Sites;
        if (id < 0 || id >= sites.Count) return null;
        if (_targets.TryGetValue(id, out var cached)) return cached;
        var s = sites[id];
        var o = s.Out;
        var bx = s.Origin.X + o.X * _cfg.BaseOutM;
        var bz = s.Origin.Z + o.Z * _cfg.BaseOutM;
        var gy = _world.GroundAt(bx, bz);
        if (float.IsNaN(gy)) { _targets[id] = null; return null; }
        var want = new Vector3(bx, gy, bz);
        if (!_world.TrySnap(want, out var snap)) return null; // no mesh yet: ask again later
        StealTarget? t = null;
        var flat = MathF.Sqrt((snap.X - want.X) * (snap.X - want.X) + (snap.Z - want.Z) * (snap.Z - want.Z));
        if (flat <= _cfg.SnapMaxM && MathF.Abs(snap.Y - want.Y) <= _cfg.SnapMaxDyM)
        {
            var topY = MathF.Max(s.Origin.Y - _cfg.ReachBelowM, snap.Y + _cfg.MinClimbM);
            t = new StealTarget(LootKind.Dish, id, s.Origin, snap, new Vector3(snap.X, topY, snap.Z));
        }
        _targets[id] = t;
        return t;
    }

    public void FreeNear(Vector3 near, float radius, List<StealTarget> into)
    {
        var n = _world.Sites.Count;
        for (var i = 0; i < n; i++)
        {
            if (_taken.Contains(i) || Build(i) is not { } t) continue;
            var dx = t.WorkSpot.X - near.X; var dz = t.WorkSpot.Z - near.Z;
            if (dx * dx + dz * dz <= radius * radius) into.Add(t);
        }
    }

    public void Places(List<Vector2> into)
    {
        var n = _world.Sites.Count;
        for (var i = 0; i < n; i++)
            if (!_taken.Contains(i) && Build(i) is { } t) into.Add(new Vector2(t.WorkSpot.X, t.WorkSpot.Z));
    }

    public bool TryFind(int id, out StealTarget target)
    {
        target = default;
        if (_taken.Contains(id) || Build(id) is not { } t) return false;
        target = t;
        return true;
    }

    public void Take(int id)
    {
        if (id >= 0 && id < _world.Sites.Count && _taken.Add(id)) TakenChanged?.Invoke();
    }

    public void Return(int id)
    {
        if (_taken.Remove(id)) TakenChanged?.Invoke();
    }

    public void Reset()
    {
        if (_taken.Count == 0) return;
        _taken.Clear();
        TakenChanged?.Invoke();
    }
}

/// <summary>
/// The dishes taken, as the wire carries them (<see cref="EventKind.DishTaken"/>, the whole set each time, also to a joiner): u16 count, then per dish u8 length and the object id (utf8). The client hides
/// the object node of each id and shows every other dish. An empty set (a round reset) is two bytes.
/// </summary>
public static class DishCodec
{
    public const int MaxIdBytes = 255;

    public static byte[] Encode(IReadOnlyList<string> objectIds)
    {
        var list = objectIds.Select(Encoding.UTF8.GetBytes).Where(b => b.Length is > 0 and <= MaxIdBytes).Take(ushort.MaxValue).ToList();
        var bytes = new List<byte> { (byte)list.Count, (byte)(list.Count >> 8) };
        foreach (var b in list) { bytes.Add((byte)b.Length); bytes.AddRange(b); }
        return bytes.ToArray();
    }

    /// <summary>The object ids, or null for a payload that is not one.</summary>
    public static List<string>? Decode(byte[] payload)
    {
        if (payload.Length < 2) return null;
        var count = payload[0] | (payload[1] << 8);
        var ids = new List<string>(count);
        var at = 2;
        for (var i = 0; i < count; i++)
        {
            if (at >= payload.Length) return null;
            var len = payload[at++];
            if (len == 0 || at + len > payload.Length) return null;
            ids.Add(Encoding.UTF8.GetString(payload, at, len));
            at += len;
        }
        return at == payload.Length ? ids : null;
    }
}
