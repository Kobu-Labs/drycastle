using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Drycastle.ShooterCore;

public readonly record struct ScoreRow(ushort Id, string Name, int Kills, int Deaths, ushort Ping);

public sealed record MatchStateMsg(MatchPhase Phase, float TimeRemaining, IReadOnlyList<ushort> Winners, IReadOnlyList<ScoreRow> Rows);
/// <summary>A kill for the feed. <paramref name="Cause"/> travels as an optional sixth byte (old 5-byte messages decode as Standard).</summary>
public sealed record KillMsg(ushort Killer, ushort Victim, byte Weapon, KillCause Cause = KillCause.Standard);
public sealed record SpawnMsg(ushort Id, Vector3 Position, float Protection);
public sealed record DeathMsg(ushort Victim, ushort Killer, float RespawnDelay);

/// <summary>
/// Payload codecs for the match event kinds (little-endian), carried in the reliable event envelope.
///   MatchState: u8 phase, f32 timeRemaining, u8 nWinners, u16*n ids, u8 nRows, rows (u16 id, u8 nameLen, name, u16 kills, u16 deaths, u16 ping)
///   Kill: u16 killer, u16 victim, u8 weapon          (killer == victim: suicide)
///   Spawn: u16 id, f32 x, y, z, f32 protectionSeconds
///   Death: u16 victim, u16 killer, f32 respawnDelay
///   Debug (client to server, dev servers only): u8 command (1 = kill), u16 target
/// </summary>
public static class MatchCodec
{
    public const byte DebugKill = 1;

    public static byte[] Encode(MatchStateMsg m)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)m.Phase); w.Write(m.TimeRemaining);
        w.Write((byte)m.Winners.Count);
        foreach (var id in m.Winners) w.Write(id);
        w.Write((byte)m.Rows.Count);
        foreach (var r in m.Rows)
        {
            var nb = Encoding.UTF8.GetBytes(r.Name);
            if (nb.Length > Wire.MaxNameBytes) nb = nb[..Wire.MaxNameBytes];
            w.Write(r.Id); w.Write((byte)nb.Length); w.Write(nb);
            w.Write((ushort)r.Kills); w.Write((ushort)r.Deaths); w.Write(r.Ping);
        }
        return ms.ToArray();
    }

    public static MatchStateMsg? DecodeMatchState(byte[] d)
    {
        try
        {
            var r = new BinaryReader(new MemoryStream(d));
            var phase = (MatchPhase)r.ReadByte();
            var t = r.ReadSingle();
            var winners = new List<ushort>();
            for (int i = 0, n = r.ReadByte(); i < n; i++) winners.Add(r.ReadUInt16());
            var rows = new List<ScoreRow>();
            for (int i = 0, n = r.ReadByte(); i < n; i++)
            {
                var id = r.ReadUInt16();
                var name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
                rows.Add(new ScoreRow(id, name, r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16()));
            }
            return new MatchStateMsg(phase, t, winners, rows);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException) { return null; }
    }

    public static byte[] Encode(KillMsg m)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Killer);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Victim);
        b[4] = m.Weapon;
        b[5] = (byte)m.Cause;
        return b;
    }

    public static KillMsg? DecodeKill(byte[] d) =>
        d.Length < 5 ? null : new KillMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), d[4],
            d.Length >= 6 && d[5] <= (byte)KillCause.Roadkill ? (KillCause)d[5] : KillCause.Standard);

    public static byte[] Encode(SpawnMsg m)
    {
        var b = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(2), m.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6), m.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(10), m.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(14), m.Protection);
        return b;
    }

    public static SpawnMsg? DecodeSpawn(byte[] d) => d.Length < 18 ? null : new SpawnMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d),
        new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(2)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(6)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(10))),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(14)));

    public static byte[] Encode(DeathMsg m)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Victim);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Killer);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), m.RespawnDelay);
        return b;
    }

    public static DeathMsg? DecodeDeath(byte[] d) => d.Length < 8 ? null : new DeathMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(4)));

    public static byte[] EncodeDebugKill(ushort target)
    {
        var b = new byte[3];
        b[0] = DebugKill;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), target);
        return b;
    }

    public static ushort? DecodeDebugKill(byte[] d) =>
        d.Length >= 3 && d[0] == DebugKill ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(1)) : null;
}

/// <summary>
/// Client side store for match events: phase, countdown, scoreboard, kill feed, own spawn/death. Feed it every
/// event envelope; time is the local clock in seconds. No UI: <see cref="ScoreboardText"/> is the debug print.
/// </summary>
public sealed class ClientMatchState
{
    public const int FeedSize = 8;

    public MatchPhase Phase { get; private set; } = MatchPhase.Waiting;
    public IReadOnlyList<ScoreRow> Rows { get; private set; } = Array.Empty<ScoreRow>();
    public IReadOnlyList<ushort> Winners { get; private set; } = Array.Empty<ushort>();
    public List<KillMsg> KillFeed { get; } = new();
    public Dictionary<ushort, SpawnMsg> LastSpawn { get; } = new();
    public Dictionary<ushort, DeathMsg> LastDeath { get; } = new();
    /// <summary>
    /// The spawn and the death of the event applied last, whoever it was for. The tables above are per player and keep the
    /// order in which players were first seen, so their last entry is not the newest event.
    /// </summary>
    public SpawnMsg? LatestSpawn { get; private set; }
    public DeathMsg? LatestDeath { get; private set; }
    public Dictionary<ushort, double> SpawnedAt { get; } = new();
    public Dictionary<ushort, double> DiedAt { get; } = new();
    public int RoundsEnded { get; private set; }
    public int Restarts { get; private set; }
    double _timeAt;
    float _timeLeft;

    public float TimeRemaining(double now) => Math.Max(0f, _timeLeft - (float)(now - _timeAt));

    public bool Apply(EventKind kind, byte[] payload, double now)
    {
        switch (kind)
        {
            case EventKind.MatchState when MatchCodec.DecodeMatchState(payload) is { } m:
                if (Phase == MatchPhase.Ending && m.Phase == MatchPhase.Running) { Restarts++; KillFeed.Clear(); }
                if (Phase != MatchPhase.Ending && m.Phase == MatchPhase.Ending) RoundsEnded++;
                Phase = m.Phase; Rows = m.Rows; Winners = m.Winners; _timeLeft = m.TimeRemaining; _timeAt = now;
                return true;
            case EventKind.Kill when MatchCodec.DecodeKill(payload) is { } k:
                KillFeed.Add(k);
                if (KillFeed.Count > FeedSize) KillFeed.RemoveAt(0);
                return true;
            case EventKind.Spawn when MatchCodec.DecodeSpawn(payload) is { } s:
                LastSpawn[s.Id] = s; SpawnedAt[s.Id] = now; LatestSpawn = s;
                return true;
            case EventKind.Death when MatchCodec.DecodeDeath(payload) is { } d:
                LastDeath[d.Victim] = d; DiedAt[d.Victim] = now; LatestDeath = d;
                return true;
        }
        return false;
    }

    public void Remove(ushort id)
    {
        LastSpawn.Remove(id); LastDeath.Remove(id); SpawnedAt.Remove(id); DiedAt.Remove(id);
    }

    readonly Dictionary<ushort, string> _targetNames = new();

    /// <summary>Target dummies are not on the scoreboard; their names come from the roster (#148).</summary>
    public void NameTarget(ushort id, string name) => _targetNames[id] = name;

    public string NameOf(ushort id) => Rows.FirstOrDefault(r => r.Id == id).Name ?? (_targetNames.TryGetValue(id, out var t) ? t : $"#{id}");

    public string ScoreboardText(double now)
    {
        var sb = new StringBuilder();
        sb.Append($"match {Phase} {TimeRemaining(now):F0}s");
        if (Phase == MatchPhase.Ending) sb.Append($" winner(s): {string.Join(", ", Winners.Select(NameOf))}");
        foreach (var r in Rows.OrderByDescending(r => r.Kills).ThenBy(r => r.Deaths).ThenBy(r => r.Id))
            sb.Append($" | {r.Name} {r.Kills}/{r.Deaths} {r.Ping}ms");
        return sb.ToString();
    }
}
