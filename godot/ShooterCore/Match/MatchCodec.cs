using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Drycastle.ShooterCore;

/// <param name="Sandbox">A sandbox player (no team, but playing: not "unassigned"). Bit 2 of the row's team byte (#312).</param>
/// <param name="Bot">The player is a bot. Bit 3 of the team byte.</param>
/// <param name="Squad">The player's squad (#379), 0 for none; its name is in <see cref="MatchStateMsg.Squads"/>. Its own byte per row in the squad tail.</param>
public readonly record struct ScoreRow(ushort Id, string Name, int Kills, int Deaths, ushort Ping, byte Team = Teams.None, bool Sandbox = false, bool Bot = false, byte Squad = 0);

/// <param name="Mode">Free-for-all or team deathmatch; in team deathmatch <paramref name="ScoreA"/> and <paramref name="ScoreB"/> are the team totals and each row carries its team.</param>
/// <param name="Squads">The squads that have a member (null: none, as from an older server).</param>
public sealed record MatchStateMsg(MatchPhase Phase, float TimeRemaining, IReadOnlyList<ushort> Winners, IReadOnlyList<ScoreRow> Rows,
    MatchMode Mode = MatchMode.FreeForAll, int ScoreA = 0, int ScoreB = 0, IReadOnlyList<SquadName>? Squads = null);
/// <summary>A kill for the feed. <paramref name="Cause"/> travels as an optional sixth byte (old 5-byte messages decode as Standard).</summary>
public sealed record KillMsg(ushort Killer, ushort Victim, byte Weapon, KillCause Cause = KillCause.Standard);
public sealed record SpawnMsg(ushort Id, Vector3 Position, float Protection);
public sealed record DeathMsg(ushort Victim, ushort Killer, float RespawnDelay)
{
    /// <summary><see cref="RespawnDelay"/> of a player an admin took out of the round (#312): no countdown, they wait to be put on a team.</summary>
    public const float WaitsForAdmin = -1f;
}

/// <summary>
/// Payload codecs for the match event kinds (little-endian), carried in the reliable event envelope.
///   MatchState: u8 phase, f32 timeRemaining, u8 nWinners, u16*n ids, u8 nRows, rows (u16 id, u8 nameLen, name, u16 kills, u16 deaths, u16 ping)
///     then an optional team tail (older messages end after the rows): u8 mode, u16 scoreA, u16 scoreB, u8 per row in row order (bits 0-1 team, bit 2 sandbox, bit 3 bot)
///       then an optional squad tail (#379; older messages end after the team tail): u8 squad per row in row order, u8 nSquads, squads (u8 id, u8 nameLen, name)
///   Kill: u16 killer, u16 victim, u8 weapon          (killer == victim: suicide)
///   Spawn: u16 id, f32 x, y, z, f32 protectionSeconds
///   Death: u16 victim, u16 killer, f32 respawnDelay
///   Debug (client to server, dev servers only): u8 command (1 = kill), u16 target
/// </summary>
public static class MatchCodec
{
    public const byte DebugKill = 1;
    const byte TeamMask = 3, SandboxBit = 4, BotBit = 8;
    /// <summary>A squad name is at most 32 characters (<see cref="Squads"/>); this many utf8 bytes is a safe cap.</summary>
    const int MaxSquadNameBytes = 120;

    static ScoreRow DecodeTeamByte(ScoreRow row, byte b) =>
        row with { Team = (b & TeamMask) is var tm and (Teams.A or Teams.B) ? (byte)tm : Teams.None, Sandbox = (b & SandboxBit) != 0, Bot = (b & BotBit) != 0 };

    /// <summary>u8 length, utf8; a message that ends inside the name is rejected (EndOfStreamException).</summary>
    static string ReadName(BinaryReader r)
    {
        var n = r.ReadByte();
        var b = r.ReadBytes(n);
        return b.Length == n ? Encoding.UTF8.GetString(b) : throw new EndOfStreamException();
    }

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
        w.Write((byte)m.Mode); w.Write((ushort)m.ScoreA); w.Write((ushort)m.ScoreB);
        foreach (var r in m.Rows) w.Write((byte)((r.Team & TeamMask) | (r.Sandbox ? SandboxBit : 0) | (r.Bot ? BotBit : 0)));
        foreach (var r in m.Rows) w.Write(r.Squad);
        var squads = (m.Squads ?? Array.Empty<SquadName>()).Take(byte.MaxValue).ToList();
        w.Write((byte)squads.Count);
        foreach (var q in squads)
        {
            var nb = Encoding.UTF8.GetBytes(q.Name);
            if (nb.Length > MaxSquadNameBytes) nb = nb[..MaxSquadNameBytes];
            w.Write(q.Id); w.Write((byte)nb.Length); w.Write(nb);
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
            var mode = MatchMode.FreeForAll; int scoreA = 0, scoreB = 0;
            List<SquadName>? squads = null;
            if (r.BaseStream.Position < r.BaseStream.Length) // the team tail; absent from older servers
            {
                mode = r.ReadByte() == (byte)MatchMode.TeamDeathmatch ? MatchMode.TeamDeathmatch : MatchMode.FreeForAll;
                scoreA = r.ReadUInt16(); scoreB = r.ReadUInt16();
                for (var i = 0; i < rows.Count; i++) rows[i] = DecodeTeamByte(rows[i], r.ReadByte());
                if (r.BaseStream.Position < r.BaseStream.Length) // the squad tail; absent from older servers
                {
                    for (var i = 0; i < rows.Count; i++) rows[i] = rows[i] with { Squad = r.ReadByte() };
                    squads = new List<SquadName>();
                    for (int i = 0, n = r.ReadByte(); i < n; i++) squads.Add(new SquadName(r.ReadByte(), ReadName(r)));
                }
            }
            return new MatchStateMsg(phase, t, winners, rows, mode, scoreA, scoreB, squads);
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
    public MatchMode Mode { get; private set; } = MatchMode.FreeForAll;
    public bool IsTeamMode => Mode == MatchMode.TeamDeathmatch;
    public int ScoreA { get; private set; }
    public int ScoreB { get; private set; }
    public int TeamScore(byte team) => team == Teams.A ? ScoreA : team == Teams.B ? ScoreB : 0;
    /// <summary>Team of a player on the scoreboard; <see cref="Teams.None"/> in free-for-all or for an unknown id.</summary>
    public byte TeamOf(ushort id)
    {
        foreach (var r in Rows) if (r.Id == id) return r.Team;
        return Teams.None;
    }
    /// <summary>The squads that have a member, as the last scoreboard listed them (#379).</summary>
    public IReadOnlyList<SquadName> Squads { get; private set; } = Array.Empty<SquadName>();
    /// <summary>Squad of a player on the scoreboard, 0 for none or an unknown id.</summary>
    public byte SquadOf(ushort id)
    {
        foreach (var r in Rows) if (r.Id == id) return r.Squad;
        return 0;
    }
    public string SquadNameOf(byte squad)
    {
        foreach (var q in Squads) if (q.Id == squad) return q.Name;
        return "";
    }
    /// <summary>Two different players in the same squad (a squad is within a team, so they are friends too).</summary>
    public bool AreSquadMates(ushort a, ushort b) => IsTeamMode && a != b && SquadOf(a) is var s && s != 0 && s == SquadOf(b);
    /// <summary>Two different players on the same real team (never in free-for-all).</summary>
    public bool AreFriends(ushort a, ushort b) => IsTeamMode && a != b && TeamOf(a) is var t && t != Teams.None && t == TeamOf(b);
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
    /// <summary>How many times a round started over: after the end screen, or by an admin's restart while it ran (see <see cref="RoundRestarted"/>).</summary>
    public int Restarts { get; private set; }
    double _timeAt;
    float _timeLeft;

    /// <summary>The one place the phase edge is detected (the first state message counts as an edge, so a view that has drawn nothing yet draws).</summary>
    Change<byte> _phaseSeen; // the enum's byte

    /// <summary>
    /// The phase changed: (from, to). <c>from</c> is null for the first state message of the connection, so a sound or banner that marks a change (round start,
    /// round end) can skip it: someone joining mid-round has not seen a round start. Raised after the store is updated.
    /// </summary>
    public event Action<MatchPhase?, MatchPhase>? PhaseChanged;

    /// <summary>
    /// A fresh round began over the old one: the end screen ended, or an admin restarted a running round (<see cref="Restarts"/> counts both). The kill feed is
    /// already cleared; the HUD clears its own copy here. The wire has no restart flag: see <see cref="IsRestart"/> for how a restart of a running round is told.
    /// </summary>
    public event Action? RoundRestarted;

    /// <summary>A running round's remaining time that jumps up by more than this many seconds is a restart (it only runs down); more than any latency difference between two messages.</summary>
    public const float RestartTimeJump = 3f;

    /// <summary>
    /// Whether <paramref name="m"/> starts a new round over what the store holds. After the end screen that is the phase change itself. An admin's restart of a
    /// running round (Running to Running) has no phase change and no flag on the wire, so it is read from what it resets: the time left jumps back up to the
    /// limit, and the score of someone who is still here falls (kills and deaths only ever rise within a round).
    /// </summary>
    bool IsRestart(MatchStateMsg m, double now)
    {
        if (Phase == MatchPhase.Ending && m.Phase == MatchPhase.Running) return true;
        if (Phase != MatchPhase.Running || m.Phase != MatchPhase.Running) return false;
        if (m.TimeRemaining > TimeRemaining(now) + RestartTimeJump) return true;
        foreach (var r in m.Rows)
            foreach (var old in Rows)
                if (old.Id == r.Id && (r.Kills < old.Kills || r.Deaths < old.Deaths)) return true;
        return false;
    }

    public float TimeRemaining(double now) => Math.Max(0f, _timeLeft - (float)(now - _timeAt));

    public bool Apply(EventKind kind, byte[] payload, double now)
    {
        switch (kind)
        {
            case EventKind.MatchState when MatchCodec.DecodeMatchState(payload) is { } m:
                var restarted = IsRestart(m, now);
                if (restarted) { Restarts++; KillFeed.Clear(); }
                if (Phase != MatchPhase.Ending && m.Phase == MatchPhase.Ending) RoundsEnded++;
                var hadPhase = _phaseSeen.HasValue;
                var changed = _phaseSeen.Update((byte)m.Phase, out var before);
                Phase = m.Phase; Rows = m.Rows; Winners = m.Winners; Mode = m.Mode; ScoreA = m.ScoreA; ScoreB = m.ScoreB; Squads = m.Squads ?? Array.Empty<SquadName>(); _timeLeft = m.TimeRemaining; _timeAt = now;
                if (changed) PhaseChanged?.Invoke(hadPhase ? (MatchPhase)before : null, m.Phase);
                if (restarted) RoundRestarted?.Invoke();
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
