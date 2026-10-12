using System.Text.Json;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// One byte form per JSON value, so a hash survives storage. It matches what <c>jsonb</c>
/// does to a document: object keys are sorted, the last duplicate key wins, and a number
/// has one spelling (<c>1.0</c>, <c>1e0</c> and <c>1</c> are the same value).
/// </summary>
internal static class CanonicalJson
{
    public static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .GroupBy(p => p.Name, StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Last().Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var exact))
                    // Dividing by one with maximum scale strips trailing zeros; adding zero drops the sign of -0.
                    writer.WriteNumberValue(exact / 1.0000000000000000000000000000m + 0m);
                else
                    writer.WriteNumberValue(element.GetDouble());
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
