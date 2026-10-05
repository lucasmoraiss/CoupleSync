using System.Text.Json;
using CoupleSync.Api.Serialization;

namespace CoupleSync.UnitTests.Validation;

/// <summary>
/// A07 — PostgreSQL "timestamp with time zone" only accepts DateTime values with Kind=Utc.
/// Every DateTime read from a request body must therefore arrive as UTC.
/// </summary>
public sealed class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeJsonConverter());
        return options;
    }

    private sealed record Payload(DateTime Value, DateTime? Optional);

    [Fact]
    public void Read_WithOffset_ConvertsToUtc()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"value":"2026-10-03T10:00:00-03:00"}""", Options)!;

        Assert.Equal(DateTimeKind.Utc, payload.Value.Kind);
        Assert.Equal(new DateTime(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc), payload.Value);
    }

    [Fact]
    public void Read_WithoutZone_IsTreatedAsUtc()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"value":"2026-10-05T10:00:00"}""", Options)!;

        Assert.Equal(DateTimeKind.Utc, payload.Value.Kind);
        Assert.Equal(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), payload.Value);
    }

    [Fact]
    public void Read_WithZ_StaysUtc()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"value":"2026-10-05T10:00:00Z"}""", Options)!;

        Assert.Equal(DateTimeKind.Utc, payload.Value.Kind);
        Assert.Equal(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), payload.Value);
    }

    [Fact]
    public void Read_NullableWithOffset_ConvertsToUtc()
    {
        var payload = JsonSerializer.Deserialize<Payload>(
            """{"value":"2026-10-05T10:00:00Z","optional":"2026-12-31T23:30:00+02:00"}""", Options)!;

        Assert.Equal(DateTimeKind.Utc, payload.Optional!.Value.Kind);
        Assert.Equal(new DateTime(2026, 12, 31, 21, 30, 0, DateTimeKind.Utc), payload.Optional.Value);
    }

    [Fact]
    public void Write_UtcValue_KeepsIsoFormatWithZ()
    {
        var json = JsonSerializer.Serialize(
            new Payload(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), null), Options);

        Assert.Contains("\"2026-10-05T10:00:00Z\"", json);
    }
}
