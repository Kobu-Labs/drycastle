using Num = System.Numerics;

namespace Drycastle.MapLoader.Objects;

/// <summary>A placed utility pole that carries wires: its object id, where its column stands (the foot, map space), how tall it is, and the highest wire seat on it (null: no seat is known).</summary>
public readonly record struct PoleSite(string ObjectId, Num.Vector3 Foot, float Height, Num.Vector3? TopSeat);
