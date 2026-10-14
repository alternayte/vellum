using System.Globalization;
using System.Security.Claims;
using System.Text;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Costs.Prices;

/// <param name="Csv">The text of the file: a header line, then one price on each line.</param>
public sealed record PriceCsvRequest(string? Csv);

/// <param name="Imported">The number of entries added. Every line of the file is one entry.</param>
public sealed record PriceCsvResult(int Imported, IReadOnlyList<PriceEntryDto> Entries);

/// <summary>
/// Custom prices from a CSV file. Every line is checked first; a file with one bad line adds
/// nothing, and the answer names each bad line and column. The person who sends the file is
/// the acceptor of every entry.
/// </summary>
public static class PriceCsv
{
    public const int MaxChars = 1024 * 1024;
    public const int MaxRows = 500;

    /// <summary>The columns, in the names of a custom price. <c>description</c> is optional.</summary>
    public static readonly string[] Columns = ["provider", "region", "sku", "unit", "unitPrice", "currency", "effectiveDate"];
    private const string Description = "description";

    public static async Task<IResult> Import(
        Guid workspaceId, PriceCsvRequest request,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var (rows, errors) = Parse(request.Csv);
        if (errors.Count > 0)
            return Invalid(errors);

        var now = time.GetUtcNow();
        var entries = rows.Select(row => PriceRules.Custom(workspaceId, row, userId, now)).ToList();
        if (await PriceBook.AddManyAsync(db, workspaceId, entries, PriceAudit.Added, ct) is { } refusal)
            return refusal;
        return Results.Created($"/api/workspaces/{workspaceId}/prices",
            new PriceCsvResult(entries.Count, entries.Select(e => PriceEntryDto.From(e)).ToList()));
    }

    /// <summary>
    /// The prices of the file, or the errors. The field of an error is <c>line N</c> or
    /// <c>line N.column</c>, with N the line of the file from 1, the header included.
    /// </summary>
    public static (List<CustomPriceRequest> Rows, List<ValidationError> Errors) Parse(string? csv)
    {
        var rows = new List<CustomPriceRequest>();
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(csv))
            return (rows, [new("csv", $"A file with a header line ({string.Join(", ", Columns)}) and at least one price is required.")]);
        if (csv.Length > MaxChars)
            return (rows, [new("csv", $"The file is larger than {MaxChars / 1024} KB. Split it.")]);

        var lines = Records(csv, out var syntax);
        if (syntax is { } broken)
            return (rows, [broken]);

        var header = lines.FirstOrDefault(l => !l.Blank);
        if (header is null)
            return (rows, [new("csv", "The file is empty.")]);

        var names = header.Fields.Select(f => f.Trim()).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var known = Columns.Append(Description).FirstOrDefault(c => string.Equals(c, names[i], StringComparison.OrdinalIgnoreCase));
            if (known is null)
                errors.Add(new($"line {header.Line}", $"Unknown column '{Short(names[i])}'. The columns are: {string.Join(", ", Columns)} and optionally {Description}."));
            else if (!index.TryAdd(known, i))
                errors.Add(new($"line {header.Line}", $"The column {known} appears twice."));
        }
        foreach (var missing in Columns.Where(c => !index.ContainsKey(c)))
            errors.Add(new($"line {header.Line}", $"The column {missing} is missing."));
        if (errors.Count > 0)
            return (rows, errors);

        var data = lines.SkipWhile(l => l != header).Skip(1).Where(l => !l.Blank).ToList();
        if (data.Count == 0)
            return (rows, [new("csv", "The file has a header line and no price.")]);
        if (data.Count > MaxRows)
            return (rows, [new("csv", $"The file has {data.Count} prices. Import at most {MaxRows} at a time.")]);

        foreach (var line in data)
        {
            if (line.Fields.Count != names.Count)
            {
                errors.Add(new($"line {line.Line}", $"The line has {line.Fields.Count} values and the header has {names.Count}."));
                continue;
            }

            string Get(string column) => line.Fields[index[column]].Trim();
            var lineErrors = new List<ValidationError>();
            decimal? unitPrice = null;
            if (decimal.TryParse(Get("unitPrice"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var price))
                unitPrice = price;
            else if (Get("unitPrice").Length > 0)
                lineErrors.Add(new("unitPrice", "Must be a number with a dot for decimals, for example 0.0416."));
            var effectiveDate = default(DateOnly);
            if (Get("effectiveDate").Length > 0
                && !DateOnly.TryParseExact(Get("effectiveDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out effectiveDate))
                lineErrors.Add(new("effectiveDate", "Must be a date as YYYY-MM-DD, for example 2026-01-01."));

            var row = new CustomPriceRequest(
                Get("provider"), Get("region"), Get("sku"),
                index.ContainsKey(Description) ? Get(Description) : null,
                Get("unit"), unitPrice, Get("currency"), effectiveDate);
            // A value that did not parse is reported once, as such, and not again as missing.
            lineErrors.AddRange(PriceRules.Check(row).Where(e => lineErrors.All(l => l.Field != e.Field)));
            errors.AddRange(lineErrors.Select(e => new ValidationError($"line {line.Line}.{e.Field}", e.Message)));
            if (lineErrors.Count == 0)
                rows.Add(row);
        }

        return (errors.Count > 0 ? [] : rows, errors);
    }

    private sealed record Record(int Line, List<string> Fields)
    {
        public bool Blank => Fields is [var only] && string.IsNullOrWhiteSpace(only);
    }

    /// <summary>
    /// The records of RFC 4180 text: fields split by commas, a field in double quotes may
    /// hold commas, line breaks and doubled quotes. <paramref name="error"/> is set for a quote
    /// that is not closed or text after a closing quote.
    /// </summary>
    private static List<Record> Records(string csv, out ValidationError? error)
    {
        error = null;
        var records = new List<Record>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var i = 0;
        if (csv.StartsWith('﻿'))
            i = 1;

        while (i <= csv.Length)
        {
            if (i == csv.Length)
            {
                fields.Add(field.ToString());
                records.Add(new Record(recordLine, fields));
                break;
            }

            var c = csv[i];
            if (c == '"' && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                i++;
                var closed = false;
                while (i < csv.Length)
                {
                    if (csv[i] == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        closed = true;
                        i++;
                        break;
                    }
                    if (csv[i] == '\n')
                        line++;
                    field.Append(csv[i]);
                    i++;
                }
                if (!closed)
                {
                    error = new ValidationError($"line {recordLine}", "A quote is not closed.");
                    return records;
                }
                while (i < csv.Length && csv[i] is ' ' or '\t')
                    i++;
                if (i < csv.Length && csv[i] is not (',' or '\r' or '\n'))
                {
                    error = new ValidationError($"line {line}", "Text follows a closing quote. Put the whole value in quotes.");
                    return records;
                }
                continue;
            }

            switch (c)
            {
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r' or '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(new Record(recordLine, fields));
                    fields = [];
                    i += c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n' ? 2 : 1;
                    line++;
                    recordLine = line;
                    if (i == csv.Length)
                        return records;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        return records;
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
