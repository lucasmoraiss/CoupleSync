using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoupleSync.Api.Serialization;

/// <summary>
/// Normalises every DateTime read from a request body to UTC:
/// values with an offset ("-03:00") are converted, values without any zone are assumed to be UTC.
/// PostgreSQL "timestamp with time zone" columns only accept Kind=Utc, so anything else used to
/// surface as an HTTP 500. Output formatting is unchanged.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
