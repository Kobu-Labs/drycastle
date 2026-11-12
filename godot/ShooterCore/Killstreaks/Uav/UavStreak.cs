using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>A living player the UAV could show: their ground position (x, z) and their perk's <c>uav_visibility</c> (0 = hidden).</summary>
public readonly record struct UavContact(ushort Id, float X, float Z, float Visibility);

/// <summary>One UAV pulse as the owner sees it: seconds left in the streak and the enemy ground positions (x, z).</summary>
public sealed record UavPulseInfo(float SecondsLeft, IReadOnlyList<Vector2> Contacts);

/// <summary>
/// Pure rules of the UAV streak (issue #121): which players it shows and when a pulse goes out. Godot-free.
/// </summary>
public static class UavRules
{
    /// <summary>How often the owner's minimap is refreshed while the UAV is up.</summary>
    public const double PulseSeconds = 1.0;

    /// <summary>
    /// The owner's enemies: everyone alive but the owner (FFA), minus anyone whose perk makes them invisible to UAVs
    /// (<c>uav_visibility</c> of 0, the Ghost perk). Other players never see this data.
    /// </summary>
    public static IReadOnlyList<Vector2> Visible(IEnumerable<UavContact> alive, ushort owner) =>
        alive.Where(c => c.Id != owner && c.Visibility > 0).Select(c => new Vector2(c.X, c.Z)).ToList();
}

/// <summary>
/// The UAV killstreak (issue #121). Activation shows the owner's enemies at once and then every
/// <see cref="UavRules.PulseSeconds"/> until the streak's <c>duration_seconds</c> (60 s in killstreaks.json) runs out. The
/// expiry, owner's death and round end are handled by <see cref="KillstreakService"/>, which ends the instance; this class
/// only sends the pulses, to the owner alone.
/// </summary>
public sealed class UavStreak : IKillstreak
{
    /// <summary>The killstreaks.json id.</summary>
    public const string StreakId = "uav";

    readonly Func<IReadOnlyList<UavContact>> _sample;
    readonly Action<ushort, EventKind, byte[]> _send;
    readonly List<(KillstreakUse Use, double Next)> _open = new();

    /// <param name="sample">The current living players and their perks (called at each pulse).</param>
    /// <param name="send">Sends a reliable event to one player (the owner).</param>
    public UavStreak(Func<IReadOnlyList<UavContact>> sample, Action<ushort, EventKind, byte[]> send)
    {
        _sample = sample;
        _send = send;
    }

    public string Id => StreakId;
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string>();

    public bool Activate(KillstreakUse use)
    {
        _open.Add((use, use.StartedAt + UavRules.PulseSeconds));
        use.OnEnded(_ => _open.RemoveAll(e => ReferenceEquals(e.Use, use)));
        Pulse(use, use.StartedAt);
        return true;
    }

    /// <summary>Sends the pulses that are due. The server calls it every frame with its match clock.</summary>
    public void Tick(double now)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var (use, next) = _open[i];
            if (!use.IsOpen || now < next) continue;
            Pulse(use, now);
            _open[i] = (use, now + UavRules.PulseSeconds);
        }
    }

    void Pulse(KillstreakUse use, double now)
    {
        var left = (float)Math.Max(0, use.Def.DurationSeconds - (now - use.StartedAt));
        var contacts = UavRules.Visible(_sample(), use.Owner);
        _send(use.Owner, EventKind.UavPing, UavEvents.Encode(new UavPulseInfo(left, contacts)));
    }
}

/// <summary>
/// Payload of the owner-only <see cref="EventKind.UavPing"/>: f32 seconds left, u16 contact count, then per contact
/// f32 x and f32 z (ground, metres). Decoding returns null on a short or malformed payload.
/// </summary>
public static class UavEvents
{
    const int Header = 4 + 2;
    const int PerContact = 8;

    public static byte[] Encode(UavPulseInfo p)
    {
        var count = Math.Min(p.Contacts.Count, ushort.MaxValue);
        var b = new byte[Header + count * PerContact];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0, 4), p.SecondsLeft);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4, 2), (ushort)count);
        for (var i = 0; i < count; i++)
        {
            var at = Header + i * PerContact;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at, 4), p.Contacts[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4, 4), p.Contacts[i].Y);
        }
        return b;
    }

    public static UavPulseInfo? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < Header) return null;
        var left = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(0, 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(4, 2));
        if (d.Length < Header + count * PerContact || !float.IsFinite(left)) return null;
        var list = new List<Vector2>(count);
        for (var i = 0; i < count; i++)
        {
            var at = Header + i * PerContact;
            list.Add(new Vector2(BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at, 4)), BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 4, 4))));
        }
        return new UavPulseInfo(left, list);
    }
}
