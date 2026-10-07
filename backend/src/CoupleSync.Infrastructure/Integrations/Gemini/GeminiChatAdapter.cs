using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Gemini;

public sealed class GeminiChatAdapter : IGeminiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiChatAdapter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GeminiChatAdapter(IHttpClientFactory httpClientFactory, IOptions<GeminiOptions> options, ILogger<GeminiChatAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> SendAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
            throw new AppException("CHAT_NOT_CONFIGURED", "O assistente de IA não está configurado.", 503);

        var contents = history
            .Select(h => new GeminiContent(h.Role, new[] { new GeminiPart(h.Content) }))
            .Append(new GeminiContent("user", new[] { new GeminiPart(userMessage) }))
            .ToArray();

        var requestBody = new GeminiRequest(
            SystemInstruction: new GeminiSystemInstruction(new[] { new GeminiPart(systemPrompt) }),
            Contents: contents,
            GenerationConfig: new GeminiGenerationConfig(_options.MaxTokens));

        var url = $"{_options.Endpoint}/models/{_options.Model}:generateContent";

        var client = _httpClientFactory.CreateClient("Gemini");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", _options.ApiKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);
        using var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Gemini API answered {StatusCode}.", (int)response.StatusCode);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ChatRateLimitException("CHAT_RATE_LIMITED", "O assistente de IA atingiu o limite de uso. Tente novamente mais tarde.");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AppException("CHAT_ADAPTER_AUTH_FAILURE", "Não foi possível acessar o assistente de IA no momento.", 502);

        if (!response.IsSuccessStatusCode)
            throw new AppException("CHAT_ADAPTER_ERROR", "O assistente de IA está indisponível no momento. Tente novamente mais tarde.", 502);

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty response from Gemini API.");

        return result.Candidates?[0].Content?.Parts?[0].Text
            ?? throw new InvalidOperationException("Unexpected Gemini response structure.");
    }

    private sealed record GeminiRequest(
        [property: JsonPropertyName("systemInstruction")] GeminiSystemInstruction SystemInstruction,
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiSystemInstruction(
        [property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiContent(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);
}
