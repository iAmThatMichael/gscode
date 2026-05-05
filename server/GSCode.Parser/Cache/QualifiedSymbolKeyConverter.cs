using GSCode.Parser.SA;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GSCode.Parser.Cache;

/// <summary>
/// JSON converter for <see cref="QualifiedSymbolKey"/> when used as a dictionary key.
/// Serializes as "qualifier::symbolName" and deserializes the same format.
/// </summary>
public sealed class QualifiedSymbolKeyConverter : JsonConverter<QualifiedSymbolKey>
{
    private const string Separator = "::";

    public override QualifiedSymbolKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        if (value is null)
            return default;

        int separatorIndex = value.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
            return new QualifiedSymbolKey(string.Empty, value);

        string qualifier = value[..separatorIndex];
        string symbolName = value[(separatorIndex + Separator.Length)..];
        return new QualifiedSymbolKey(qualifier, symbolName);
    }

    public override void Write(Utf8JsonWriter writer, QualifiedSymbolKey value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"{value.Qualifier}{Separator}{value.SymbolName}");
    }

    public override QualifiedSymbolKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Read(ref reader, typeToConvert, options);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, QualifiedSymbolKey value, JsonSerializerOptions options)
    {
        writer.WritePropertyName($"{value.Qualifier}{Separator}{value.SymbolName}");
    }
}
