using System.Buffers.Binary;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>Who sits in each seat of one vehicle (index = seat, 0 = free). Sent to the riders when it changes.</summary>
public sealed record SeatState(ushort CarId, ushort[] Occupants);

/// <summary>
/// Wire format of the seat switch (<see cref="EventKind.SeatSwitch"/> and <see cref="EventKind.SeatState"/>). A request is one byte, the seat index
/// (key N asks for seat N - 1). A state is the vehicle's id (u16), the seat count (u8) and one player id (u16) per seat.
/// </summary>
public static class SeatCodec
{
    public static byte[] EncodeSwitch(int seat) => new[] { (byte)seat };

    /// <summary>The seat index of a request, or null for a payload of the wrong size or an index no vehicle has.</summary>
    public static int? DecodeSwitch(ReadOnlySpan<byte> d) => d.Length == 1 && d[0] < VehicleSeat.MaxSeats ? d[0] : null;

    public static byte[] EncodeState(ushort carId, IReadOnlyList<ushort> occupants)
    {
        var b = new byte[3 + 2 * occupants.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(b, carId);
        b[2] = (byte)occupants.Count;
        for (var i = 0; i < occupants.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3 + 2 * i), occupants[i]);
        return b;
    }

    public static SeatState? DecodeState(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3) return null;
        int n = d[2];
        if (n < 1 || n > VehicleSeat.MaxSeats || d.Length != 3 + 2 * n) return null;
        var occupants = new ushort[n];
        for (var i = 0; i < n; i++) occupants[i] = BinaryPrimitives.ReadUInt16LittleEndian(d[(3 + 2 * i)..]);
        return new SeatState(BinaryPrimitives.ReadUInt16LittleEndian(d), occupants);
    }
}

/// <summary>One seat on the schematic: its index (key number is <c>Index + 1</c>), role, and the centre of its marker in panel pixels (y grows downward).</summary>
public readonly record struct SeatMarker(int Index, string Role, float X, float Y)
{
    /// <summary>The number printed in the marker and the key that takes the seat.</summary>
    public int Number => Index + 1;
}

/// <summary>The seat schematic's geometry in panel pixels: the vehicle's bounding rectangle (forward is up), its outline polygon (null: the rectangle is the body) and a marker per seat.</summary>
public sealed record SeatLayout(float BodyLeft, float BodyTop, float BodyWidth, float BodyHeight, IReadOnlyList<SeatMarker> Seats, IReadOnlyList<(float X, float Y)>? Outline = null);

/// <summary>
/// The top-down seat schematic's layout (pure, so it is tested): the vehicle drawn to scale from its top-view outline (vehicle_outlines.json; without one its
/// hit box from half width and half length), forward up the panel,
/// each seat at its vehicle-frame offset (x to the right, z backward, so a forward seat is higher up). A vehicle without a <c>seats</c> list has the one
/// default driver seat, so it gets a single marker, number 1.
/// </summary>
public static class SeatSchematic
{
    /// <summary>
    /// Lays the vehicle out in a <paramref name="width"/> x <paramref name="height"/> panel, centred, as large as fits inside <paramref name="pad"/>.
    /// Seats keep their real positions on the outline; markers (radius <paramref name="markerRadius"/>) that would touch, such as a roof gunner above its driver, are pushed apart, then every
    /// marker is kept inside the body outline, so each number stays readable and no seat is hidden under another.
    /// </summary>
    public static SeatLayout Layout(VehicleDef def, float width, float height, float pad, float markerRadius, IReadOnlyList<float[]>? outline = null)
    {
        // the vehicle-frame box that is fitted into the panel: the outline's bounds, or the hit box centred on the origin
        float minX = -def.HalfWidth, maxX = def.HalfWidth, minZ = -def.HalfLength, maxZ = def.HalfLength;
        if (outline is { Count: >= 3 })
        {
            minX = outline.Min(p => p[0]); maxX = outline.Max(p => p[0]);
            minZ = outline.Min(p => p[1]); maxZ = outline.Max(p => p[1]);
        }
        else outline = null;
        float bw = maxX - minX, bh = maxZ - minZ, mx = (minX + maxX) / 2, mz = (minZ + maxZ) / 2;
        var scale = MathF.Min((width - 2 * pad) / bw, (height - 2 * pad) / bh);
        float cx = width / 2, cy = height / 2;
        float hx = bw / 2 * scale, hy = bh / 2 * scale;
        var n = def.Seats.Count;
        var x = new float[n]; var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            var o = def.Seats[i].Offset;
            x[i] = cx + (o[0] - mx) * scale;
            y[i] = cy + (o[2] - mz) * scale;
        }
        var min = 2 * markerRadius + 4;
        for (var pass = 0; pass < 12; pass++)
        {
            var moved = false;
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                {
                    float dx = x[j] - x[i], dy = y[j] - y[i];
                    var d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d >= min) continue;
                    // exactly on top of each other (a roof gunner above its driver): split along the vehicle, the lower seat number in front
                    if (d < 0.001f) { dx = 0; dy = 1; d = 1; }
                    // a body too narrow to split them sideways (a three-seat bench in a wide, long truck): split along the vehicle instead, the lower seat number in front
                    if (MathF.Abs(dx) > MathF.Abs(dy) && (cx + hx - markerRadius) - (cx - hx + markerRadius) < 2 * min) { dy = dy != 0 ? MathF.Sign(dy) : 1; dx = dx * 0.01f; d = MathF.Sqrt(dx * dx + dy * dy); }
                    var push = (min - d) / 2 + 0.01f;
                    x[i] -= dx / d * push; y[i] -= dy / d * push;
                    x[j] += dx / d * push; y[j] += dy / d * push;
                    moved = true;
                }
            for (var i = 0; i < n; i++)
            {
                x[i] = Clamp(x[i], cx - hx + markerRadius, cx + hx - markerRadius, cx);
                y[i] = Clamp(y[i], cy - hy + markerRadius, cy + hy - markerRadius, cy);
            }
            if (!moved) break;
        }
        var seats = new SeatMarker[n];
        for (var i = 0; i < n; i++) seats[i] = new SeatMarker(i, def.Seats[i].Role, x[i], y[i]);
        var poly = outline?.Select(p => (cx + (p[0] - mx) * scale, cy + (p[1] - mz) * scale)).ToArray();
        return new SeatLayout(cx - hx, cy - hy, 2 * hx, 2 * hy, seats, poly);
    }

    /// <summary>Clamps into [lo, hi]; a range turned inside out (a body narrower than a marker) gives the centre.</summary>
    static float Clamp(float v, float lo, float hi, float centre) => lo > hi ? centre : MathF.Min(MathF.Max(v, lo), hi);
}
