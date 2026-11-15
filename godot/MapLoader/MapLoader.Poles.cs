using Godot;
using Num = System.Numerics;

namespace Drycastle.MapLoader;

/// <summary>A placed utility pole that carries wires: its object id, where its column stands (the foot, map space), how tall it is, and the highest wire seat on it (null: no seat is known).</summary>
public readonly record struct PoleSite(string ObjectId, Num.Vector3 Foot, float Height, Num.Vector3? TopSeat);

// Poles as the Thieves event needs them (docs/events/thieves/07): which poles carry wires, how tall they are and where their wire seats are now. Reads the placed objects and the cables;
// builds nothing, so it works on the headless server (CollisionOnly) too, where the object nodes exist but the wires are not drawn.
public sealed partial class MapLoader
{
    /// <summary>The assets of the placed poles: <c>prim:pole_*</c>.</summary>
    const string PoleAssetPrefix = "prim:pole_";

    bool IsPoleObject(string id) => _objectAssets.TryGetValue(id, out var a) && a.StartsWith(PoleAssetPrefix, StringComparison.Ordinal) && _objectNodes.ContainsKey(id);

    /// <summary>
    /// Every placed pole that has wires: both ends of each cable between two placed poles, ordered by object id (so the order is the same every run). Height = the asset's height times the object's scale;
    /// the top seat is the highest wire seat of the pole as it stands.
    /// </summary>
    public List<PoleSite> PoleSites()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in _cables)
            if (IsPoleObject(c.From) && IsPoleObject(c.To)) { ids.Add(c.From); ids.Add(c.To); }
        var sites = new List<PoleSite>();
        if (_assetManifest == null) ReadAssetManifest();
        foreach (var id in ids)
        {
            var node = _objectNodes[id];
            if (!GodotObject.IsInstanceValid(node) || !_assetManifest!.TryGetValue(_objectAssets[id], out var entry)) continue;
            var foot = node.Position;
            Num.Vector3? top = null;
            foreach (var s in SeatsOf(id))
                if (top == null || s.Pos.Y > top.Value.Y) top = s.Pos;
            sites.Add(new PoleSite(id, new Num.Vector3(foot.X, foot.Y, foot.Z), entry.Size.Y * node.Scale.Y, top));
        }
        return sites;
    }

    /// <summary>
    /// The wire seats (map space) of the poles whose column stands within <paramref name="radius"/> (flat) of <paramref name="at"/>: the lines a man at the top of one is shocked through. The client reads it
    /// when a Thief is electrocuted; empty when no pole is near.
    /// </summary>
    public List<Num.Vector3> WireSeatsNear(Num.Vector3 at, float radius)
    {
        var seats = new List<Num.Vector3>();
        foreach (var (id, node) in _objectNodes)
        {
            if (!GodotObject.IsInstanceValid(node) || !IsPoleObject(id)) continue;
            var dx = node.Position.X - at.X; var dz = node.Position.Z - at.Z;
            if (dx * dx + dz * dz > radius * radius) continue;
            foreach (var s in SeatsOf(id)) seats.Add(s.Pos);
        }
        return seats;
    }
}
