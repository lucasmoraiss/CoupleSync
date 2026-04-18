namespace CoupleSync.Domain.Interfaces;

public interface IGeminiAdapter
{
    Task<string> SendAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct);
}

public sealed record ChatMessage(string Role, string Content);
