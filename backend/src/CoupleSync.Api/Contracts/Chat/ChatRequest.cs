namespace CoupleSync.Api.Contracts.Chat;

public sealed record ChatHistoryItem(string Role, string Content);

public sealed record ChatRequest(string Message, IReadOnlyList<ChatHistoryItem>? History);
