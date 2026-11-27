using System.Text.Json;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Why the Contact panel's Start is not offered (shown in words instead of the button working), in the server's order of checks (<see cref="CartelRules.CanStart"/>).</summary>
public enum ContactStartBlock : byte
{
    None,
    /// <summary>The player's standing has not come yet.</summary>
    Loading,
    NotMember,
    /// <summary>In a Squad, only its leader starts a Job ("Your leader starts jobs").</summary>
    NotLeader,
    /// <summary>The round is not running.</summary>
    NoRound,
    /// <summary>The player's party runs a Job already.</summary>
    JobRunning,
}

/// <summary>One tier of a kind on the Contact's list: open to the player's rank, or locked with the rank it needs (its name), and the Respect it pays.</summary>
public readonly record struct ContactTier(int Tier, bool Open, string NeedsRank, int Respect);

/// <summary>One kind of Job on the Contact's list (its id, the name players read) with every tier of cartel.json.</summary>
public sealed record ContactJob(string Kind, string Name, IReadOnlyList<ContactTier> Tiers);

/// <summary>
/// What the Contact panel shows (docs/cartel.md "Contact"; GLOSSARY "Contact"), from cartel.json, the player's standing (<see cref="CartelStatusMsg"/>) and the match: for a member
/// the rank, the Respect and the bar to the next rank, and the job list by kind and tier (the test kind only for admins; a tier above the rank locked, with the rank it
/// needs); for anyone else what the Cartel is and Join. Start is the leader's (a solo player, or the Squad's leader) and only with no Job running. Pure: the panel draws it,
/// the server checks every request again.
/// </summary>
public sealed record ContactPanelModel(
    bool Loaded, CartelMembership Membership, bool Guest, bool Sandbox,
    string RankName, int Respect, string? NextRankName, int RespectToNext, float BarFraction,
    IReadOnlyList<ContactJob> Jobs, ContactStartBlock StartBlock)
{
    public bool IsMember => Membership == CartelMembership.Member;

    /// <param name="status">The player's standing; null until the server sent it.</param>
    /// <param name="admin">Admins also see the test kind.</param>
    /// <param name="leader">The player would lead a Job they start: alone (ffa, or in no Squad) or their Squad's leader.</param>
    /// <param name="jobRunning">The player's party runs a Job now.</param>
    public static ContactPanelModel Build(CartelFile data, CartelStatusMsg? status, bool admin, bool leader, bool jobRunning, bool roundRunning)
    {
        var s = status ?? new CartelStatusMsg(CartelMembership.Outsider, 0, 0, CartelStatusFlags.None);
        var rank = Math.Clamp((int)s.Rank, 0, data.Ranks.Count - 1);
        var here = data.Ranks[rank].Respect;
        var next = rank + 1 < data.Ranks.Count ? data.Ranks[rank + 1] : null;
        var fraction = next == null ? 1f : Math.Clamp((float)(s.Respect - here) / (next.Respect - here), 0f, 1f);
        var jobs = KindsOf(data, admin)
            .Select(k => new ContactJob(k.Id, k.Name, data.Tiers.Select(t => new ContactTier(t.Tier, data.TierOpen(t.Tier, rank), data.Ranks[data.RankIndex(t.Rank)].Name, t.Respect)).ToList()))
            .ToList();
        var block = status == null ? ContactStartBlock.Loading
            : s.Membership != CartelMembership.Member ? ContactStartBlock.NotMember
            : !leader ? ContactStartBlock.NotLeader
            : !roundRunning ? ContactStartBlock.NoRound
            : jobRunning ? ContactStartBlock.JobRunning
            : ContactStartBlock.None;
        return new ContactPanelModel(status != null, s.Membership, s.Flags.HasFlag(CartelStatusFlags.Guest), s.Flags.HasFlag(CartelStatusFlags.Sandbox),
            data.Ranks[rank].Name, s.Respect, next?.Name, next == null ? 0 : Math.Max(0, next.Respect - s.Respect), fraction, jobs, block);
    }

    /// <summary>The kinds cartel.json lists under <c>jobs</c>, in its order, with the name each section gives (its id without one); the test kind only for admins.</summary>
    public static IReadOnlyList<(string Id, string Name)> KindsOf(CartelFile data, bool admin) => data.Jobs
        .Where(kv => admin || kv.Key != TestJobKind.KindId)
        .Select(kv => (kv.Key, NameOf(kv.Key, kv.Value)))
        .ToList();

    static string NameOf(string id, JsonElement section) =>
        section.ValueKind == JsonValueKind.Object && section.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name
            ? name : id;

    /// <summary>The tier the list selects first: the highest open tier of the first kind (null with no kind).</summary>
    public (string Kind, int Tier)? FirstChoice => Jobs.Count == 0 ? null : (Jobs[0].Kind, Jobs[0].Tiers.Where(t => t.Open).Select(t => t.Tier).DefaultIfEmpty(1).Max());
}
