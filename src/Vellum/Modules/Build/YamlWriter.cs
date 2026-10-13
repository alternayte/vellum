using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Vellum.Modules.Build;

/// <summary>A JSON number as its source text, so the YAML writer decides from the exact value and not from a rounded one.</summary>
internal readonly record struct YamlNumber(string Text);

/// <summary>
/// A number that YAML cannot hold with the same value in every reader: not finite, an integer
/// outside 64 bits, or a fraction that no decimal or double holds exactly.
/// </summary>
internal sealed class YamlNumberException(string path, string value) : Exception(
    $"The number {value} at {path} cannot be written to YAML with the same value in every reader.")
{
    /// <summary>Where the number is in the document, such as <c>components.schemas.Order.properties.qty.maximum</c>.</summary>
    public string Path { get; } = path;

    /// <summary>The number as stored, cut to a bounded length.</summary>
    public string Value { get; } = value;
}

/// <summary>
/// The YAML of the contract files: block style, two-space indent, <c>\n</c> line ends, keys in
/// the order given. The same document gives the same bytes.
/// <para>
/// A string reads back as the same string in a YAML 1.1 reader and in a YAML 1.2 reader
/// (failsafe, JSON or core schema). A string is written plain only when it starts with a
/// letter, <c>/</c>, <c>_</c> or <c>$</c>, holds only letters, digits, <c>_ . / { } $ + -</c>
/// and single spaces, and is not a word that some reader takes for a boolean, a null or a
/// number (<c>yes</c>, <c>on</c>, <c>null</c>, <c>inf</c> and so on, in any case). Every other
/// string, and so every string that looks like a number, a time, a date, <c>~</c>,
/// <c>&lt;&lt;</c> or <c>=</c>, is written in double quotes. Keys follow the same rule.
/// </para>
/// <para>
/// A number is written only when it is finite and its text reads back as the same value:
/// an integer inside 64 bits, or a fraction that a decimal or a double holds exactly. A
/// fraction always has a decimal point, and an exponent always has a sign, as YAML 1.1 needs.
/// Any other number is refused with <see cref="YamlNumberException"/>.
/// </para>
/// </summary>
internal static partial class YamlWriter
{
    /// <summary>A plain or quoted key longer than this needs the explicit <c>? key</c> form (YAML allows 1024 characters).</summary>
    private const int MaxImplicitKey = 1000;

    /// <summary>A number of more characters is refused unread: no JSON number that a contract needs is this long.</summary>
    private const int MaxNumberChars = 400;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "n", "yes", "no", "on", "off", "true", "false", "null", "nan", "inf", "infinity", "nil", "none",
    };

    [GeneratedRegex(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")]
    private static partial Regex GuidShape();

    [GeneratedRegex(@"\A[A-Za-z/_$][A-Za-z0-9_./{}$+-]*(?: [A-Za-z0-9_./{}$+-]+)*\z")]
    private static partial Regex Plain();

    [GeneratedRegex(@"\A-?[0-9]+\z")]
    private static partial Regex Integer();

    [GeneratedRegex(@"\A(-?)([0-9]+)(?:\.([0-9]+))?(?:[eE]([-+]?[0-9]{1,9}))?\z")]
    private static partial Regex JsonNumber();

    /// <param name="document">
    /// A tree of maps (<see cref="IEnumerable{T}"/> of string and value pairs), lists, strings,
    /// booleans, <see cref="int"/>, <see cref="long"/>, <see cref="YamlNumber"/> and null.
    /// </param>
    /// <exception cref="YamlNumberException">The document holds a number that cannot be written.</exception>
    public static string Write(object? document)
    {
        var text = new StringBuilder();
        switch (document)
        {
            case IEnumerable<KeyValuePair<string, object?>> map when map.Any():
                Map(text, map, 0, "", null);
                break;
            case IEnumerable<object?> list when list.Any():
                List(text, list, 0, "");
                break;
            default:
                text.Append(Leaf(document, "$")).Append('\n');
                break;
        }
        return text.ToString();
    }

    private static void Map(StringBuilder text, IEnumerable<KeyValuePair<string, object?>> map, int indent, string path, string? firstPrefix)
    {
        var first = true;
        foreach (var (key, value) in map)
        {
            var prefix = first && firstPrefix is not null ? firstPrefix : new string(' ', indent);
            first = false;
            var at = path.Length == 0 ? key : $"{path}.{key}";
            var name = String(key);
            if (name.Length > MaxImplicitKey)
                text.Append(prefix).Append("? ").Append(name).Append('\n').Append(' ', indent).Append(':');
            else
                text.Append(prefix).Append(name).Append(':');

            switch (value)
            {
                case IEnumerable<KeyValuePair<string, object?>> child when child.Any():
                    text.Append('\n');
                    Map(text, child, indent + 2, at, null);
                    break;
                case IEnumerable<object?> items when items.Any():
                    text.Append('\n');
                    List(text, items, indent + 2, at);
                    break;
                default:
                    text.Append(' ').Append(Leaf(value, at)).Append('\n');
                    break;
            }
        }
    }

    private static void List(StringBuilder text, IEnumerable<object?> list, int indent, string path)
    {
        var index = 0;
        foreach (var item in list)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{path}[{index++}]");
            switch (item)
            {
                case IEnumerable<KeyValuePair<string, object?>> child when child.Any():
                    Map(text, child, indent + 2, at, new string(' ', indent) + "- ");
                    break;
                case IEnumerable<object?> items when items.Any():
                    text.Append(' ', indent).Append("-\n");
                    List(text, items, indent + 2, at);
                    break;
                default:
                    text.Append(' ', indent).Append("- ").Append(Leaf(item, at)).Append('\n');
                    break;
            }
        }
    }

    /// <summary>A scalar, or an empty map or list in flow form.</summary>
    private static string Leaf(object? value, string path) => value switch
    {
        null => "null",
        true => "true",
        false => "false",
        string text => String(text),
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        YamlNumber number => Number(number.Text, path),
        IEnumerable<KeyValuePair<string, object?>> => "{}",
        IEnumerable<object?> => "[]",
        _ => throw new NotSupportedException($"The YAML writer has no form for a value of type {value.GetType().Name} at {path}."),
    };

    /// <summary>A string as a plain scalar when every reader reads it as that string, else in double quotes.</summary>
    internal static string String(string text)
    {
        if (GuidShape().IsMatch(text) || (Plain().IsMatch(text) && !Reserved.Contains(text)))
            return text;

        var quoted = new StringBuilder(text.Length + 2).Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': quoted.Append("\\\""); continue;
                case '\\': quoted.Append("\\\\"); continue;
                case '\n': quoted.Append("\\n"); continue;
                case '\r': quoted.Append("\\r"); continue;
                case '\t': quoted.Append("\\t"); continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var codePoint = char.ConvertToUtf32(c, text[i + 1]);
                if (CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned)
                    quoted.Append("\\U").Append(codePoint.ToString("X8", CultureInfo.InvariantCulture));
                else
                    quoted.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            // Everything a reader may not print, or may print in another place: control and
            // format characters, line and paragraph separators, lone surrogates, non-characters.
            var hidden = char.IsSurrogate(c) || c >= (char)0xFFFE || c == (char)0x180E
                || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned;
            if (!hidden)
                quoted.Append(c);
            else if (c < (char)0x100)
                quoted.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                quoted.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>The text of a JSON number as a YAML number with the same value.</summary>
    internal static string Number(string raw, string path)
    {
        YamlNumberException Refused() => new(path, raw.Length <= 80 ? raw : raw[..77] + "...");

        if (raw.Length > MaxNumberChars || Exact(raw) is not { } exact)
            throw Refused();

        if (Integer().IsMatch(raw))
        {
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                return whole.ToString(CultureInfo.InvariantCulture);
            if (ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var large))
                return large.ToString(CultureInfo.InvariantCulture);
            throw Refused();
        }

        // A decimal keeps the digits as written, so "1.50" stays "1.50".
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            var text = number.ToString(CultureInfo.InvariantCulture);
            if (Exact(text) == exact)
            {
                if (text.Contains('.'))
                    return text;
                // A whole value written with an exponent, such as 1e3: an integer when 64 bits hold it.
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                    || ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    return text;
            }
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating))
        {
            var shortest = floating.ToString("R", CultureInfo.InvariantCulture);
            if (Exact(shortest) == exact)
            {
                var e = shortest.IndexOf('E');
                var mantissa = e < 0 ? shortest : shortest[..e];
                if (!mantissa.Contains('.'))
                    mantissa += ".0";
                if (e < 0)
                    return mantissa;
                var exponent = shortest[(e + 1)..];
                return $"{mantissa}e{(exponent[0] is '-' or '+' ? "" : "+")}{exponent}";
            }
        }

        throw Refused();
    }

    /// <summary>
    /// The exact value of number text as digits and a power of ten, with no trailing zero in
    /// the digits. Two texts are the same number when these are equal. Null when the text is
    /// not a number of the JSON form.
    /// </summary>
    private static (BigInteger Digits, long Power)? Exact(string text)
    {
        if (JsonNumber().Match(text) is not { Success: true } match)
            return null;

        var fraction = match.Groups[3].Value;
        var digits = BigInteger.Parse(match.Groups[2].Value + fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        if (digits.IsZero)
            return (BigInteger.Zero, 0);

        var power = (match.Groups[4].Success ? long.Parse(match.Groups[4].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0) - fraction.Length;
        while ((digits % 10).IsZero)
        {
            digits /= 10;
            power++;
        }
        return (match.Groups[1].Length > 0 ? -digits : digits, power);
    }
}
