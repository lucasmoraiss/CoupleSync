using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;

namespace CoupleSync.Infrastructure.Integrations.Llm;

/// <summary>
/// Google Gemini (AI Studio) through generateContent, asking for JSON in a schema. The key travels in the
/// x-goog-api-key header, never in the URL.
/// </summary>
public sealed class GeminiLlmProvider : ILlmProvider
{
    public const string HttpClientName = "Llm.gemini";
    public const string ApiKeyHeader = "x-goog-api-key";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _endpoint;
    private readonly string _apiKey;

    public GeminiLlmProvider(IHttpClientFactory httpClientFactory, string endpoint, string apiKey, string model)
    {
        _httpClientFactory = httpClientFactory;
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        Model = model;
    }

    public string Provider => AiOptions.GeminiProviderName;

    public string Model { get; }

    public LlmCapabilities Capabilities { get; } = new(JsonSchema: true, Images: true, Pdf: true);

    public Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        var contents = new JsonArray();
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            var parts = new JsonArray { new JsonObject { ["text"] = message.Text } };
            if (i == request.Messages.Count - 1)
            {
                foreach (var attachment in request.Attachments ?? [])
                {
                    parts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject { ["mimeType"] = attachment.MimeType, ["data"] = Convert.ToBase64String(attachment.Data) },
                    });
                }
            }

            contents.Add(new JsonObject { ["role"] = message.Role == "model" ? "model" : "user", ["parts"] = parts });
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = request.SystemPrompt } } },
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = request.Temperature,
                ["maxOutputTokens"] = request.MaxOutputTokens,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = LlmSchemaWriter.Gemini(request.ResponseSchema),
            },
        };

        var http = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/models/{Uri.EscapeDataString(Model)}:generateContent")
        {
            Content = LlmHttp.JsonBody(body),
        };
        http.Headers.Add(ApiKeyHeader, _apiKey);

        return SendAsync(http, ct);
    }

    private async Task<LlmResult> SendAsync(HttpRequestMessage http, CancellationToken ct)
    {
        using (http)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            return await LlmHttp.PostAsync(client, http, Parse, SaysTheDayIsOver, ct);
        }
    }

    private static LlmHttp.Parsed Parse(JsonElement root)
    {
        var usage = LlmHttp.Child(root, "usageMetadata") ?? default;
        var input = LlmHttp.Int(usage, "promptTokenCount");
        // The "thinking" tokens are billed as output.
        var output = LlmHttp.Int(usage, "candidatesTokenCount") + LlmHttp.Int(usage, "thoughtsTokenCount");

        var candidate = LlmHttp.First(LlmHttp.Child(root, "candidates"));
        var content = candidate is { } c ? LlmHttp.Child(c, "content") : null;
        var part = content is { } ct ? LlmHttp.First(LlmHttp.Child(ct, "parts")) : null;
        var text = part is { } p ? LlmHttp.Text(p, "text") : null;
        return new LlmHttp.Parsed(text, input, output);
    }

    /// <summary>
    /// A 429 of Gemini is RESOURCE_EXHAUSTED for every kind of quota; the kind is in the QuotaFailure detail, whose
    /// quotaId names the window ("...PerDay..." or "...PerMinute..."). The day is over only when a violation says
    /// "PerDay". (Format taken from Google's error model; it could not be confirmed against the real key in this
    /// phase — an answer in any other shape is treated as a per-minute limit, which only pauses the link.)
    /// </summary>
    internal static bool SaysTheDayIsOver(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var details = LlmHttp.Child(LlmHttp.Child(document.RootElement, "error") ?? default, "details");
            if (details is not { ValueKind: JsonValueKind.Array } list) return false;

            foreach (var detail in list.EnumerateArray())
            {
                if (LlmHttp.Child(detail, "violations") is not { ValueKind: JsonValueKind.Array } violations) continue;
                foreach (var violation in violations.EnumerateArray())
                {
                    var quotaId = LlmHttp.Text(violation, "quotaId");
                    if (quotaId is not null && quotaId.Contains("PerDay", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Any API in the chat/completions format (Groq as the first choice; OpenRouter, Mistral). Delivered ready and
/// switched off: there is no key of a second provider, and without a key it is in no chain. Tested against a fake
/// HTTP server only — the exact base URL and format are confirmed in the provider's documentation when it is
/// switched on.
/// </summary>
public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public OpenAiCompatibleLlmProvider(IHttpClientFactory httpClientFactory, string name, string baseUrl, string apiKey, string model)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        Provider = name;
        Model = model;
    }

    public static string HttpClientNameOf(string providerName) => $"Llm.{providerName}";

    public string Provider { get; }

    public string Model { get; }

    public LlmCapabilities Capabilities { get; } = new(JsonSchema: true, Images: false, Pdf: false);

    public async Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt } };
        foreach (var message in request.Messages)
            messages.Add(new JsonObject { ["role"] = message.Role == "model" ? "assistant" : "user", ["content"] = message.Text });

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxOutputTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.ResponseSchema.Name,
                    ["strict"] = true,
                    ["schema"] = LlmSchemaWriter.JsonSchema(request.ResponseSchema),
                },
            },
        };

        using var http = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions") { Content = LlmHttp.JsonBody(body) };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var client = _httpClientFactory.CreateClient(HttpClientNameOf(Provider));
        return await LlmHttp.PostAsync(client, http, Parse, SaysTheDayIsOver, ct);
    }

    private static LlmHttp.Parsed Parse(JsonElement root)
    {
        var usage = LlmHttp.Child(root, "usage") ?? default;
        var choice = LlmHttp.First(LlmHttp.Child(root, "choices"));
        var message = choice is { } c ? LlmHttp.Child(c, "message") : null;
        var text = message is { } m ? LlmHttp.Text(m, "content") : null;
        return new LlmHttp.Parsed(text, LlmHttp.Int(usage, "prompt_tokens"), LlmHttp.Int(usage, "completion_tokens"));
    }

    /// <summary>Groq names the window in the message ("... on tokens per day (TPD)"). Anything else is a pause.</summary>
    private static bool SaysTheDayIsOver(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = LlmHttp.Text(LlmHttp.Child(document.RootElement, "error") ?? default, "message");
            return message is not null
                && (message.Contains("per day", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("(RPD)", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("(TPD)", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// The provider of the integration tests and of "App E2E" (<c>Ai__UseFakeProvider=true</c>): it calls nobody. It
/// builds its answer from the request, in the schema asked for, with a fixed text in Portuguese and no numbers of its
/// own. (From the phase that brings the fact pack on, it also copies numbers and ids from the pack.)
/// <see cref="FakeLlmProviderGuard"/> keeps it away from any environment that has a real key.
/// </summary>
public sealed class FakeLlmProvider : ILlmProvider
{
    public const string FixedText = "Esta é uma resposta de teste do assistente, sem dados reais.";

    public string Provider => AiOptions.FakeProviderName;

    public string Model => AiOptions.FakeProviderName;

    public LlmCapabilities Capabilities { get; } = new(JsonSchema: true, Images: false, Pdf: false);

    public Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        var json = Build(request.ResponseSchema)!.ToJsonString();
        var input = PromptText.EstimateTokens(request.SystemPrompt) + request.Messages.Sum(m => PromptText.EstimateTokens(m.Text));
        return Task.FromResult(new LlmResult(LlmOutcome.Ok, json, Math.Max(1, input), Math.Max(1, PromptText.EstimateTokens(json)), null, 1));
    }

    private static JsonNode? Build(LlmJsonSchema schema)
    {
        switch (schema.Type)
        {
            case LlmJsonType.Object:
                var node = new JsonObject();
                foreach (var property in schema.Properties) node[property.Key] = Build(property.Value);
                return node;
            case LlmJsonType.Array:
                return new JsonArray();
            case LlmJsonType.String:
                return JsonValue.Create(schema.Enum.Count > 0 ? schema.Enum[0] : FixedText);
            case LlmJsonType.Boolean:
                return JsonValue.Create(false);
            default:
                return JsonValue.Create(0);
        }
    }
}
