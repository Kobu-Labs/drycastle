using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>One greedy face of the mesher, in grid units: a corner, its two edges, the outward normal and the palette entry (its colour).</summary>
public readonly record struct Quad(Vector3 Corner, Vector3 U, Vector3 V, Vector3 Normal, byte Palette);