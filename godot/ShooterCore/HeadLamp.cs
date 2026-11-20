using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>
/// The head lamp (GLOSSARY "Head lamp"): a beam from a player's head, switched by the headlights key (H) while on foot. One bit per player, server-owned
/// and told to everyone (<see cref="EventKind.HeadLampState"/>), so the others see the beam. Off at spawn, on death and while in a vehicle (H works the car's
/// <see cref="Headlights"/> there). The numbers of the beam are all here.
/// </summary>
public static class HeadLamp
{
    /// <summary>Real spot lights per client (the nearest lit players, the local player first); the others show nothing.</summary>
    public const int MaxBeams = 3;

    /// <summary>Beam range in metres.</summary>
    public const float RangeM = 50f;

    /// <summary>Spot half angle in degrees.</summary>
    public const float AngleDeg = 38f;

    /// <summary>
    /// Godot's spot_angle_attenuation: intensity is 1 - rim^k with rim 0 on the axis and 1 at the border (rim is linear in 1 - cos angle), so a LOW k gives the
    /// softest border (the slope there is k) and a high k a hard rim; 1 is a straight ramp.
    /// </summary>
    public const float AngleAttenuation = 0.5f;

    /// <summary>Godot's spot_attenuation (the fall off along the range; 1 is Godot's default).</summary>
    public const float RangeAttenuation = 1f;

    /// <summary>Light energy; the night's ambient is very low (moon 0.08, ambient 0.04, see DayNightCycle).</summary>
    public const float Energy = 10f;

    /// <summary>Cool white (linear sRGB values 0..1).</summary>
    public const float ColourR = 0.85f, ColourG = 0.93f, ColourB = 1f;

    /// <summary>The lamp's state after one input: H on its press edge toggles it on foot and alive; in a vehicle or dead it is off.</summary>
    public static bool Next(bool on, bool pressEdge, bool onFoot, bool alive)
    {
        if (!onFoot || !alive) return false;
        return pressEdge ? !on : on;
    }

    /// <summary>Server to everyone, <see cref="EventKind.HeadLampState"/>: u16 player id, u8 1 on, 0 off.</summary>
    public static byte[] Encode(ushort player, bool on)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, player);
        b[2] = (byte)(on ? 1 : 0);
        return b;
    }

    public static bool TryDecode(byte[] payload, out ushort player, out bool on)
    {
        player = 0; on = false;
        if (payload.Length != 3 || payload[2] > 1) return false;
        player = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        on = payload[2] == 1;
        return true;
    }
}
