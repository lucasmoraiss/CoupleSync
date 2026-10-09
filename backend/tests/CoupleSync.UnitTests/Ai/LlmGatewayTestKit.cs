using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Ai;

/// <summary>What the model is asked for in the gateway tests: the Assistant's answer.</summary>
public sealed record TestAnswer(string Answer, IReadOnlyList<string> Refs);

/// <summary>
/// The gateway on a real database (SQLite in memory) with scripted providers, a clock the test moves and a waiter
/// that advances it. <see cref="Gateway"/> builds everything again on the same database each time it is called:
/// that is "the API restarted" — a new context, a new minute window, nothing kept in memory.
/// </summary>
internal sealed class LlmGatewayTestKit : IDisposable
{
    public const string Ok = """{"answer":"Tudo certo por aqui.","refs":[]}""";

    public static readonly LlmJsonSchema AnswerSchema = LlmJsonSchema.Object(
        "answer",
        ("answer", LlmJsonSchema.String()),
        ("refs", LlmJsonSchema.Array(LlmJsonSchema.String())));

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly List<AppDbContext> _contexts = new();

    public LlmGatewayTestKit()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    /// <summary>10:00 in Brasília, 13:00 UTC: both calendars are on the same day.</summary>
    public MutableClock Clock { get; } = new(new DateTime(2026, 10, 8, 13, 0, 0, DateTimeKind.Utc));

    public AiOptions Options { get; } = new();

    public TestCatalog Catalog { get; } = new();

    public TestConsentGate Consent { get; } = new();

    public ListLogger<LlmGateway> Log { get; } = new();

    public List<TimeSpan> Waits { get; } = new();

    public AppDbContext NewContext()
    {
        var context = new AppDbContext(_dbOptions);
        _contexts.Add(context);
        return context;
    }

    public LlmGateway Gateway(LlmMinuteWindow? window = null) => new(
        Catalog,
        new AiUsageRepository(NewContext()),
        Consent,
        Microsoft.Extensions.Options.Options.Create(Options),
        window ?? new LlmMinuteWindow(),
        Clock,
        new AdvancingWaiter(Clock, Waits),
        Log);

    public void Chain(string name, params ScriptedProvider[] providers)
    {
        Options.Chains[name] = providers.Select(p => new LlmLink(p.Provider, p.Model)).ToList();
        foreach (var provider in providers) Catalog.Add(provider);
    }

    public static LlmRequest Request(string feature = LlmFeatures.Chat, string question = "Quanto gastamos?") => new(
        feature,
        "Você é o assistente.",
        [new LlmMessage("user", question)],
        AnswerSchema,
        LlmFeatures.TemperatureOf(feature),
        1000);

    public Task<LlmGatewayResult<TestAnswer>> AskAsync(
        Guid? coupleId,
        LlmCallMode mode = LlmCallMode.Interactive,
        string feature = LlmFeatures.Chat,
        LlmMinuteWindow? window = null)
        => Gateway(window).GenerateAsync<TestAnswer>(coupleId, Request(feature), mode, CancellationToken.None);

    public List<AiUsage> Rows()
    {
        using var db = new AppDbContext(_dbOptions);
        return db.AiUsages.AsNoTracking().ToList().OrderBy(u => u.CreatedAtUtc).ToList();
    }

    public void Seed(int count, string provider, string model, Guid? coupleId, string outcome = "Ok", string feature = LlmFeatures.Chat, int inputTokens = 10, int outputTokens = 5)
    {
        using var db = new AppDbContext(_dbOptions);
        for (var i = 0; i < count; i++)
            db.AiUsages.Add(AiUsage.Record(Clock.UtcNow, provider, model, coupleId, feature, inputTokens, outputTokens, outcome, 12));
        db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
        _connection.Dispose();
    }
}

internal sealed class MutableClock : IDateTimeProvider
{
    public MutableClock(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Waiting costs no real time: the clock jumps by what was asked.</summary>
internal sealed class AdvancingWaiter : ILlmWaiter
{
    private readonly MutableClock _clock;
    private readonly List<TimeSpan>? _waits;

    public AdvancingWaiter(MutableClock clock, List<TimeSpan>? waits = null)
    {
        _clock = clock;
        _waits = waits;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        _waits?.Add(delay);
        _clock.Advance(delay);
        return Task.CompletedTask;
    }
}

internal sealed class TestConsentGate : IAiConsentGate
{
    public bool Enabled { get; set; } = true;

    public int Checks { get; private set; }

    public Task<bool> IsEnabledAsync(Guid? coupleId, CancellationToken ct)
    {
        Checks++;
        return Task.FromResult(Enabled);
    }
}

internal sealed class TestCatalog : ILlmProviderCatalog
{
    private readonly Dictionary<string, ILlmProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public bool AnyAvailable => _providers.Count > 0;

    public void Add(ILlmProvider provider) => _providers[$"{provider.Provider}|{provider.Model}"] = provider;

    public ILlmProvider? Find(string provider, string model)
        => _providers.TryGetValue($"{provider}|{model}", out var found) ? found : null;
}

/// <summary>A provider that answers what the test scripted, in order; the last answer repeats.</summary>
internal sealed class ScriptedProvider : ILlmProvider
{
    private readonly Queue<Func<LlmRequest, CancellationToken, Task<LlmResult>>> _script = new();
    private Func<LlmRequest, CancellationToken, Task<LlmResult>> _last;

    public ScriptedProvider(string provider, string model, params LlmResult[] results)
    {
        Provider = provider;
        Model = model;
        _last = (_, _) => Task.FromResult(Result(LlmOutcome.Ok, LlmGatewayTestKit.Ok));
        foreach (var result in results) Then(result);
    }

    public string Provider { get; }

    public string Model { get; }

    public LlmCapabilities Capabilities { get; } = new(JsonSchema: true, Images: false, Pdf: false);

    public List<LlmRequest> Requests { get; } = new();

    public int Calls => Requests.Count;

    public ScriptedProvider Then(LlmResult result) => Then((_, _) => Task.FromResult(result));

    public ScriptedProvider Then(Func<LlmRequest, CancellationToken, Task<LlmResult>> answer)
    {
        _script.Enqueue(answer);
        _last = answer;
        return this;
    }

    public Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var answer = _script.Count > 0 ? _script.Dequeue() : _last;
        return answer(request, ct);
    }

    public static LlmResult Result(LlmOutcome outcome, string? json = null, int inputTokens = 100, int outputTokens = 20)
        => outcome == LlmOutcome.Ok || outcome == LlmOutcome.InvalidOutput
            ? new LlmResult(outcome, json, inputTokens, outputTokens, null, 15)
            : new LlmResult(outcome, null, 0, 0, outcome.ToString().ToUpperInvariant(), 15);

    public static LlmResult OkResult(string json = LlmGatewayTestKit.Ok) => Result(LlmOutcome.Ok, json);
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
