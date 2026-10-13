using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// One byte form per JSON value, so a hash survives storage. It matches what <c>jsonb</c>
/// does to a document: object keys are sorted, the last duplicate key wins, and a number
/// has one spelling (<c>1.0</c>, <c>1e0</c> and <c>1</c> are the same value).
/// <para>
/// <b>Numbers.</b> A number is written as a decimal with at most 28 significant digits when
/// it has such a form, else as the nearest finite double. <see cref="Defect"/> refuses a
/// number that has neither form (<c>1e999</c>) and a number whose text is longer than
/// <see cref="MaxNumberChars"/>.
/// </para>
/// <para>
/// <b>Total.</b> <see cref="Write"/> does not throw for any <see cref="JsonElement"/>. A
/// value that <see cref="Defect"/> refuses still has one byte form, so that a caller that
/// skipped the check gets a stable hash and not an exception: a number with no finite form
/// is written with its own digits, and text that is not valid Unicode is written with its
/// escapes as plain characters. The validator and the save rule refuse both before any
/// payload is hashed, stored or played.
/// </para>
/// </summary>
internal static class CanonicalJson
{
    /// <summary>The most characters the text of one number holds. A decimal has 28 significant digits; the rest is sign, point and exponent.</summary>
    public const int MaxNumberChars = 40;

    public static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Select(p => (Name: NameOf(p), p.Value))
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
                var real = element.GetDouble();
                // A decimal reads a number below its range as zero. That is another value, so the double is used.
                if (element.TryGetDecimal(out var exact) && (exact != 0m || real == 0d))
                    // Dividing by one with maximum scale strips trailing zeros; adding zero drops the sign of -0.
                    writer.WriteNumberValue(exact / 1.0000000000000000000000000000m + 0m);
                else if (double.IsFinite(real))
                    writer.WriteNumberValue(real);
                else
                    writer.WriteRawValue(JsonMarshal.GetRawUtf8Value(element), skipInputValidation: true);
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(TextOf(element));
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            // Null, and an element that holds no value.
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Why a value cannot be hashed, stored and played as it is, or null. One sentence part
    /// that follows "The payload": a number with no finite form, a number that is too long,
    /// or text that is not valid Unicode. The walk does not recurse past <paramref name="maxDepth"/>.
    /// </summary>
    public static string? Defect(JsonElement element, int maxDepth = 64)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (maxDepth <= 0)
                    return "nests too deep";
                foreach (var property in element.EnumerateObject())
                {
                    if (!IsUnicode(() => property.Name))
                        return "holds text that is not valid Unicode";
                    if (Defect(property.Value, maxDepth - 1) is { } inner)
                        return inner;
                }
                return null;

            case JsonValueKind.Array:
                if (maxDepth <= 0)
                    return "nests too deep";
                foreach (var item in element.EnumerateArray())
                {
                    if (Defect(item, maxDepth - 1) is { } inner)
                        return inner;
                }
                return null;

            case JsonValueKind.String:
                return IsUnicode(element.GetString) ? null : "holds text that is not valid Unicode";

            case JsonValueKind.Number:
                if (JsonMarshal.GetRawUtf8Value(element).Length > MaxNumberChars)
                    return $"holds a number that is written with more than {MaxNumberChars} characters";
                return double.IsFinite(element.GetDouble()) ? null : "holds a number that is outside the range of a 64-bit floating-point number";

            case JsonValueKind.Undefined:
                return "is not a JSON value";

            default:
                return null;
        }
    }

    private static bool IsUnicode(Func<string?> read)
    {
        try
        {
            read();
            return true;
        }
        // The reader refuses an escape that is half of a surrogate pair.
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string NameOf(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException)
        {
            return Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8PropertyName(property));
        }
    }

    private static string TextOf(JsonElement element)
    {
        try
        {
            return element.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // The text between the quotes, with its escapes as plain characters.
            return Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8Value(element)[1..^1]);
        }
    }
}
