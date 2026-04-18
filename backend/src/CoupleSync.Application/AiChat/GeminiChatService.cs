using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Application.AiChat;

public sealed class GeminiChatService
{
    private readonly IGeminiAdapter _geminiAdapter;
    private readonly ChatContextService _contextService;
    private readonly ChatRateLimiter _rateLimiter;

    public GeminiChatService(
        IGeminiAdapter geminiAdapter,
        ChatContextService contextService,
        ChatRateLimiter rateLimiter)
    {
        _geminiAdapter = geminiAdapter;
        _contextService = contextService;
        _rateLimiter = rateLimiter;
    }

    public async Task<string> ChatAsync(
        Guid coupleId,
        string message,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct)
    {
        if (!_rateLimiter.IsAllowed(coupleId))
            throw new ChatRateLimitException("CHAT_RATE_LIMITED", "Maximum 30 requests per hour. Please try again later.");

        var systemPrompt = await _contextService.BuildSystemPromptAsync(coupleId, ct);
        return await _geminiAdapter.SendAsync(systemPrompt, history, message, ct);
    }
}
