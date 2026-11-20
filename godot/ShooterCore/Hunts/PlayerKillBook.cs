namespace Drycastle.ShooterCore;

/// <summary>
/// One player's longest kill of another player (GLOSSARY "Player kill"): not a <see cref="HuntRecord"/> (no species, weight or rarity). Killer and victim are
/// both stored profiles' ids. <see cref="DistanceM"/> is kept to a tenth.
/// </summary>
public sealed record PlayerKillRecord(long KillerId, long VictimId, float DistanceM, string Weapon, float X, float Y, float Z, DateTime AtUtc)
{
    /// <summary>
    /// The kill the book may keep; null when it counts for nothing: a guest, bot or sandbox player on either side (id 0), a suicide, no weapon, no distance. That the shot was
    /// an enemy's and hitscan (no grenade, vehicle, melee or run over) is the server's to know where it deals the damage.
    /// </summary>
    public static PlayerKillRecord? For(long killer, long victim, float? distanceM, string weapon, float x, float y, float z, DateTime atUtc)
    {
        if (killer == 0 || victim == 0 || killer == victim || weapon == "" || distanceM is not { } d) return null;
        d = HuntRecord.Round(d);
        return d > 0 ? new PlayerKillRecord(killer, victim, d, weapon, x, y, z, atUtc) : null;
    }
}

/// <summary>A stored best kill with the names its killer and victim have now (players.name).</summary>
public sealed record PlayerKillRow(PlayerKillRecord Kill, string Killer, string Victim);

/// <summary>What a new kill did to the book: whether it is the killer's own longest now, and whether it is the server's (the one announced).</summary>
public readonly record struct PlayerKillOutcome(bool Own, bool Server);

/// <summary>
/// The longest player kills, in one place beside <see cref="HuntBook"/>: the best of each killer and the server's, which is the best of those. Built by replaying the
/// stored bests oldest first and then fed each new kill. A tie keeps the earlier kill (a best is beaten only by more). No log of every kill. Not thread-safe: the server's tick owns it.
/// </summary>
public sealed class PlayerKillBook
{
    readonly Dictionary<long, PlayerKillRecord> _best = new();
    readonly Dictionary<long, string> _names = new();
    PlayerKillRecord? _server;

    /// <summary>The book of the stored bests, oldest first.</summary>
    public static PlayerKillBook From(IEnumerable<PlayerKillRow> rows)
    {
        var book = new PlayerKillBook();
        foreach (var r in rows)
        {
            book.SetName(r.Kill.KillerId, r.Killer);
            book.SetName(r.Kill.VictimId, r.Victim);
            book.Add(r.Kill);
        }
        return book;
    }

    public void SetName(long playerId, string name)
    {
        if (playerId != 0 && name != "") _names[playerId] = name;
    }

    public string NameOf(long playerId) => _names.GetValueOrDefault(playerId) ?? "";

    /// <summary>Takes one kill (newer than all that are in it) and says what it changed.</summary>
    public PlayerKillOutcome Add(PlayerKillRecord k)
    {
        var own = !_best.TryGetValue(k.KillerId, out var b) || k.DistanceM > b.DistanceM;
        if (own) _best[k.KillerId] = k;
        var server = _server == null || k.DistanceM > _server.DistanceM;
        if (server) _server = k;
        return new PlayerKillOutcome(own, server);
    }

    /// <summary>The longest player kill of the server; null while there is none.</summary>
    public PlayerKillView? Server => View(_server);

    /// <summary>One player's longest kill; null if they have none.</summary>
    public PlayerKillView? Of(long playerId) => View(_best.GetValueOrDefault(playerId));

    public PlayerKillView? View(PlayerKillRecord? k) =>
        k == null ? null : new PlayerKillView(k.KillerId, NameOf(k.KillerId), k.VictimId, NameOf(k.VictimId), k.DistanceM, k.Weapon, HuntBook.Unix(k.AtUtc));
}
