using CoupleSync.Application.AiChat;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.AiChat;

[Trait("Category", "AiChat")]
public sealed class GeminiChatServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc);

    private static GeminiChatService BuildService(
        IGeminiAdapter? adapter = null,
        ChatRateLimiter? rateLimiter = null)
    {
        var dt = new FixedDateTimeProvider(FixedNow);
        var budgetRepo = new FakeBudgetRepository();
        var txRepo = new FakeTransactionRepository();
        var goalRepo = new FakeGoalRepository();
        var budgetService = new BudgetService(budgetRepo, txRepo, dt);
        var contextService = new ChatContextService(budgetService, txRepo, goalRepo, dt);

        return new GeminiChatService(
            adapter ?? new FakeGeminiAdapter("OK"),
            contextService,
            rateLimiter ?? new ChatRateLimiter());
    }

    [Fact]
    public async Task ReturnsReplyFromAdapter()
    {
        var adapter = new FakeGeminiAdapter("Hello from AI");
        var svc = BuildService(adapter);
        var coupleId = Guid.NewGuid();

        var result = await svc.ChatAsync(coupleId, "Hi", [], CancellationToken.None);

        Assert.Equal("Hello from AI", result);
    }

    [Fact]
    public async Task ThrowsWhenRateLimited()
    {
        var limiter = new ChatRateLimiter();
        var coupleId = Guid.NewGuid();
        for (var i = 0; i < 30; i++)
            limiter.IsAllowed(coupleId);

        var svc = BuildService(rateLimiter: limiter);

        var ex = await Assert.ThrowsAsync<ChatRateLimitException>(
            () => svc.ChatAsync(coupleId, "Hi", [], CancellationToken.None));

        Assert.Equal("CHAT_RATE_LIMITED", ex.Code);
    }

    [Fact]
    public async Task PassesHistoryToAdapter()
    {
        var adapter = new FakeGeminiAdapter("reply");
        var svc = BuildService(adapter);
        var coupleId = Guid.NewGuid();
        var history = new List<ChatMessage>
        {
            new("user", "previous question"),
            new("model", "previous answer")
        };

        await svc.ChatAsync(coupleId, "follow-up", history, CancellationToken.None);

        Assert.Equal(history, adapter.CapturedHistory);
        Assert.Equal("follow-up", adapter.CapturedUserMessage);
    }
}

internal sealed class FakeGeminiAdapter : IGeminiAdapter
{
    private readonly string _response;

    public IReadOnlyList<ChatMessage>? CapturedHistory { get; private set; }
    public string? CapturedUserMessage { get; private set; }
    public string? CapturedSystemPrompt { get; private set; }

    public FakeGeminiAdapter(string response)
    {
        _response = response;
    }

    public Task<string> SendAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        CancellationToken ct)
    {
        CapturedSystemPrompt = systemPrompt;
        CapturedHistory = history;
        CapturedUserMessage = userMessage;
        return Task.FromResult(_response);
    }
}
