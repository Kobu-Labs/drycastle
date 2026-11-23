using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>
/// The Companions one player can have out and the one that is out (GLOSSARY "Companion"): what the picker on the Equipment screen shows. The server builds it
/// (<see cref="Of"/>), tells the owner (<see cref="EventKind.Companions"/>) and checks a pick against it (<see cref="EventKind.CompanionPick"/>). Indices are those of
/// companions.json. Godot-free.
/// </summary>
/// <param name="Owned">The Companions the player can pick, in the order of companions.json.</param>
/// <param name="Out">The one that is out, or -1 for none.</param>
public sealed record CompanionRoster(IReadOnlyList<int> Owned, int Out)
{
    public static readonly CompanionRoster None = new(Array.Empty<int>(), -1);

    const ushort NoneOut = ushort.MaxValue;
    /// <summary>The most Companions one roster carries (the count is a byte).</summary>
    public const int Max = byte.MaxValue;

    public bool Owns(int def) => Owned.Contains(def);

    /// <summary>
    /// The roster of one player: every Companion with an unlock row of kind <see cref="CompanionRules.UnlockKind"/>, the one given for the session (<c>/companion</c>),
    /// the one that is out, or all of them in dev mode (<paramref name="all"/>).
    /// </summary>
    public static CompanionRoster Of(CompanionsFile cfg, IEnumerable<Unlock> unlocks, bool all, string? given, string? @out)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in unlocks) if (u.Kind == CompanionRules.UnlockKind) ids.Add(u.ItemId);
        if (!string.IsNullOrEmpty(given)) ids.Add(given);
        if (!string.IsNullOrEmpty(@out)) ids.Add(@out);
        var owned = new List<int>();
        for (var i = 0; i < cfg.Companions.Count && owned.Count < Max; i++)
            if (all || ids.Contains(cfg.Companions[i].Id)) owned.Add(i);
        var index = string.IsNullOrEmpty(@out) ? -1 : cfg.IndexOf(@out);
        return new CompanionRoster(owned, owned.Contains(index) ? index : -1);
    }

    /// <summary>
    /// The Companion a player has out when they join: the one they chose last (<paramref name="chosen"/>) when they still own it, else the first they own; -1 when they own none.
    /// </summary>
    public int Default(CompanionsFile cfg, string? chosen)
    {
        var index = string.IsNullOrEmpty(chosen) ? -1 : cfg.IndexOf(chosen);
        return Owns(index) ? index : Owned.Count > 0 ? Owned[0] : -1;
    }

    /// <summary>Server to the owner, <see cref="EventKind.Companions"/>: u8 count, a u16 companions.json index each, then the u16 index of the one that is out (0xFFFF none).</summary>
    public byte[] Encode()
    {
        var n = Math.Min(Owned.Count, Max);
        var b = new byte[1 + n * 2 + 2];
        b[0] = (byte)n;
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1 + i * 2), (ushort)Owned[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1 + n * 2), Out < 0 ? NoneOut : (ushort)Out);
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> p, out CompanionRoster roster)
    {
        roster = None;
        if (p.Length < 3 || p.Length != 1 + p[0] * 2 + 2) return false;
        var owned = new int[p[0]];
        for (var i = 0; i < owned.Length; i++) owned[i] = BinaryPrimitives.ReadUInt16LittleEndian(p[(1 + i * 2)..]);
        var o = BinaryPrimitives.ReadUInt16LittleEndian(p[(1 + owned.Length * 2)..]);
        roster = new CompanionRoster(owned, o == NoneOut ? -1 : o);
        return true;
    }

    /// <summary>Client to server, <see cref="EventKind.CompanionPick"/>: the u16 companions.json index of the Companion the player wants out.</summary>
    public static byte[] EncodePick(int def)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Math.Clamp(def, 0, ushort.MaxValue));
        return b;
    }

    public static bool TryDecodePick(ReadOnlySpan<byte> p, out int def)
    {
        def = -1;
        if (p.Length != 2) return false;
        def = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return true;
    }
}
