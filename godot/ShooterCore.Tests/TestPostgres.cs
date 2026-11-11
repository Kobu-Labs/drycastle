using Npgsql;
using Suchohrad.ShooterServer.Persistence;
using Suchohrad.ShooterServer.Persistence.Postgres;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// PostgreSQL test backend. Runs only when SHOOTER_TEST_PG holds an Npgsql connection string (or postgres:// URL) of a throwaway
/// database, for example <c>docker run --rm -p 55432:5432 -e POSTGRES_PASSWORD=test postgres:16</c> and
/// <c>SHOOTER_TEST_PG="Host=localhost;Port=55432;Username=postgres;Password=test;Database=postgres"</c>. Each store gets its own schema.
/// </summary>
public static class TestPostgres
{
    public static string? Target => Environment.GetEnvironmentVariable("SHOOTER_TEST_PG") is { Length: > 0 } v ? v : null;

    /// <summary>"memory" and "sqlite", plus "postgres" when SHOOTER_TEST_PG is set.</summary>
    public static IEnumerable<object[]> Backends() =>
        Target == null ? new[] { new object[] { "memory" }, new object[] { "sqlite" } } : new[] { new object[] { "memory" }, new object[] { "sqlite" }, new object[] { "postgres" } };

    /// <summary>A store on a fresh schema of the test database.</summary>
    public static IProgressionStore Open(LevelCurve curve, Action<string> log)
    {
        var baseConn = ProgressionStores.ToConnectionString(Target!);
        var schema = $"t_{Guid.NewGuid():N}";
        using (var admin = new NpgsqlConnection(baseConn))
        {
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA {schema};";
            cmd.ExecuteNonQuery();
        }
        return PostgresProgressionStore.Open(new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ToString(), log, curve);
    }
}
