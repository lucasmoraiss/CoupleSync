using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
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

    /// <summary>
    /// False (the default): everything is logged down to Trace, to prove that no secret is written at any level.
    /// True: the levels of appsettings.json, the ones production runs with.
    /// </summary>
    public bool ProductionLogLevels { get; init; }

    /// <summary>
    /// When set, the Pluggy client keeps the HTTP handler the application registers and calls this address
    /// (a loopback server of the test) instead of <see cref="Pluggy"/>.
    /// </summary>
    public string? LoopbackPluggyAddress { get; init; }

    /// <summary>
    /// The synchronisation job looks at its queue every 50 ms instead of every 5 s (the tests of the synchronisation
    /// wait for the real hosted job). Off by default: the tests of the connection never have a run to execute.
    /// </summary>
    public bool FastSync { get; init; }

    /// <summary>The daily scheduler ticks every 50 ms. Off by default (it would enqueue runs the tests did not ask for).</summary>
    public bool SchedulerOn { get; init; }

    /// <summary>The clock of the API: the real one, moved by <see cref="TestClock.Offset"/>.</summary>
    public TestClock Clock { get; } = new();

    /// <summary>The AI category classifier of the API: records what it is asked and answers <see cref="RecordingClassifier.Answer"/>.</summary>
    public RecordingClassifier Classifier { get; } = new();

    /// <summary>Runs once, right before the next save of the API: what another request did in the meantime.</summary>
    public Func<Task>? BeforeNextSave
    {
        get => _saveHook.BeforeNextSave;
        set => _saveHook.BeforeNextSave = value;
    }

    private readonly SaveHook _saveHook = new();

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
            var settings = new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["RateLimiting:Auth:PermitLimit"] = "10000",
                ["RateLimiting:CoupleJoin:PermitLimit"] = "10000",
                // Empty (not absent) so that a value in the machine's environment can never leak into a test.
                ["OPENFINANCE_ENCRYPTION_KEY"] = _encryptionKey ?? string.Empty,
                ["OpenFinance:PluggyBaseUrl"] = LoopbackPluggyAddress ?? FakePluggyServer.BaseUrl,
                ["OpenFinance:SchedulerTickSeconds"] = SchedulerOn ? "0.05" : "0",
            };
            if (FastSync) settings["OpenFinance:SyncPollSeconds"] = "0.05";
            if (!ProductionLogLevels)
            {
                // Everything is captured, down to Trace (where HttpClient writes request headers).
                settings["Logging:LogLevel:Default"] = "Trace";
                settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
                settings["Logging:LogLevel:System.Net.Http.HttpClient.Pluggy"] = "Trace";
            }

            configBuilder.AddInMemoryCollection(settings);
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

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_databaseConnectionString).AddInterceptors(_saveHook));

            services.RemoveAll<IDateTimeProvider>();
            services.AddSingleton<IDateTimeProvider>(Clock);
            services.RemoveAll<ICategoryClassifier>();
            services.AddSingleton<ICategoryClassifier>(Classifier);

            // Every call of the named client "Pluggy" lands on the in-memory fake: no network.
            if (LoopbackPluggyAddress is null)
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

    /// <summary>Writes straight to the database, as another request (or another server) would have.</summary>
    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SaveHook : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public Func<Task>? BeforeNextSave { get; set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var hook = BeforeNextSave;
            BeforeNextSave = null;
            if (hook is not null) await hook();
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static void Use(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

internal sealed record Member(HttpClient Client, Guid UserId, Guid CoupleId, string JoinCode);

/// <summary>The real clock, moved forward or back by <see cref="Offset"/>.</summary>
internal sealed class TestClock : IDateTimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public DateTime UtcNow => DateTime.UtcNow + Offset;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    /// <summary>Makes "now" this instant (and lets time go on from there).</summary>
    public void SetNow(DateTime utc) => Offset = DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UtcNow;
}

/// <summary>An AI classifier that calls nobody: it records every description it was sent.</summary>
internal sealed class RecordingClassifier : ICategoryClassifier
{
    private readonly ConcurrentQueue<string> _asked = new();

    public IReadOnlyCollection<string> Asked => _asked.ToArray();

    /// <summary>What it answers (a label as the real one would: "Lazer"); null: no suggestion.</summary>
    public string? Answer { get; set; }

    /// <summary>When set, every call fails with this.</summary>
    public Exception? Failure { get; set; }

    public Task<string?> SuggestCategoryAsync(string description, IReadOnlyList<string> availableCategories, CancellationToken ct)
    {
        _asked.Enqueue(description);
        return Failure is null ? Task.FromResult(Answer) : Task.FromException<string?>(Failure);
    }
}

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
