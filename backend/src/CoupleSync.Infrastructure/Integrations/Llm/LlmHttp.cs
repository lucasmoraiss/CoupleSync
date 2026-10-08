using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Integrations.Llm;

/// <summary>
/// What the HTTP adapters share: one POST, timed, whose failures become outcomes. Nothing here logs or returns the
/// body of a request or of an error answer — a provider may echo the prompt in it.
/// </summary>
internal static class LlmHttp
{
    /// <summary>What a successful answer carried.</summary>
    internal sealed record Parsed(string? Text, int InputTokens, int OutputTokens);

    /// <param name="saysTheDayIsOver">Reads the body of a 429: true only when it says explicitly that the day's quota ended.</param>
    public static async Task<LlmResult> PostAsync(
        HttpClient client,
        HttpRequestMessage request,
        Func<JsonElement, Parsed> parse,
        Func<string, bool> saysTheDayIsOver,
        CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        int Elapsed() => (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Only an explicit "the day's quota is over" takes the link out for the day; a per-minute limit or
                // an answer that does not say which one is a pause (the gateway makes it grow if it repeats).
                var outcome = saysTheDayIsOver(body) ? LlmOutcome.QuotaExhaustedDay : LlmOutcome.RateLimitedMinute;
                return new LlmResult(outcome, null, 0, 0, "HTTP_429", Elapsed());
            }

            if (!response.IsSuccessStatusCode)
                return new LlmResult(LlmOutcome.Error, null, 0, 0, $"HTTP_{(int)response.StatusCode}", Elapsed());

            Parsed parsed;
            try
            {
                using var document = JsonDocument.Parse(body);
                parsed = parse(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return new LlmResult(LlmOutcome.InvalidOutput, null, 0, 0, "UNREADABLE_ANSWER", Elapsed());
            }

            return string.IsNullOrWhiteSpace(parsed.Text)
                ? new LlmResult(LlmOutcome.InvalidOutput, null, parsed.InputTokens, parsed.OutputTokens, "EMPTY_ANSWER", Elapsed())
                : new LlmResult(LlmOutcome.Ok, parsed.Text, parsed.InputTokens, parsed.OutputTokens, null, Elapsed());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout. A cancellation asked by the caller (the gateway's time for the link) goes up.
            return new LlmResult(LlmOutcome.Timeout, null, 0, 0, "TIMEOUT", Elapsed());
        }
        catch (HttpRequestException)
        {
            return new LlmResult(LlmOutcome.Error, null, 0, 0, "NETWORK", Elapsed());
        }
    }

    public static StringContent JsonBody(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    public static int Int(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    public static string? Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonElement? Child(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? First(JsonElement? array)
        => array is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0] : null;
}

/// <summary>The common schema subset written in each provider's dialect.</summary>
internal static class LlmSchemaWriter
{
    /// <summary>Gemini's responseSchema: upper-case types, "required" and "propertyOrdering".</summary>
    public static JsonObject Gemini(LlmJsonSchema schema)
    {
        var node = new JsonObject { ["type"] = schema.Type.ToString().ToUpperInvariant() };
        switch (schema.Type)
        {
            case LlmJsonType.Object:
                var properties = new JsonObject();
                foreach (var property in schema.Properties) properties[property.Key] = Gemini(property.Value);
                node["properties"] = properties;
                node["required"] = Names(schema);
                node["propertyOrdering"] = Names(schema);
                break;
            case LlmJsonType.Array:
                node["items"] = Gemini(schema.Items!);
                break;
            case LlmJsonType.String when schema.Enum.Count > 0:
                node["enum"] = new JsonArray(schema.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
                break;
        }

        return node;
    }

    /// <summary>JSON Schema as "strict" structured outputs want it: every property required, no additional ones.</summary>
    public static JsonObject JsonSchema(LlmJsonSchema schema)
    {
        var node = new JsonObject { ["type"] = schema.Type.ToString().ToLowerInvariant() };
        switch (schema.Type)
        {
            case LlmJsonType.Object:
                var properties = new JsonObject();
                foreach (var property in schema.Properties) properties[property.Key] = JsonSchema(property.Value);
                node["properties"] = properties;
                node["required"] = Names(schema);
                node["additionalProperties"] = false;
                break;
            case LlmJsonType.Array:
                node["items"] = JsonSchema(schema.Items!);
                break;
            case LlmJsonType.String when schema.Enum.Count > 0:
                node["enum"] = new JsonArray(schema.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
                break;
        }

        return node;
    }

    private static JsonArray Names(LlmJsonSchema schema)
        => new(schema.Properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
}
