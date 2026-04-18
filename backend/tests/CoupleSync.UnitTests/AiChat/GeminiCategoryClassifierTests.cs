using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.Integrations.Gemini;

namespace CoupleSync.UnitTests.AiChat;

[Trait("Category", "AiChat")]
public sealed class GeminiCategoryClassifierTests
{
    private static readonly IReadOnlyList<string> DefaultCats = GeminiCategoryClassifier.DefaultCategories;

    private static GeminiCategoryClassifier Build(IGeminiAdapter adapter)
        => new(adapter);

    [Fact]
    public async Task ReturnsNull_WhenGeminiThrows()
    {
        var adapter = new ThrowingGeminiAdapter();
        var classifier = Build(adapter);

        var result = await classifier.SuggestCategoryAsync("Pagamento de boleto", DefaultCats, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenResponseNotInCategoryList()
    {
        var adapter = new FixedGeminiAdapter("Bebida alcoólica");
        var classifier = Build(adapter);

        var result = await classifier.SuggestCategoryAsync("Bar do João", DefaultCats, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsCategory_WhenGeminiMatchesCaseInsensitive()
    {
        var adapter = new FixedGeminiAdapter("alimentação");
        var classifier = Build(adapter);

        var result = await classifier.SuggestCategoryAsync("Padaria Central", DefaultCats, CancellationToken.None);

        Assert.Equal("Alimentação", result);
    }

    [Fact]
    public async Task ReturnsCategory_WhenGeminiReturnsExactMatch()
    {
        var adapter = new FixedGeminiAdapter("Transporte");
        var classifier = Build(adapter);

        var result = await classifier.SuggestCategoryAsync("Uber viagem centro", DefaultCats, CancellationToken.None);

        Assert.Equal("Transporte", result);
    }

    [Fact]
    public async Task ReturnsNull_WhenGeminiReturnsEmptyString()
    {
        var adapter = new FixedGeminiAdapter("   ");
        var classifier = Build(adapter);

        var result = await classifier.SuggestCategoryAsync("Compra diversa", DefaultCats, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ShortCircuits_AfterTwoConsecutiveErrors()
    {
        var adapter = new ThrowingGeminiAdapter();
        var classifier = Build(adapter);

        // First two calls throw → _consecutiveErrors = 2
        await classifier.SuggestCategoryAsync("desc1", DefaultCats, CancellationToken.None);
        await classifier.SuggestCategoryAsync("desc2", DefaultCats, CancellationToken.None);

        // Third call must NOT reach the adapter
        var callsBefore = adapter.CallCount;
        await classifier.SuggestCategoryAsync("desc3", DefaultCats, CancellationToken.None);

        Assert.Equal(callsBefore, adapter.CallCount);
    }

    [Fact]
    public async Task ResetsConsecutiveErrors_OnSuccessfulCall()
    {
        var adapter = new PartiallyThrowingGeminiAdapter(throwTimes: 1, thenReturn: "Saúde");
        var classifier = Build(adapter);

        // First call throws → _consecutiveErrors = 1
        await classifier.SuggestCategoryAsync("desc1", DefaultCats, CancellationToken.None);

        // Second call succeeds → _consecutiveErrors = 0
        var result = await classifier.SuggestCategoryAsync("Farmácia", DefaultCats, CancellationToken.None);

        Assert.Equal("Saúde", result);
    }
}

// ── Adapter fakes ──────────────────────────────────────────────────────────

internal sealed class FixedGeminiAdapter : IGeminiAdapter
{
    private readonly string _response;
    public FixedGeminiAdapter(string response) => _response = response;

    public Task<string> SendAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct)
        => Task.FromResult(_response);
}

internal sealed class ThrowingGeminiAdapter : IGeminiAdapter
{
    public int CallCount { get; private set; }

    public Task<string> SendAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct)
    {
        CallCount++;
        throw new InvalidOperationException("Gemini unavailable");
    }
}

internal sealed class PartiallyThrowingGeminiAdapter : IGeminiAdapter
{
    private readonly int _throwTimes;
    private readonly string _thenReturn;
    private int _calls;

    public PartiallyThrowingGeminiAdapter(int throwTimes, string thenReturn)
    {
        _throwTimes = throwTimes;
        _thenReturn = thenReturn;
    }

    public Task<string> SendAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct)
    {
        _calls++;
        if (_calls <= _throwTimes)
            throw new InvalidOperationException("Temporary Gemini error");
        return Task.FromResult(_thenReturn);
    }
}
