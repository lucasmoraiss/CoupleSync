using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Persistence.Seeders;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// The API on SQLite with the Pluggy HTTP client pointed at <see cref="FakePluggyServer"/> (no network) and an
/// encryption key generated for this host only. Pass <c>encryptionKey: null</c> for a server without the variable.
/// </summary>
internal sealed class OpenFinanceApiFactory : TestApiFactory
{
    private const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    private const string JwtIssuer = "CoupleSync.IntegrationTests";
    private const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString = $"Data Source=couplesync-openfinance-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly string? _encryptionKey;
    private SqliteConnection? _keepAliveConnection;

    public OpenFinanceApiFactory(string? encryptionKey)
    {
        _encryptionKey = encryptionKey;
        SetEnvironment();
    }

    public OpenFinanceApiFactory() : this(NewEncryptionKey())
    {
    }

    /// <summary>A fresh random 32-byte key in Base64, as the owner generates for the server.</summary>
    public static string NewEncryptionKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public FakePluggyServer Pluggy { get; } = new();

    public CapturedLogs Logs { get; } = new();

    private static void SetEnvironment()
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
    }

    protected override void BeforeCreateHost() => SetEnvironment();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["RateLimiting:Auth:PermitLimit"] = "10000",
                ["RateLimiting:CoupleJoin:PermitLimit"] = "10000",
                // Empty (not absent) so that a value in the machine's environment can never leak into a test.
                ["OPENFINANCE_ENCRYPTION_KEY"] = _encryptionKey ?? string.Empty,
                ["OpenFinance:PluggyBaseUrl"] = FakePluggyServer.BaseUrl,
                // Everything is captured, down to Trace (where HttpClient writes request headers).
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            });
        });

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();

            _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
            _keepAliveConnection.Open();

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_databaseConnectionString));

            // Every call of the named client "Pluggy" lands on the in-memory fake: no network.
            services.AddHttpClient("Pluggy").ConfigurePrimaryHttpMessageHandler(() => Pluggy);

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            new CategoryRulesSeeder(db).SeedAsync().GetAwaiter().GetResult();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
        Environment.SetEnvironmentVariable("JWT__SECRET", null);
        Environment.SetEnvironmentVariable("JWT__ISSUER", null);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
    }

    /// <summary>A new user in a new group (or in the group of <paramref name="joinCode"/>); the client carries the token.</summary>
    public async Task<Member> RegisterAsync(string name, string? joinCode = null)
    {
        var client = CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = $"of-{Guid.NewGuid():N}@example.com",
            Name = name,
            Password = "SecurePass123!",
        });
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        var userId = registered.GetProperty("user").GetProperty("id").GetGuid();
        Use(client, registered.GetProperty("accessToken").GetString()!);

        var group = joinCode is null
            ? await client.PostAsJsonAsync("/api/v1/couples", new { })
            : await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
        group.EnsureSuccessStatusCode();
        var joined = await group.Content.ReadFromJsonAsync<JsonElement>();
        Use(client, joined.GetProperty("accessToken").GetString()!);

        return new Member(client, userId, joined.GetProperty("coupleId").GetGuid(),
            joinCode ?? joined.GetProperty("joinCode").GetString()!);
    }

    /// <summary>Rows of a table read straight from the database (no group filter, no entity mapping).</summary>
    public async Task<List<Dictionary<string, object?>>> RowsAsync(string sql)
    {
        await using var connection = new SqliteConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    private static void Use(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

internal sealed record Member(HttpClient Client, Guid UserId, Guid CoupleId, string JoinCode);

/// <summary>Everything the host logged, at every level, for the "no secret in the log" assertions.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _lines;

        public Logger(string category, ConcurrentQueue<string> lines)
        {
            _category = category;
            _lines = lines;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue($"{logLevel} {_category}: {formatter(state, exception)} {exception}");
    }
}
