using Godot;

namespace Drycastle.MapLoader.Ground;

/// <summary>A hand-drawn ground patch, for the minimap: its type's colour and ring in ground (x, z) metres.</summary>
public sealed record PatchPaint(string Id, Color Colour, Vector2[] Ring, string Type = "");
