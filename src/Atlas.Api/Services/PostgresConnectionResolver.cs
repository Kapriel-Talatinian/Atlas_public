using Npgsql;

namespace Atlas.Api.Services;

/// <summary>
/// Resolves a Postgres connection string from environment variables using a
/// prioritised fallback chain that accounts for Railway's variable resolution
/// behaviour (cross-service variable references such as ${{Postgres.DATABASE_URL}}
/// are NOT resolved at runtime on the consuming service).
///
/// Resolution order:
///   1. BOT_RUNTIME_DB_CONNECTION_STRING — accepted only when it looks like a
///      real connection string (contains "Host=" or starts with "postgres://"/
///      "postgresql://").  An unresolved Railway reference such as
///      "${{Postgres.DATABASE_URL}}" is silently skipped.
///   2. Individual PG* variables (PGHOST, PGPORT, PGUSER, PGPASSWORD, PGDATABASE)
///      — standard libpq environment variables that Railway injects on the
///      Postgres service itself, or that operators can copy to the consuming
///      service manually.
///   3. RAILWAY_PRIVATE_DOMAIN + DB_PORT / DB_USER / DB_PASSWORD / DB_NAME
///      — a Railway-idiomatic alternative when the operator has forwarded the
///      private domain of the Postgres service to the consuming service.
///   4. Throws a descriptive <see cref="InvalidOperationException"/> that tells
///      the operator exactly what to set.
/// </summary>
internal static class PostgresConnectionResolver
{
    /// <summary>
    /// Returns <c>true</c> when enough Postgres configuration is present to
    /// attempt a connection.  Used by the DI registration in Program.cs to
    /// decide whether to activate the Postgres backend without throwing.
    /// </summary>
    internal static bool HasPostgresConfiguration()
    {
        // Valid explicit connection string
        string? explicit_ = Environment.GetEnvironmentVariable("BOT_RUNTIME_DB_CONNECTION_STRING")?.Trim();
        if (TryNormalizeExplicitConnectionString(explicit_, out _))
            return true;

        // Sufficient PG* variables
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGHOST")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGUSER")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGPASSWORD")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGDATABASE")))
            return true;

        // RAILWAY_PRIVATE_DOMAIN + DB_* variables
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAILWAY_PRIVATE_DOMAIN")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB_USER")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB_PASSWORD")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB_NAME")))
            return true;

        return false;
    }

    /// <summary>
    /// Returns a valid Npgsql connection string or throws
    /// <see cref="InvalidOperationException"/> with a clear remediation message.
    /// </summary>
    internal static string ResolveConnectionString()
    {
        // ── Step 1: BOT_RUNTIME_DB_CONNECTION_STRING ──────────────────────────
        // Accept the value only when it is a real connection string, not an
        // unresolved Railway variable reference like "${{Postgres.DATABASE_URL}}".
        string? explicit_ = Environment.GetEnvironmentVariable("BOT_RUNTIME_DB_CONNECTION_STRING")?.Trim();
        if (TryNormalizeExplicitConnectionString(explicit_, out string? normalizedExplicit))
            return normalizedExplicit!;

        // ── Step 2: standard libpq PG* variables ─────────────────────────────
        string? pgHost = Environment.GetEnvironmentVariable("PGHOST")?.Trim();
        string? pgPort = Environment.GetEnvironmentVariable("PGPORT")?.Trim();
        string? pgUser = Environment.GetEnvironmentVariable("PGUSER")?.Trim();
        string? pgPassword = Environment.GetEnvironmentVariable("PGPASSWORD")?.Trim();
        string? pgDatabase = Environment.GetEnvironmentVariable("PGDATABASE")?.Trim();

        if (!string.IsNullOrWhiteSpace(pgHost) &&
            !string.IsNullOrWhiteSpace(pgUser) &&
            !string.IsNullOrWhiteSpace(pgPassword) &&
            !string.IsNullOrWhiteSpace(pgDatabase))
        {
            int port = int.TryParse(pgPort, out int p) ? p : 5432;
            return BuildNpgsqlConnectionString(pgHost, port, pgUser, pgPassword, pgDatabase);
        }

        // ── Step 3: RAILWAY_PRIVATE_DOMAIN + individual DB_* variables ────────
        string? railwayHost = Environment.GetEnvironmentVariable("RAILWAY_PRIVATE_DOMAIN")?.Trim();
        string? dbPort = Environment.GetEnvironmentVariable("DB_PORT")?.Trim();
        string? dbUser = Environment.GetEnvironmentVariable("DB_USER")?.Trim();
        string? dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD")?.Trim();
        string? dbName = Environment.GetEnvironmentVariable("DB_NAME")?.Trim();

        if (!string.IsNullOrWhiteSpace(railwayHost) &&
            !string.IsNullOrWhiteSpace(dbUser) &&
            !string.IsNullOrWhiteSpace(dbPassword) &&
            !string.IsNullOrWhiteSpace(dbName))
        {
            int port = int.TryParse(dbPort, out int p) ? p : 5432;
            return BuildNpgsqlConnectionString(railwayHost, port, dbUser, dbPassword, dbName);
        }

        // ── Step 4: nothing worked — give the operator a clear error ──────────
        throw new InvalidOperationException(
            "Could not resolve a Postgres connection string. " +
            "Set BOT_RUNTIME_DB_CONNECTION_STRING to a valid Npgsql connection string " +
            "(e.g. \"Host=myhost;Port=5432;Username=myuser;Password=mypass;Database=mydb\") " +
            "or a postgres:// URI on the service that needs it. " +
            "Railway does not resolve cross-service variable references (${{Postgres.DATABASE_URL}}) " +
            "at runtime on consuming services — copy the value directly instead. " +
            "Alternatively, set PGHOST, PGPORT, PGUSER, PGPASSWORD, and PGDATABASE individually.");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> looks like a real
    /// connection string rather than an unresolved Railway variable reference.
    /// </summary>
    private static bool TryNormalizeExplicitConnectionString(string? rawValue, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        string candidate = rawValue.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        bool looksLikeConnectionString =
            candidate.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeConnectionString)
            return false;

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(candidate);
            if (string.IsNullOrWhiteSpace(builder.Host) ||
                string.IsNullOrWhiteSpace(builder.Username) ||
                string.IsNullOrWhiteSpace(builder.Database))
            {
                return false;
            }

            normalized = builder.ConnectionString;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string BuildNpgsqlConnectionString(
        string host, int port, string user, string password, string database) =>
        $"Host={host};Port={port};Username={user};Password={password};Database={database};SSL Mode=Prefer;Trust Server Certificate=true";
}
