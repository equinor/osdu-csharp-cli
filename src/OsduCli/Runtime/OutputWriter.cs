using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Equinor.OsduCli.Runtime;

/// <summary>How a command's result is rendered to stdout.</summary>
public enum OutputFormat
{
    /// <summary>Human-friendly aligned table, falling back to JSON when no columns apply.</summary>
    Table,
    /// <summary>Indented JSON.</summary>
    Json,
}

/// <summary>Renders a command's JSON response according to its <see cref="OutputSpec"/>.</summary>
public sealed class OutputWriter(OutputFormat format, TextWriter output)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Search reports at most this many matches without trackTotalCount.</summary>
    private const long SearchCountCap = 10_000;

    /// <summary>
    /// Writes <paramref name="json"/> and returns the process exit code (always 0 — a failed
    /// call throws before reaching here).
    /// </summary>
    public int Write(string? json, OutputSpec spec)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;

        using var doc = JsonDocument.Parse(json);
        var element = Unwrap(doc.RootElement, spec.Root);

        if (format == OutputFormat.Json || spec.Columns.Count == 0)
        {
            output.WriteLine(JsonSerializer.Serialize(element, Indented));
            return 0;
        }

        WriteTable(element, spec.Columns);
        return 0;
    }

    /// <summary>Writes a plain message (for endpoints with no response body).</summary>
    public int WriteMessage(string message)
    {
        if (format == OutputFormat.Table) output.WriteLine(message);
        return 0;
    }

    /// <summary>
    /// Descends into <paramref name="root"/> if the response wraps its payload in one
    /// property. A missing property yields the original element rather than an error: OSDU
    /// services vary in whether they include an empty envelope, and the Python CLI's
    /// <c>results || {...}</c> idiom has the same forgiving behaviour.
    /// </summary>
    private static JsonElement Unwrap(JsonElement element, string? root)
    {
        if (root is null) return element;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(root, out var inner)
            ? inner
            : element;
    }

    private void WriteTable(JsonElement element, IReadOnlyList<(string Header, string Path)> columns)
    {
        var rows = element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToList()
            : [element];

        if (rows.Count == 0)
        {
            output.WriteLine("(no results)");
            return;
        }

        var cells = rows
            .Select(row => columns.Select(c => Resolve(row, c.Path)).ToArray())
            .ToList();

        var widths = columns
            .Select((c, i) => Math.Max(c.Header.Length, cells.Max(r => r[i].Length)))
            .ToArray();

        output.WriteLine(Join(columns.Select(c => c.Header), widths));
        output.WriteLine(Join(widths.Select(w => new string('-', w)), widths));
        foreach (var row in cells) output.WriteLine(Join(row, widths));
    }

    private static string Join(IEnumerable<string> values, int[] widths)
    {
        var builder = new StringBuilder();
        var index = 0;
        foreach (var value in values)
        {
            if (index > 0) builder.Append("  ");
            builder.Append(index == widths.Length - 1 ? value : value.PadRight(widths[index]));
            index++;
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Resolves a dotted path such as <c>acl.owners</c> against one element. Anything that
    /// is not a scalar at the end of the path is rendered as compact JSON, so a column
    /// pointing at an object or array still prints something useful.
    /// </summary>
    private static string Resolve(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
                return string.Empty;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.ToString(),
            _ => JsonSerializer.Serialize(current),
        };
    }

    /// <summary>
    /// Writes the match count carried alongside the results, when the response has one.
    /// </summary>
    /// <remarks>
    /// Search reports <c>totalCount</c> next to <c>results</c>, outside the projection root,
    /// so it would otherwise be discarded — leaving <c>--track-total-count</c> with nothing
    /// to show for itself. Table mode only: in JSON mode the field is already in the
    /// document the caller is piping.
    /// </remarks>
    public void WriteTotal(string? json, string path)
    {
        if (format != OutputFormat.Table || string.IsNullOrWhiteSpace(json)) return;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        if (!document.RootElement.TryGetProperty(path, out var total)) return;
        if (total.ValueKind != JsonValueKind.Number) return;

        // Search caps the reported count at exactly 10000 unless trackTotalCount is set.
        // Printing a bare "10,000" where the truth is 141,286 is worse than printing
        // nothing, so the cap is marked and the way past it named.
        // Invariant, not current culture: a thousands separator that changes with the
        // machine's locale makes output that scripts grep and humans compare unstable. The
        // project sets InvariantGlobalization today, but this should not depend on that
        // staying true.
        var count = total.GetInt64().ToString("N0", CultureInfo.InvariantCulture);
        output.WriteLine(total.GetInt64() == SearchCountCap
            ? $"{count}+ matching records (use --track-total-count for the exact figure)"
            : $"{count} matching records");
    }
}
