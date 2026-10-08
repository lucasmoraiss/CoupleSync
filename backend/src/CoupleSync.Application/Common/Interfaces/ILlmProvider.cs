using System.Text.Json;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>One link of a chain: a provider and one of its models. Adapters live in Infrastructure.</summary>
public interface ILlmProvider
{
    /// <summary>"gemini", "fake"; "groq" etc. when switched on.</summary>
    string Provider { get; }

    string Model { get; }

    LlmCapabilities Capabilities { get; }

    /// <summary>What the provider did wrong comes back as <see cref="LlmResult.Outcome"/>, not as an exception.</summary>
    Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct);
}

public sealed record LlmCapabilities(bool JsonSchema, bool Images, bool Pdf);

/// <param name="Role">"user" or "model".</param>
public sealed record LlmMessage(string Role, string Text);

public sealed record LlmAttachment(string MimeType, byte[] Data);

/// <param name="Feature">One of <c>LlmFeatures</c>: chat, insight_daily, insight_weekly, insight_monthly, education, categorize, ocr.</param>
/// <param name="Messages">Role and text; the facts go in a message of their own.</param>
/// <param name="ResponseSchema">Mandatory: every call asks for JSON in a schema.</param>
public sealed record LlmRequest(
    string Feature,
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    LlmJsonSchema ResponseSchema,
    decimal Temperature,
    int MaxOutputTokens,
    IReadOnlyList<LlmAttachment>? Attachments = null);

public enum LlmOutcome
{
    Ok,
    RateLimitedMinute,
    QuotaExhaustedDay,
    InvalidOutput,
    Error,
    Timeout,

    /// <summary>
    /// The caller gave up (the client closed the connection) after the request had been sent: the provider counted
    /// the call, so it is recorded and counts in the budgets like one that was answered.
    /// </summary>
    Cancelled,
}

/// <param name="RetryAfter">In a 429: how long the provider said to wait before trying again, when it said.</param>
public sealed record LlmResult(
    LlmOutcome Outcome,
    string? Json,
    int InputTokens,
    int OutputTokens,
    string? ErrorCode,
    int LatencyMs,
    TimeSpan? RetryAfter = null);

public enum LlmJsonType
{
    Object,
    Array,
    String,
    Number,
    Integer,
    Boolean,
}

/// <summary>
/// The subset of JSON Schema every provider accepts: object, array, string (optionally an enum), number, integer and
/// boolean. Every property of an object is required and no other property is allowed; there is no oneOf nor $ref.
/// Each adapter writes it in its provider's dialect.
/// </summary>
public sealed class LlmJsonSchema
{
    private LlmJsonSchema(LlmJsonType type)
    {
        Type = type;
    }

    public LlmJsonType Type { get; }

    /// <summary>Name of the root object (some providers ask for one).</summary>
    public string Name { get; private init; } = "response";

    /// <summary>The properties of an object, in order; all required.</summary>
    public IReadOnlyList<KeyValuePair<string, LlmJsonSchema>> Properties { get; private init; } = [];

    public LlmJsonSchema? Items { get; private init; }

    public IReadOnlyList<string> Enum { get; private init; } = [];

    public static LlmJsonSchema Object(string name, params (string Name, LlmJsonSchema Schema)[] properties)
    {
        if (properties.Length == 0) throw new ArgumentException("An object needs at least one property.", nameof(properties));
        return new LlmJsonSchema(LlmJsonType.Object)
        {
            Name = name,
            Properties = properties.Select(p => new KeyValuePair<string, LlmJsonSchema>(p.Name, p.Schema)).ToList(),
        };
    }

    public static LlmJsonSchema Array(LlmJsonSchema items) => new(LlmJsonType.Array) { Items = items };

    public static LlmJsonSchema String(params string[] enumValues) => new(LlmJsonType.String) { Enum = enumValues };

    public static LlmJsonSchema Number() => new(LlmJsonType.Number);

    public static LlmJsonSchema Integer() => new(LlmJsonType.Integer);

    public static LlmJsonSchema Boolean() => new(LlmJsonType.Boolean);

    /// <summary>True when the value has exactly the shape of this schema.</summary>
    public bool Matches(JsonElement value)
    {
        switch (Type)
        {
            case LlmJsonType.Object:
                if (value.ValueKind != JsonValueKind.Object) return false;
                var seen = 0;
                foreach (var property in value.EnumerateObject())
                {
                    var schema = Properties.FirstOrDefault(p => p.Key == property.Name).Value;
                    if (schema is null || !schema.Matches(property.Value)) return false;
                    seen++;
                }

                return seen == Properties.Count;
            case LlmJsonType.Array:
                if (value.ValueKind != JsonValueKind.Array) return false;
                foreach (var item in value.EnumerateArray())
                    if (!Items!.Matches(item)) return false;
                return true;
            case LlmJsonType.String:
                return value.ValueKind == JsonValueKind.String
                    && (Enum.Count == 0 || Enum.Contains(value.GetString(), StringComparer.Ordinal));
            case LlmJsonType.Number:
                return value.ValueKind == JsonValueKind.Number;
            case LlmJsonType.Integer:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
            case LlmJsonType.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            default:
                return false;
        }
    }
}
