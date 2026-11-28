using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Mining;

/// <summary>One stored find (GLOSSARY "Find"): <see cref="PlayerId"/> (a stored profile) found <see cref="MineralId"/> at <see cref="AtUtc"/>. A player has at most one per mineral.</summary>
public sealed record MineralFind(long PlayerId, string MineralId, DateTime AtUtc);

/// <summary>
/// A crystal in the world that holds a mineral (GLOSSARY "Mineral node"). <see cref="Id"/> is its place in the server's node list. The node never goes away: whether it can be
/// taken now is its mineral's own rule (<see cref="MineralActivity"/>), and it stays in the world for a player who already has the mineral (they just cannot take it again).
/// </summary>
public sealed record MineralNode(byte Id, string MineralId, Vector3 Position);

/// <summary>What the world is like for the rules of <see cref="MineralActivity"/>. Grows with the minerals that need it (a living phoenix for Pheonixite). <paramref name="HolyMassPeaceful"/>: a Holy Mass was completed with no pastor killed (<see cref="Citizens.HolyMassEvent.Peaceful"/>), for Holystone. <paramref name="Storm"/>: a thunderstorm is on (<see cref="Scenery.WeatherMachine.Storm"/>), for Thundernite.</summary>
public readonly record struct MineralWorld(bool HolyMassPeaceful = false, bool Storm = false);

/// <summary>
/// When a mineral can be taken: every mineral brings its own criteria (Thundernite only in a storm, Pheonixite once a phoenix drops it), so there is no global respawn rule.
/// While a mineral is active any number of players can take it; a mineral with no entry here is always active.
/// </summary>
public static class MineralActivity
{
    static readonly Dictionary<string, Func<MineralWorld, bool>> Rules = new()
    {
        ["holystone"] = w => w.HolyMassPeaceful,
        ["thundernite"] = w => w.Storm,
    };

    /// <summary>Whether a node of this mineral can be taken now.</summary>
    public static bool IsActive(string mineralId, MineralWorld world) => !Rules.TryGetValue(mineralId, out var rule) || rule(world);
}

/// <summary>How an attempt to take a mineral ended.</summary>
public enum MineralPickResult : byte { Ok, UnknownNode, TooFar, Inactive, AlreadyFound, NotStored }

/// <summary>The constants of taking a mineral and the codecs of its events (kinds 111-113).</summary>
public static class MineralRules
{
    /// <summary>Seconds the interact key is held at a node in reach to take its mineral (the client holds, the server trusts the send, as with the care package).</summary>
    public const float HoldSeconds = 1.5f;
    /// <summary>How near (metres, flat) a player must be to a node.</summary>
    public const float ReachMetres = 2.5f;
    /// <summary>The server accepts a take this much farther than <see cref="ReachMetres"/>, for lag.</summary>
    public const float LatencySlackMetres = 1.5f;
    /// <summary>How far (metres, up or down, feet to node) a player may be from a node: every node sits at foot level (ground, deck, gallery floor), so a floor below never reaches it.</summary>
    public const float ReachUpDownMetres = 2f;

    public static int IndexOf(string mineralId) => Minerals.All.ToList().FindIndex(m => m.Id == mineralId);

    // ---- events ----

    /// <summary>Client to server (<see cref="EventKind.MineralPick"/>): u8 node id.</summary>
    public static byte[] EncodePick(byte node) => new[] { node };

    public static bool TryDecodePick(ReadOnlySpan<byte> p, out byte node)
    {
        node = p.Length == 1 ? p[0] : (byte)0;
        return p.Length == 1;
    }

    /// <summary>Server to everyone, and to a joiner (<see cref="EventKind.MineralNodes"/>, the whole set again when an activity changes): u8 count, then per node u8 id, u8 mineral index (<see cref="Minerals.All"/>), u8 active, f32 x, y, z.</summary>
    public static byte[] EncodeNodes(IReadOnlyList<(MineralNode Node, bool Active)> nodes)
    {
        var b = new byte[1 + nodes.Count * 15];
        b[0] = (byte)nodes.Count;
        for (var i = 0; i < nodes.Count; i++)
        {
            var (n, active) = nodes[i];
            var o = 1 + i * 15;
            b[o] = n.Id;
            b[o + 1] = (byte)IndexOf(n.MineralId);
            b[o + 2] = (byte)(active ? 1 : 0);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 3), n.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 7), n.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 11), n.Position.Z);
        }
        return b;
    }

    public static IReadOnlyList<(MineralNode Node, bool Active)>? DecodeNodes(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1 || p.Length != 1 + p[0] * 15) return null;
        var list = new List<(MineralNode, bool)>(p[0]);
        for (var i = 0; i < p[0]; i++)
        {
            var o = 1 + i * 15;
            if (p[o + 1] >= Minerals.All.Count) return null;
            var pos = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(p[(o + 3)..]), BinaryPrimitives.ReadSingleLittleEndian(p[(o + 7)..]), BinaryPrimitives.ReadSingleLittleEndian(p[(o + 11)..]));
            list.Add((new MineralNode(p[o], Minerals.All[p[o + 1]].Id, pos), p[o + 2] != 0));
        }
        return list;
    }

    /// <summary>Server to the finder (<see cref="EventKind.MineralFinds"/>) at join and after each find: u8 count, then per find u8 mineral index, u32 rank. <paramref name="justFound"/> (a mineral index, or 255 for none) is the find that made this message, so the client plays the pickup sound.</summary>
    public static byte[] EncodeFinds(IReadOnlyDictionary<string, int> ranks, byte justFound = 255)
    {
        var list = ranks.Select(kv => (Index: IndexOf(kv.Key), Rank: kv.Value)).Where(f => f.Index >= 0).OrderBy(f => f.Index).ToList();
        var b = new byte[2 + list.Count * 5];
        b[0] = justFound;
        b[1] = (byte)list.Count;
        for (var i = 0; i < list.Count; i++)
        {
            b[2 + i * 5] = (byte)list[i].Index;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(3 + i * 5), (uint)list[i].Rank);
        }
        return b;
    }

    public static (IReadOnlyDictionary<string, int> Ranks, string? JustFound)? DecodeFinds(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 || p.Length != 2 + p[1] * 5) return null;
        var d = new Dictionary<string, int>();
        for (var i = 0; i < p[1]; i++)
        {
            var idx = p[2 + i * 5];
            if (idx >= Minerals.All.Count) return null;
            d[Minerals.All[idx].Id] = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[(3 + i * 5)..]);
        }
        return (d, p[0] < Minerals.All.Count ? Minerals.All[p[0]].Id : null);
    }
}

/// <summary>
/// The finding rules of the Mineral Log (GLOSSARY "Find"), pure and in memory: built by replaying the stored finds oldest first, then fed each new one. A player finds each mineral once, ever;
/// the place among a mineral's finders ("7th found") is the order of first finds, the same for the server's whole life. Not thread-safe: the server's tick owns it.
/// </summary>
public sealed class MineralBook
{
    readonly Dictionary<string, int> _finders = new();
    readonly Dictionary<long, Dictionary<string, int>> _ranks = new();

    public static MineralBook From(IEnumerable<MineralFind> finds)
    {
        var book = new MineralBook();
        foreach (var f in finds) book.Add(f.PlayerId, f.MineralId);
        return book;
    }

    /// <summary>Records a find and returns its place among the mineral's finders (1: the first); 0 when the mineral is unknown or the player already has it.</summary>
    public int Add(long playerId, string mineralId)
    {
        if (Minerals.Find(mineralId) == null) return 0;
        if (!_ranks.TryGetValue(playerId, out var mine)) _ranks[playerId] = mine = new Dictionary<string, int>();
        if (mine.ContainsKey(mineralId)) return 0;
        var rank = _finders.GetValueOrDefault(mineralId) + 1;
        _finders[mineralId] = rank;
        mine[mineralId] = rank;
        return rank;
    }

    public bool Has(long playerId, string mineralId) => _ranks.TryGetValue(playerId, out var m) && m.ContainsKey(mineralId);

    /// <summary>The player's finds: mineral id to place among its finders (what <see cref="MineralTileModel.For"/> takes).</summary>
    public IReadOnlyDictionary<string, int> RanksOf(long playerId) => _ranks.TryGetValue(playerId, out var m) ? m : new Dictionary<string, int>();

    /// <summary>How many players have found the mineral.</summary>
    public int Finders(string mineralId) => _finders.GetValueOrDefault(mineralId);

    /// <summary>
    /// Whether and how a player can take a node's mineral: the node's rule says active, the player is within reach (flat) and has not found it. Does not record anything.
    /// </summary>
    public MineralPickResult Check(long playerId, MineralNode? node, bool active, Vector3 player, float slack = MineralRules.LatencySlackMetres)
    {
        if (node == null) return MineralPickResult.UnknownNode;
        var dx = node.Position.X - player.X;
        var dz = node.Position.Z - player.Z;
        if (MathF.Sqrt(dx * dx + dz * dz) > MineralRules.ReachMetres + slack || MathF.Abs(node.Position.Y - player.Y) > MineralRules.ReachUpDownMetres) return MineralPickResult.TooFar;
        if (!active) return MineralPickResult.Inactive;
        return Has(playerId, node.MineralId) ? MineralPickResult.AlreadyFound : MineralPickResult.Ok;
    }
}
