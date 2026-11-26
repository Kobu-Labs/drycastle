using System.Numerics;
using Drycastle.ShooterCore.Glass;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// A hollow house's openings and its cells (docs/house-destruction.md): the frame of a window pane or a doorway is the house's solid cells around its rectangle,
/// within <see cref="RimM"/> of its edge in the wall's plane and <see cref="DepthM"/> through the wall. Once a carve has taken one of them nothing holds what is
/// in the opening: a pane shatters (the server's glass), a door is blown out (<see cref="HouseDoors.Blow"/>).
/// </summary>
public static class HouseFrames
{
    public const float RimM = 0.3f, DepthM = 0.5f;

    /// <summary>A doorway's opening as a rectangle (Godot map local): from its foot up its height, facing out.</summary>
    public static GlassPaneRect Rect(Doorway way) => GlassPaneRect.Make(way.Pos + new Vector3(0, way.Height / 2, 0), way.Out, way.Width, way.Height);

    /// <summary>The frame cells of an opening given in Godot map local metres (windows.json's rectangle, as <see cref="MapGlass"/> reads it; <see cref="Rect"/>). Empty when it is not in this house's grid.</summary>
    public static int[] FrameCells(HouseGrid g, GlassPaneRect r)
    {
        var c = HouseFrame.GodotToGrid(g, r.Centre);
        var k = (int)MathF.Ceiling((MathF.Max(r.Width, r.Height) * 0.5f + RimM + DepthM) / g.Cell) + 1;
        int cx = (int)MathF.Floor(c.X), cy = (int)MathF.Floor(c.Y), cz = (int)MathF.Floor(c.Z);
        var pal = g.Cells.Span;
        var into = new List<int>();
        for (var z = Math.Max(cz - k, 0); z <= Math.Min(cz + k, g.Nz - 1); z++)
            for (var y = Math.Max(cy - k, 0); y <= Math.Min(cy + k, g.Ny - 1); y++)
                for (var x = Math.Max(cx - k, 0); x <= Math.Min(cx + k, g.Nx - 1); x++)
                {
                    var i = g.IndexOf(x, y, z);
                    if (pal[i] == 0) continue;
                    var d = HouseFrame.GridToGodot(g, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) - r.Centre;
                    if (MathF.Abs(Vector3.Dot(d, r.Right)) <= r.Width * 0.5f + RimM && MathF.Abs(Vector3.Dot(d, r.Up)) <= r.Height * 0.5f + RimM
                        && MathF.Abs(Vector3.Dot(d, r.Normal)) <= DepthM)
                        into.Add(i);
                }
        return into.ToArray();
    }

    /// <summary>Whether a carve has taken a cell of the frame.</summary>
    public static bool Unframed(HouseCells c, int[] frame)
    {
        foreach (var i in frame) if (!c.Solid(i)) return true;
        return false;
    }
}
