using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// Resolves the Npgsql connection string from environment or configuration.
///
/// Neon.tech connection string format (set as DATABASE_URL env var in production):
///   Host=ep-xxx.region.neon.tech;Port=5432;Database=couplesync;Username=user;Password=pass;
///   SslMode=Require;MaxPoolSize=10;MinPoolSize=1;ConnectionIdleLifetime=240
///
/// Or as a postgres:// URI:
///   postgresql://user:pass@ep-xxx.region.neon.tech/couplesync?sslmode=require
///
/// When a Neon host (*.neon.tech) is detected, SslMode=Require,
/// MaxPoolSize=10, MinPoolSize=1 and ConnectionIdleLifetime=240 are applied automatically.
/// </summary>
public static class DatabaseConnectionResolver
{
    // Neon free-tier caps at ~20 connections; keep pool ≤ 10 per instance.
    private const int NeonMaxPoolSize = 10;
    private const int NeonMinPoolSize = 1;
    // Neon auto-hibernates after 5 min; release idle connections before that (4 min buffer).
    private const int NeonConnectionIdleLifetimeSeconds = 240;

    public static string Resolve(IConfiguration configuration)
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return ParseDatabaseUrl(databaseUrl);
        }

        var fallback = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(fallback))
        {
            throw new InvalidOperationException("Database connection string is missing. Set DATABASE_URL or ConnectionStrings:DefaultConnection.");
        }

        return fallback;
    }

    public static string ParseDatabaseUrl(string databaseUrl)
    {
        if (databaseUrl.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyNeonSettingsIfNeeded(databaseUrl);
        }

        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("DATABASE_URL format is invalid.");
        }

        var userInfoParts = uri.UserInfo.Split(':', 2);
        if (userInfoParts.Length != 2)
        {
            throw new InvalidOperationException("DATABASE_URL user info is invalid.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfoParts[0]),
            Password = Uri.UnescapeDataString(userInfoParts[1]),
            SslMode = SslMode.Prefer
        };

        if (IsNeonHost(builder.Host))
        {
            ApplyNeonSettings(builder);
        }

        return builder.ConnectionString;
    }

    private static string ApplyNeonSettingsIfNeeded(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (IsNeonHost(builder.Host))
        {
            ApplyNeonSettings(builder);
        }
        return builder.ConnectionString;
    }

    private static bool IsNeonHost(string? host) =>
        host != null && host.Contains("neon.tech", StringComparison.OrdinalIgnoreCase);

    private static void ApplyNeonSettings(NpgsqlConnectionStringBuilder builder)
    {
        builder.SslMode = SslMode.Require;
        // TrustServerCertificate is intentionally not set here; Neon uses a CA-signed certificate
        // and SslMode=Require with full validation is the secure default for Npgsql 8+.
        builder.MaxPoolSize = NeonMaxPoolSize;
        builder.MinPoolSize = NeonMinPoolSize;
        builder.ConnectionIdleLifetime = NeonConnectionIdleLifetimeSeconds;
    }
}
