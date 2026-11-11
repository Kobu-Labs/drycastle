using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Care package rules (ticket #124): the killstreak that drops a crate on a parachute. The owner's crate lands
/// <see cref="DropDistanceMetres"/> ahead of them; any living player within reach holds the interact key for
/// <see cref="OpenHoldSeconds"/> to open it and gets a random perk until they die. An unopened crate disappears after
/// <see cref="LifetimeSeconds"/>.
/// </summary>
public static class CarePackageRules
{
    /// <summary>Wire id of the care package world entity type.</summary>
    public const byte TypeId = 30;
    /// <summary>killstreaks.json id of the care package streak.</summary>
    public const string StreakId = "care_package";
    /// <summary>Seconds an unopened crate stays on the map, counted from the drop.</summary>
    public const float LifetimeSeconds = 60f;
    /// <summary>Where the crate lands, metres ahead of the owner's feet.</summary>
    public const float DropDistanceMetres = 5f;
    /// <summary>Height the crate starts its fall from, above the ground under the landing point.</summary>
    public const float StartHeightMetres = 30f;
    /// <summary>Descent speed on the parachute, m/s.</summary>
    public const float FallSpeedMetresPerSecond = 6f;
    /// <summary>Hold time of the interact key that opens a landed crate (client holds it, server trusts the send, as with corpses).</summary>
    public const float OpenHoldSeconds = 2f;
    /// <summary>Reach of the opening, metres from the crate.</summary>
    public const float ReachMetres = 2.5f;
    /// <summary>Extra reach the server allows for latency, as for corpses.</summary>
    public const float LatencySlackMetres = 0.5f;

    /// <summary>State byte on the wire: still on its parachute, or on the ground and openable.</summary>
    public const byte StateFalling = 0, StateLanded = 1;

    /// <summary>Registers the type once (idempotent). A crate carries no hit points.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "care package", HasHp: false));
    }

    /// <summary>The landing point of a drop: <see cref="DropDistanceMetres"/> ahead of <paramref name="feet"/> along the owner's yaw (the movement forward).</summary>
    public static Vector3 DropPoint(Vector3 feet, float yaw)
    {
        var (sin, cos) = MathF.SinCos(yaw);
        return feet + new Vector3(-sin, 0, -cos) * DropDistanceMetres;
    }

    public static bool InReach(Vector3 player, Vector3 crate, float slack = 0) =>
        Vector3.Distance(player, crate) <= ReachMetres + slack;
}

/// <summary>
/// The perk weights of the care package (killstreaks.json "perk_weights"). A roll picks one perk with a probability
/// proportional to its weight, so the better perks (smaller weights) are rarer. Entries keep perks.json order.
/// </summary>
public sealed class PerkTable
{
    readonly (string Id, int Weight)[] _entries;

    PerkTable((string Id, int Weight)[] entries, int total)
    {
        _entries = entries;
        Total = total;
    }

    /// <summary>Sum of all weights: the roll range is [0, Total).</summary>
    public int Total { get; }

    public IReadOnlyList<(string Id, int Weight)> Entries => _entries;

    /// <summary>
    /// Builds the table, checking every weighted id against perks.json. Throws on an empty table, an unknown perk, or a
    /// weight below 1; the server refuses to start with a bad table.
    /// </summary>
    public static PerkTable Build(IReadOnlyDictionary<string, int> weights, PerksFile perks)
    {
        if (weights.Count == 0) throw new InvalidOperationException("care package: no perk weights");
        foreach (var (id, w) in weights)
        {
            if (perks.Find(id) == null) throw new InvalidOperationException($"care package: perk \"{id}\" is not in perks.json");
            if (w < 1) throw new InvalidOperationException($"care package: perk \"{id}\" needs a weight of 1 or more");
        }
        var entries = perks.Perks.Where(p => weights.ContainsKey(p.Id)).Select(p => (p.Id, weights[p.Id])).ToArray();
        return new PerkTable(entries, entries.Sum(e => e.Item2));
    }

    /// <summary>The perk for a roll in [0, <see cref="Total"/>). Throws outside that range.</summary>
    public string Pick(int roll)
    {
        if (roll < 0 || roll >= Total) throw new ArgumentOutOfRangeException(nameof(roll), roll, $"roll must be in [0, {Total})");
        var acc = 0;
        foreach (var (id, w) in _entries)
        {
            acc += w;
            if (roll < acc) return id;
        }
        throw new InvalidOperationException("unreachable: the weights cover the roll range");
    }

    /// <summary>A random perk from the weights.</summary>
    public string Pick(Random rng) => Pick(rng.Next(Total));
}

/// <summary>Why a crate could not be opened.</summary>
public enum OpenResult : byte { Ok, Gone, NotLanded, OutOfReach }

/// <summary>
/// A care package on the map: falls from <see cref="CarePackageRules.StartHeightMetres"/> above its landing point on a
/// parachute and is openable once it lands. Takes no damage. It despawns after its lifetime, or when opened.
/// </summary>
public sealed class CarePackage : IWorldEntity
{
    readonly Vector3 _landing;
    float _altitude;
    float _age;

    public CarePackage(ushort owner, Vector3 landing)
    {
        Owner = owner;
        _landing = landing;
        _altitude = landing.Y + CarePackageRules.StartHeightMetres;
    }

    public byte TypeId => CarePackageRules.TypeId;
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }
    /// <summary>The landing point (its Y is the ground).</summary>
    public Vector3 Landing => _landing;
    public bool Landed => _altitude <= _landing.Y;
    public Vector3 Position => new(_landing.X, _altitude, _landing.Z);

    public void Tick(float dt)
    {
        _age += dt;
        if (!Landed) _altitude = MathF.Max(_landing.Y, _altitude - CarePackageRules.FallSpeedMetresPerSecond * dt);
        if (Ended == null && _age >= CarePackageRules.LifetimeSeconds) Ended = WorldDespawnReason.Expired;
    }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, 0, 0, Owner, Landed ? CarePackageRules.StateLanded : CarePackageRules.StateFalling, 0);

    /// <summary>Marks the crate as opened; the book then despawns it with <see cref="WorldDespawnReason.Opened"/>.</summary>
    internal void MarkOpened() => Ended = WorldDespawnReason.Opened;
}

/// <summary>
/// The care packages on the map. Owned by the match next to the entity manager; a crate that the manager despawns (expiry,
/// opened, round end) is forgotten. Opening rolls the perk from the table, so the caller only applies it.
/// </summary>
public sealed class CarePackageBook
{
    readonly WorldEntityManager _world;
    readonly PerkTable _table;
    readonly Dictionary<ushort, CarePackage> _crates = new();

    public CarePackageBook(WorldEntityManager world, PerkTable table)
    {
        _world = world;
        _table = table;
        world.Despawned += d => _crates.Remove(d.Id);
    }

    public int Count => _crates.Count;

    /// <summary>Drops a crate at <paramref name="landing"/>. Null when the world refuses it (cap reached).</summary>
    public ushort? Drop(ushort owner, Vector3 landing)
    {
        var crate = new CarePackage(owner, landing);
        var id = _world.Spawn(crate);
        if (id is { } i) _crates[i] = crate;
        return id;
    }

    /// <summary>
    /// Opens a crate for a player at <paramref name="player"/>. On <see cref="OpenResult.Ok"/> the rolled perk id is in
    /// <paramref name="perk"/> and the crate is gone. Nothing changes otherwise.
    /// </summary>
    public OpenResult TryOpen(ushort crateId, Vector3 player, Random rng, out string? perk, float slack = CarePackageRules.LatencySlackMetres)
    {
        perk = null;
        if (!_crates.TryGetValue(crateId, out var crate)) return OpenResult.Gone;
        if (!crate.Landed) return OpenResult.NotLanded;
        if (!CarePackageRules.InReach(player, crate.Position, slack)) return OpenResult.OutOfReach;
        perk = _table.Pick(rng);
        crate.MarkOpened();
        _world.Despawn(crateId, WorldDespawnReason.Opened);
        return OpenResult.Ok;
    }
}

/// <summary>
/// The care package killstreak: a use drops a crate ahead of the owner and ends at once (the crate lives on by itself).
/// Refused (the streak stays held) when the world has no room or there is no ground under the landing point.
/// </summary>
public sealed class CarePackageStreak : IKillstreak
{
    readonly CarePackageBook _book;
    readonly Func<ushort, float> _yawOf;
    readonly Func<Vector3, float?> _groundAt;

    /// <param name="book">Where the crate goes.</param>
    /// <param name="yawOf">The owner's yaw now (radians), for the drop direction.</param>
    /// <param name="groundAt">Height of the ground under a point, or null when there is none.</param>
    public CarePackageStreak(CarePackageBook book, Func<ushort, float> yawOf, Func<Vector3, float?> groundAt)
    {
        _book = book;
        _yawOf = yawOf;
        _groundAt = groundAt;
    }

    public string Id => CarePackageRules.StreakId;
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string>();

    public bool Activate(KillstreakUse use)
    {
        var drop = CarePackageRules.DropPoint(use.Position, _yawOf(use.Owner));
        if (_groundAt(drop) is not { } ground)
        {
            use.Log("no ground at the drop point; streak kept");
            return false;
        }
        var landing = new Vector3(drop.X, ground, drop.Z);
        if (_book.Drop(use.Owner, landing) is not { } id)
        {
            use.Log("no room for another world entity; streak kept");
            return false;
        }
        use.Log($"care package {id} dropped at {landing.X:0.0}, {landing.Z:0.0}");
        use.End();
        return true;
    }
}

/// <summary>Payloads of the care package event kinds (little-endian).</summary>
public static class CarePackageCodec
{
    /// <summary>Client to server, <see cref="EventKind.OpenCarePackage"/>: u16 crate id.</summary>
    public static byte[] EncodeOpen(ushort crate)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, crate);
        return b;
    }

    public static bool TryDecodeOpen(ReadOnlySpan<byte> p, out ushort crate)
    {
        crate = 0;
        if (p.Length != 2) return false;
        crate = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return true;
    }

    /// <summary>Server to the opener, <see cref="EventKind.PerkGained"/>: u8 length + utf8 perk id.</summary>
    public static byte[] EncodePerk(string perkId)
    {
        var bytes = Encoding.UTF8.GetBytes(perkId ?? "");
        if (bytes.Length > byte.MaxValue) Array.Resize(ref bytes, byte.MaxValue);
        var o = new byte[1 + bytes.Length];
        o[0] = (byte)bytes.Length;
        bytes.CopyTo(o, 1);
        return o;
    }

    /// <summary>The perk id, or null on malformed input.</summary>
    public static string? DecodePerk(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1 || p.Length != 1 + p[0]) return null;
        return Encoding.UTF8.GetString(p[1..]);
    }
}
