using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>One greedy face of the mesher, in grid units: a corner, its two edges, the outward normal and the palette entry (its colour).</summary>
public readonly record struct Quad(Vector3 Corner, Vector3 U, Vector3 V, Vector3 Normal, byte Palette);

/// <summary>Which faces <see cref="HouseMesher.Surface(HouseCells, MeshMode, List{Quad})"/> draws: only the cut faces next to a removed cell, or every face next to air.</summary>
public enum MeshMode { CutFaces, AllFaces }
