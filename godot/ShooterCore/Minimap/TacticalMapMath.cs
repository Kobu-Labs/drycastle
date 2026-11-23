using System.Numerics;

namespace Drycastle.ShooterCore.Minimap;

/// <summary>
/// Godot-free geometry of the tactical map (the full-screen map sheet, opened with M). The sheet is north-up and fixed. Its
/// square extent covers the whole terrain (the world is a polygon, the square is its bounding box): <c>originX</c> is the ground x of the west edge and
/// <c>southZ</c> the ground z of the south edge (north is -z). Map coordinates are metres from the south-west corner: easting
/// grows east, northing grows north. Grid references work like a printed map sheet: columns A, B, C... from the west, rows
/// 1, 2, 3... from the south, so "C7" is the third column and seventh row.
/// </summary>
public static class TacticalMapMath
{
    /// <summary>The grid cell sizes in metres, smallest first. The cell is the first that gives at most <see cref="MaxCells"/> per side.</summary>
    public static readonly float[] CellSizes = { 25f, 50f, 100f, 250f, 500f, 1000f };

    /// <summary>Most cells per side the grid may have; the sizes above give roughly 16 to 40 for a map of a few hundred metres to a few kilometres.</summary>
    public const int MaxCells = 40;

    /// <summary>The grid cell size in metres for a square map of <paramref name="extentMetres"/>: the smallest of <see cref="CellSizes"/> that keeps the grid to <see cref="MaxCells"/> cells a side (the largest when none does, the smallest for a tiny map).</summary>
    public static float CellSize(float extentMetres)
    {
        foreach (var c in CellSizes)
            if (Cells(extentMetres, c) <= MaxCells) return c;
        return CellSizes[^1];
    }

    /// <summary>How many cells cover <paramref name="extentMetres"/>; the last one may be cut short by the map's edge. At least one.</summary>
    public static int Cells(float extentMetres, float cellMetres) =>
        cellMetres > 0 ? Math.Max(1, (int)MathF.Ceiling(extentMetres / cellMetres - 1e-4f)) : 1;

    /// <summary>Metres of padding the tactical map's sheet leaves round the terrain and the parcels.</summary>
    public const float SheetPad = 10f;

    /// <summary>
    /// The square sheet of the world map (the tactical map, <c>MapBase.World</c>) over the box (<paramref name="minX"/>, <paramref name="minZ"/>) to (<paramref name="maxX"/>, <paramref name="maxZ"/>) of ground
    /// metres (the parcels' box merged with the terrain's): its side is the longer side of the box plus the padding both ways, centred on the box. <c>Origin</c> is the ground (x, z) of the north-west corner;
    /// the south edge is <c>Origin.Y + Side</c>. The server asks for the same sheet so it measures the same grid cells the clients draw.
    /// </summary>
    public static (Vector2 Origin, float Side) Sheet(float minX, float minZ, float maxX, float maxZ)
    {
        var side = MathF.Max(maxX - minX, maxZ - minZ) + 2 * SheetPad;
        var centre = new Vector2((minX + maxX) / 2f, (minZ + maxZ) / 2f);
        return (centre - new Vector2(side / 2f, side / 2f), side);
    }

    /// <summary>Column label for a zero-based column: A..Z, then AA, AB, ...</summary>
    public static string ColumnLabel(int column)
    {
        if (column < 0) column = 0;
        var s = "";
        for (var n = column + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    /// <summary>Row label for a zero-based row: 1, 2, 3, ... from the south.</summary>
    public static string RowLabel(int row) => (Math.Max(0, row) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Metres from the south-west corner (easting, northing) of a ground point (x, z).</summary>
    public static Vector2 ToMap(Vector2 groundXZ, float originX, float southZ) => new(groundXZ.X - originX, southZ - groundXZ.Y);

    /// <summary>The ground point (x, z) of a map position (easting, northing). The inverse of <see cref="ToMap"/>.</summary>
    public static Vector2 ToGround(Vector2 map, float originX, float southZ) => new(map.X + originX, southZ - map.Y);

    /// <summary>
    /// Fraction of the sheet from its west edge (x) and its north edge (y, down), for drawing: (0, 0) is the top-left corner of
    /// the square map, (1, 1) the bottom-right. Not clamped, so a point off the map lands outside 0..1.
    /// </summary>
    public static Vector2 ToSheet(Vector2 groundXZ, float originX, float southZ, float extentMetres) =>
        extentMetres > 0 ? new Vector2((groundXZ.X - originX) / extentMetres, 1f - (southZ - groundXZ.Y) / extentMetres) : Vector2.Zero;

    /// <summary>The ground point (x, z) at a fraction of the sheet (from its west edge, from its north edge down). The inverse of <see cref="ToSheet"/>.</summary>
    public static Vector2 FromSheet(Vector2 fraction, float originX, float southZ, float extentMetres) =>
        new(originX + fraction.X * extentMetres, southZ - (1f - fraction.Y) * extentMetres);

    /// <summary>The zero-based (column, row) of a map position, clamped onto the grid so a player on the edge, or just off it, still has a reference.</summary>
    public static (int Column, int Row) CellOf(Vector2 map, float extentMetres, float cellMetres)
    {
        var n = Cells(extentMetres, cellMetres);
        var col = (int)MathF.Floor(map.X / cellMetres);
        var row = (int)MathF.Floor(map.Y / cellMetres);
        return (Math.Clamp(col, 0, n - 1), Math.Clamp(row, 0, n - 1));
    }

    /// <summary>The short grid reference of a cell, such as "C7".</summary>
    public static string Reference(int column, int row) => ColumnLabel(column) + RowLabel(row);

    /// <summary>The grid reference of a ground point (x, z) on the sheet of <paramref name="extentMetres"/> (its west edge at <paramref name="originX"/>, its south edge at <paramref name="southZ"/>), with the cell size the sheet itself uses.</summary>
    public static string ReferenceOf(Vector2 groundXZ, float originX, float southZ, float extentMetres)
    {
        var (column, row) = CellOf(ToMap(groundXZ, originX, southZ), extentMetres, CellSize(extentMetres));
        return Reference(column, row);
    }

    /// <summary>The player's heading in whole degrees, 0 to 359, clockwise from north (north 0, east 90), for a yaw in the game's convention (forward = (-sin yaw, -cos yaw) in ground x/z).</summary>
    public static int HeadingDegrees(float yaw)
    {
        var deg = (int)MathF.Round(HeadingRadians(yaw) * 180f / MathF.PI);
        return deg >= 360 ? 0 : deg;
    }

    /// <summary>The heading in radians in [0, 2 pi), clockwise from north. It is also the screen rotation of an arrow drawn pointing up on a north-up sheet.</summary>
    public static float HeadingRadians(float yaw)
    {
        const float twoPi = MathF.PI * 2f;
        var h = -yaw % twoPi;
        if (h < 0) h += twoPi;
        return h >= twoPi ? 0f : h;
    }

    /// <summary>Eight-point compass name index of a heading in degrees: 0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW.</summary>
    public static int Octant(int headingDegrees) => (int)MathF.Floor((((headingDegrees % 360) + 360) % 360 + 22.5f) / 45f) % 8;

    /// <summary>
    /// The length in metres of a scale bar that is as long as possible without passing <paramref name="maxPixels"/> on screen: a
    /// round 1, 2 or 5 times a power of ten. Zero when nothing fits or the input is not positive.
    /// </summary>
    public static float ScaleBarMetres(float pxPerMetre, float maxPixels)
    {
        if (pxPerMetre <= 0f || maxPixels <= 0f) return 0f;
        var maxMetres = maxPixels / pxPerMetre;
        var pow = MathF.Pow(10f, MathF.Floor(MathF.Log10(maxMetres)));
        foreach (var m in new[] { 5f, 2f, 1f })
            if (m * pow <= maxMetres) return m * pow;
        return 0f;
    }
}

/// <summary>Where the tactical map's pieces go on the screen, in HUD units (the HUD root's own pixels, before the UI scale).</summary>
public static class TacticalMapLayout
{
    /// <summary>
    /// The side of the square map. It takes the screen height less the margins and the label gutters, but gives way when the
    /// screen is too narrow to keep <paramref name="sidePanel"/> free on each side of the sheet. Never under <paramref name="minSide"/>.
    /// </summary>
    public static float Side(float width, float height, float margin, float gutter, float sidePanel, float panelGap, float minSide = 160f)
    {
        var byHeight = height - 2f * margin - 2f * gutter;
        var byWidth = width - 2f * (margin + sidePanel + panelGap) - 2f * gutter;
        return Math.Max(minSide, Math.Min(byHeight, byWidth));
    }

    /// <summary>The top-left corner of a <paramref name="side"/> square centred in the screen.</summary>
    public static Vector2 Origin(float width, float height, float side) => new((width - side) / 2f, (height - side) / 2f);
}
