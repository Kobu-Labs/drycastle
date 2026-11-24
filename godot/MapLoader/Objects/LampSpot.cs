using Godot;

namespace Drycastle.MapLoader.Objects;

/// <summary>A street lamp from lamps.json: the foot of its pole on the ground, Godot axes (Y up).</summary>
public sealed record LampSpot(string Id, Vector3 Position, string Source);
