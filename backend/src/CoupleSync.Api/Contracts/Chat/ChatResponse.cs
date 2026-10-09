namespace CoupleSync.Api.Contracts.Chat;

/// <param name="Provider">Who wrote the reply ("gemini"...). Optional: absent in a fixed sentence of the app itself.</param>
public sealed record ChatResponse(string Reply, string? Provider = null);
