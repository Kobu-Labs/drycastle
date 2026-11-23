using System.Text;
using Drycastle.ShooterCore.Hunts;

namespace Drycastle.ShooterServer.Persistence;

/// <summary>
/// The WHERE and ORDER BY of a hunt page for the SQL stores (SQLite and PostgreSQL share this text; their parameters are named "@p0", "@p1", ...).
/// The same rule as <see cref="HuntQueryRules"/>, which the in-memory store runs; the contract tests hold them together. The hunts table is
/// aliased <c>h</c> and the players table <c>p</c>.
/// </summary>
internal static class HuntSql
{
    public const string Columns = "h.id, h.player_id, p.name, h.species, h.exact_species, h.variant, h.kg, h.distance_m, h.weapon, h.hit_along, h.hit_height, h.hit_right, h.hit_head, h.hit_pose, h.hit_frame, h.x, h.y, h.z, h.hunted_at";

    public static string Where(HuntQuery q, List<object> args)
    {
        var sb = new StringBuilder("1 = 1");
        if (q.SpeciesAny != null)
        {
            if (q.SpeciesAny.Count == 0) sb.Append(" AND 1 = 0");
            else sb.Append(" AND h.species IN (").Append(string.Join(", ", q.SpeciesAny.Select(s => Param(args, s)))).Append(')');
        }
        if (q.Weapon != "") sb.Append(" AND h.weapon = ").Append(Param(args, q.Weapon));
        if (q.PlayerId != 0) sb.Append(" AND h.player_id = ").Append(Param(args, q.PlayerId));
        return sb.ToString();
    }

    public static string OrderBy(HuntQuery q, List<object> args)
    {
        var dir = q.Descending ? "DESC" : "ASC";
        return q.Sort switch
        {
            HuntSort.Time => $"h.hunted_at {dir}, h.id {dir}",
            HuntSort.Species => (q.HiddenLast.Count == 0 ? "" : $"(CASE WHEN h.species IN ({string.Join(", ", q.HiddenLast.Select(s => Param(args, s)))}) THEN 1 ELSE 0 END), ") + $"h.species {dir}, h.id DESC",
            HuntSort.Hunter => $"lower(p.name) {dir}, h.id DESC",
            HuntSort.Kg => $"(h.kg IS NULL), h.kg {dir}, h.id DESC",
            HuntSort.Distance => $"(h.distance_m IS NULL), h.distance_m {dir}, h.id DESC",
            _ => $"h.weapon {dir}, h.id DESC",
        };
    }

    static string Param(List<object> args, object value)
    {
        args.Add(value);
        return "@p" + (args.Count - 1);
    }
}
