using Suchohrad.ShooterServer.Persistence.Postgres;
using Suchohrad.ShooterServer.Persistence.Sqlite;

namespace Suchohrad.ShooterServer.Persistence;

/// <summary>Picks the database from the server's <c>--db</c> value: a PostgreSQL URL or connection string, else a SQLite file path.</summary>
public static class ProgressionStores
{
    /// <summary>True for <c>postgres://</c> / <c>postgresql://</c> URLs and Npgsql connection strings (contain "Host=").</summary>
    public static bool IsPostgres(string target) =>
        target.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        || target.Contains("Host=", StringComparison.OrdinalIgnoreCase);

    public static IProgressionStore Open(string target, Action<string> log, LevelCurve curve) =>
        IsPostgres(target) ? PostgresProgressionStore.Open(ToConnectionString(target), log, curve) : SqliteProgressionStore.Open(target, log, curve);

    /// <summary>Turns <c>postgres://user:pass@host:port/db</c> into an Npgsql connection string; a connection string passes through.</summary>
    public static string ToConnectionString(string target)
    {
        if (!target.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || !target.Contains("://")) return target;
        var u = new Uri(target);
        var b = new Npgsql.NpgsqlConnectionStringBuilder { Host = u.Host, Database = u.AbsolutePath.TrimStart('/') };
        if (u.Port > 0) b.Port = u.Port;
        if (u.UserInfo.Length > 0)
        {
            var parts = u.UserInfo.Split(':', 2);
            b.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) b.Password = Uri.UnescapeDataString(parts[1]);
        }
        return b.ToString();
    }

    /// <summary>A log-safe description of the target (no password).</summary>
    public static string Describe(string target)
    {
        if (!IsPostgres(target)) return $"sqlite {Path.GetFullPath(target)}";
        var b = new Npgsql.NpgsqlConnectionStringBuilder(ToConnectionString(target));
        return $"postgres {b.Username}@{b.Host}:{b.Port}/{b.Database}";
    }
}
