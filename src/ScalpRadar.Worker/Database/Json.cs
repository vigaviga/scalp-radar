using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScalpRadar.Worker.Database;

/// <summary>JSON for jsonb columns. Postgres jsonb rejects NaN/Infinity, so non-finite numbers become null.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new FiniteDoubleConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override bool HandleNull => false;

        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(Math.Round(value, 10));
            else writer.WriteNullValue();
        }
    }
}
