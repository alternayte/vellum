using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vellum.Modules.Build;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vellum.Tests.Modules.Build;

/// <summary>
/// The YAML of the contract files reads back as the JSON it was made from, in a YAML 1.1
/// reader and in a YAML 1.2 reader. The check is independent of the writer: YamlDotNet parses
/// the text, and the implicit types of both YAML versions are the patterns of their
/// specifications, written out below.
/// </summary>
public partial class YamlRoundTripTests
{
    // YAML 1.1, https://yaml.org/type/: bool, null, int, float, timestamp, merge and value.
    [GeneratedRegex(@"\A(?:y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)\z")]
    private static partial Regex Bool11();

    [GeneratedRegex(@"\A(?:~|null|Null|NULL|)\z")]
    private static partial Regex Null();

    [GeneratedRegex(@"\A(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)\z")]
    private static partial Regex Int11();

    [GeneratedRegex(@"\A(?:[-+]?(?:[0-9][0-9_]*)?\.[0-9.]*(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))\z")]
    private static partial Regex Float11();

    [GeneratedRegex(@"\A(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}(?:[Tt]|[ \t]+)[0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9]{1,2}(?::[0-9]{2})?))?)\z")]
    private static partial Regex Timestamp11();

    [GeneratedRegex(@"\A(?:<<|=)\z")]
    private static partial Regex MergeOrValue11();

    // YAML 1.2.2, chapter 10.3.2: the core schema (the JSON schema is a part of it).
    [GeneratedRegex(@"\A(?:true|True|TRUE|false|False|FALSE)\z")]
    private static partial Regex Bool12();

    [GeneratedRegex(@"\A(?:[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)\z")]
    private static partial Regex Int12();

    [GeneratedRegex(@"\A(?:[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))\z")]
    private static partial Regex Float12();

    /// <summary>True when a reader of YAML 1.1 or of YAML 1.2 reads the plain scalar as something that is not a string.</summary>
    private static bool ReadsAsNonString(string plain) =>
        Bool11().IsMatch(plain) || Null().IsMatch(plain) || Int11().IsMatch(plain) || Float11().IsMatch(plain)
        || Timestamp11().IsMatch(plain) || MergeOrValue11().IsMatch(plain)
        || Bool12().IsMatch(plain) || Int12().IsMatch(plain) || Float12().IsMatch(plain);

    private static readonly string[] Tricky =
    [
        // booleans of YAML 1.1 and 1.2
        "y", "Y", "n", "N", "yes", "Yes", "YES", "no", "No", "NO", "on", "On", "ON", "off", "Off", "OFF",
        "true", "True", "TRUE", "false", "False", "FALSE",
        // nulls
        "null", "Null", "NULL", "~", "",
        // integers: decimal, signed, leading zero, octal, hex, binary, underscored, sexagesimal
        "0", "1", "-1", "+1", "007", "0o17", "017", "0x1F", "0X1F", "0b101", "1_000", "1_0", "12:30:00", "1:30", "190:20:30", "-12:30",
        // floats
        "1.0", "1.", ".5", "-.5", "1e3", "1E3", "1e+3", "1.5e-3", "1_000.5", "12:30:00.5", ".inf", "-.inf", "+.Inf", ".INF", ".nan", ".NaN", ".NAN",
        "3.0.0", "1.2.3", ".", "..", "1e400", "Infinity", "NaN", "inf", "nan",
        // timestamps and dates
        "2001-01-01", "2001-12-14t21:59:43.10-05:00", "2001-12-14 21:59:43.10 -5", "2001-12-15 2:59:43.10", "2001-12-14T21:59:43Z",
        // merge and value keys
        "<<", "=",
        // indicators and layout
        " leading", "trailing ", "a: b", "a #b", "#comment", "- item", "-", "--- ", "---", "...", "? key", ": value", "key:", "a:b",
        "[flow]", "{flow}", "a, b", "&anchor", "*alias", "!tag", "|", ">", "'single'", "\"double\"", "%directive", "@at", "`tick`",
        "line one\nline two", "tab\there", "cr\rhere", "back\\slash", "quote \" inside", "it's",
        // text that must stay as written
        "plain", "order-events", "POST /orders", "/orders/{id}", "application/json", "$ref", "x-vellum-truth",
        "00000000-0000-0000-0000-000000000001", "e3", "E", "o", "yes please", "nulls", "true-ish",
    ];

    /// <summary>The tricky strings, with the characters a source file should not hold written as code points.</summary>
    private static IEnumerable<string> AllTricky() => Tricky.Concat(
    [
        "bidi " + (char)0x202E + "evil" + (char)0x202C, "zero" + (char)0x200B + "width", "bom" + (char)0xFEFF,
        "nel" + (char)0x85 + "x", "ls" + (char)0x2028 + "x", "ps" + (char)0x2029 + "x", "nul" + (char)0 + "x", "bell" + (char)7 + "x",
        "del" + (char)0x7F + "x", "nbsp" + (char)0xA0 + "x", "emoji " + char.ConvertFromUtf32(0x1F600), "snowman " + (char)0x2603,
    ]);

    [Fact]
    public void Every_string_reads_back_as_the_same_string_in_yaml_1_1_and_1_2_as_a_value_a_key_and_a_list_item()
    {
        var strings = AllTricky().ToList();
        var document = new Dictionary<string, object>
        {
            ["values"] = strings,
            ["keys"] = strings.Distinct().ToDictionary(s => s, s => (object)s),
            ["nested"] = strings.Select(s => new Dictionary<string, object> { [s] = new[] { s, s }, ["inner"] = new Dictionary<string, object> { [s] = s } }).ToList(),
        };
        var json = JsonSerializer.SerializeToElement(document);

        var yaml = Encoding.UTF8.GetString(BuildText.Yaml(BuildText.Plain(json)!));

        AssertSame(json, Parse(yaml), "$");
        Assert.DoesNotContain('\r', yaml);
        Assert.DoesNotContain(yaml, c => c > 0x7E && char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Control
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }

    [Theory]
    [InlineData("0"), InlineData("-0"), InlineData("1"), InlineData("-17"), InlineData("9223372036854775807"), InlineData("-9223372036854775808")]
    [InlineData("18446744073709551615"), InlineData("1.0"), InlineData("1.50"), InlineData("0.1"), InlineData("-0.25"), InlineData("1e3"), InlineData("1E+3")]
    [InlineData("1.5e-7"), InlineData("1e-40"), InlineData("1e25"), InlineData("1.7976931348623157e308"), InlineData("5e-324"), InlineData("123456.789e3")]
    [InlineData("0.30000000000000004"), InlineData("7922816251426433759354395033.5")]
    public void A_number_that_yaml_can_hold_reads_back_as_the_same_number(string number)
    {
        var json = JsonDocument.Parse($$"""{ "n": {{number}}, "list": [{{number}}], "deep": { "m": { "n": {{number}} } } }""").RootElement;

        var yaml = Encoding.UTF8.GetString(BuildText.Yaml(BuildText.Plain(json)!));

        AssertSame(json, Parse(yaml), "$");
    }

    [Theory]
    [InlineData("1e400"), InlineData("-1e999"), InlineData("18446744073709551616"), InlineData("-9223372036854775809")]
    [InlineData("0.1234567890123456789012345678901234567890"), InlineData("1.00000000000000000000000000000000000001e5")]
    [InlineData("123456789012345678901234567890"), InlineData("1e-400")]
    public void A_number_that_yaml_cannot_hold_exactly_is_refused_with_its_path_and_value(string number)
    {
        var json = JsonDocument.Parse($$"""{ "components": { "schemas": [ { "maximum": {{number}} } ] } }""").RootElement;

        var refused = Assert.Throws<YamlNumberException>(() => BuildText.Yaml(BuildText.Plain(json)!));

        Assert.Equal("components.schemas[0].maximum", refused.Path);
        Assert.Equal(number, refused.Value);
    }

    [Fact]
    public void A_number_of_any_length_is_refused_and_named_with_a_bounded_value()
    {
        var json = JsonDocument.Parse($$"""{ "maximum": 1{{new string('0', 100_000)}} }""").RootElement;

        var refused = Assert.Throws<YamlNumberException>(() => BuildText.Yaml(BuildText.Plain(json)!));

        Assert.Equal("maximum", refused.Path);
        Assert.True(refused.Value.Length <= 80);
    }

    /// <summary>
    /// A schema with a number YAML cannot hold: the file that would hold it is not written,
    /// a blocker names the file, the place and the number, and the other files are written.
    /// </summary>
    [Fact]
    public void A_contract_file_with_an_unrepresentable_number_is_not_written_and_blocks_the_build()
    {
        static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");
        object[] events =
        [
            new ModelEvent.ElementAdded(G(3), ElementKind.App, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(G(4), ElementKind.App, "Billing", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.OperationAdded(G(10), G(3), "POST /orders", Transport.Http, G(71), null, 0, RetryPolicy.None),
            new ModelEvent.OperationAdded(G(11), G(4), "GET /invoices", Transport.Http, G(72), null, 0, RetryPolicy.None),
            new ModelEvent.ChannelAdded(G(20), "order-events", ChannelKind.Topic),
        ];
        var input = new BuildInput(
            G(99), "Orders", new ModelPin(G(98), 5, null, null), events.Aggregate(ModelState.Initial, ModelState.Evolve),
            [
                new BuildSchema(G(71), "OrderRequest", null, """{"type":"object","properties":{"qty":{"type":"number","maximum":1e400}}}""", 1),
                new BuildSchema(G(72), "Invoice", null, """{"type":"object","properties":{"total":{"type":"number","maximum":1e3,"multipleOf":0.01}}}""", 1),
            ],
            [], []);

        var bundle = Bundle.Build(input, BuildLimits.Default);

        var unwritten = $"openapi/{G(3)}.yaml";
        Assert.DoesNotContain(unwritten, bundle.Files.Keys);
        Assert.DoesNotContain(bundle.Manifest.Files, f => f.Path == unwritten);
        Assert.Contains($"openapi/{G(4)}.yaml", bundle.Files.Keys);
        Assert.Contains("asyncapi.yaml", bundle.Files.Keys);

        var blocker = Assert.Single(bundle.Manifest.Blockers, b => b.Code == "yaml-unrepresentable-number");
        Assert.Equal(unwritten, blocker.Subject);
        Assert.Contains("1e400", blocker.Message);
        Assert.Contains("components.schemas.OrderRequest.properties.qty.maximum", blocker.Message);
        Assert.False(bundle.Manifest.BuildReady);
        Assert.False(bundle.Manifest.SelfCheck.Passed);
        Assert.Contains(bundle.Manifest.SelfCheck.Notes, note => note.StartsWith(unwritten, StringComparison.Ordinal));

        var sdd = Encoding.UTF8.GetString(bundle.Files["SDD.md"]);
        Assert.Contains($"`{unwritten}`: OpenAPI 3.1 for Orders. This file is not in the bundle: see section 12.", sdd);
        Assert.Contains("| yaml-unrepresentable-number |", sdd);

        // The file that is written holds its numbers with the same values.
        var billing = Parse(Encoding.UTF8.GetString(bundle.Files[$"openapi/{G(4)}.yaml"]));
        var total = (YamlMappingNode)billing["components"]["schemas"]["Invoice"]["properties"]["total"];
        Assert.Equal(("1000", "0.01"), (((YamlScalarNode)total["maximum"]).Value, ((YamlScalarNode)total["multipleOf"]).Value));
    }

    private static YamlNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return Assert.Single(stream.Documents).RootNode;
    }

    /// <summary>The YAML node is the JSON value: the same structure, the same types and the same strings and numbers.</summary>
    private static void AssertSame(JsonElement json, YamlNode yaml, string path)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                var mapping = Assert.IsType<YamlMappingNode>(yaml);
                var properties = json.EnumerateObject().ToList();
                Assert.Equal(properties.Count, mapping.Children.Count);
                foreach (var property in properties)
                {
                    var entry = Assert.Single(mapping.Children, child => ((YamlScalarNode)child.Key).Value == property.Name);
                    AssertString(property.Name, entry.Key, $"{path} key");
                    AssertSame(property.Value, entry.Value, $"{path}.{property.Name}");
                }
                break;

            case JsonValueKind.Array:
                var sequence = Assert.IsType<YamlSequenceNode>(yaml);
                Assert.Equal(json.GetArrayLength(), sequence.Children.Count);
                foreach (var (item, index) in json.EnumerateArray().Select((item, index) => (item, index)))
                    AssertSame(item, sequence.Children[index], $"{path}[{index}]");
                break;

            case JsonValueKind.String:
                AssertString(json.GetString()!, yaml, path);
                break;

            case JsonValueKind.Number:
                var number = Assert.IsType<YamlScalarNode>(yaml);
                Assert.True(number.Style == ScalarStyle.Plain, $"{path}: a number is a plain scalar");
                var text = number.Value!;
                // A number in both versions: an integer of each, or a float of each.
                Assert.True((Int11().IsMatch(text) && Int12().IsMatch(text)) || (Float11().IsMatch(text) && Float12().IsMatch(text)),
                    $"{path}: '{text}' is not a number in YAML 1.1 and in YAML 1.2");
                Assert.DoesNotContain('_', text);
                Assert.DoesNotContain(':', text);
                Assert.True(Value(text) == Value(json.GetRawText()), $"{path}: '{text}' is not the number {json.GetRawText()}");
                break;

            default:
                var literal = Assert.IsType<YamlScalarNode>(yaml);
                Assert.Equal(ScalarStyle.Plain, literal.Style);
                Assert.Equal(json.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", _ => "null" }, literal.Value);
                break;
        }
    }

    private static void AssertString(string expected, YamlNode yaml, string path)
    {
        var scalar = Assert.IsType<YamlScalarNode>(yaml);
        Assert.Equal(expected, scalar.Value);
        Assert.True(scalar.Style != ScalarStyle.Plain || !ReadsAsNonString(expected),
            $"{path}: the string '{expected}' is a plain scalar that a YAML reader reads as another type");
    }

    /// <summary>The exact value of decimal number text: digits times a power of ten, with no trailing zero in the digits.</summary>
    private static (BigInteger Digits, int Power) Value(string text)
    {
        var parts = text.ToLowerInvariant().Split('e');
        var power = parts.Length > 1 ? int.Parse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var point = parts[0].IndexOf('.');
        if (point >= 0)
            power -= parts[0].Length - point - 1;
        var digits = BigInteger.Parse(parts[0].Replace(".", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (digits.IsZero)
            return (BigInteger.Zero, 0);
        while (digits % 10 == 0)
        {
            digits /= 10;
            power++;
        }
        return (digits, power);
    }
}
