using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Gemini;

public sealed class GeminiChatAdapter : IGeminiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GeminiOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GeminiChatAdapter(IHttpClientFactory httpClientFactory, IOptions<GeminiOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<string> SendAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
            throw new AppException("CHAT_NOT_CONFIGURED", "AI Chat is not configured.", 503);

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

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ChatRateLimitException("CHAT_RATE_LIMITED", "Gemini API quota exceeded. Please try again later.");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AppException("CHAT_ADAPTER_AUTH_FAILURE", "Gemini API authentication failed.", 502);

        if (!response.IsSuccessStatusCode)
            throw new AppException("CHAT_ADAPTER_ERROR", $"Gemini API returned {(int)response.StatusCode}.", 502);

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
