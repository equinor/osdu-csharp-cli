using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Search;
using SearchModels = Equinor.OsduCsharpClient.Search.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs index status</c> — per kind, how many records were indexed with a problem: a
/// warning (400), no schema (404), or another code.
/// </summary>
/// <remarks>
/// <para>The Indexer writes an <c>index</c> block on each record it indexes, with a
/// <c>statusCode</c> and a <c>trace</c>. 200 is a clean index. 400 is a warning: the record is
/// in the index, but some of it could not be, such as a failed CRS conversion. 404 is "schema
/// not found": the record is indexed without its schema, so only its metadata is searchable.</para>
///
/// <para>This is a Search aggregation by kind, once per column, merged into one row per kind.
/// Search returns no key when it aggregates by the numeric <c>index.statusCode</c>, so the
/// codes cannot be had from one aggregation by code. "Other" is its own query, every code but
/// 200, 400 and 404, rather than a difference of the others, so each column is counted, not
/// derived.</para>
///
/// <para>Search answers at most <see cref="BucketLimit"/> kinds per aggregation. A kind missing
/// from an answer that reached the limit may have been cut rather than have none, so that count
/// is unknown: null in JSON, blank in the table, and the kind's total with it.</para>
///
/// <para>The first aggregation runs alone: with an interactive sign-in and no cached token,
/// requests started together could each open a browser. The other two then run together.</para>
///
/// <para>Search applies ACLs, so the counts are of the records the caller can read. Records
/// that never reached the index have no <c>index</c> block and are not counted:
/// <c>index gaps</c> finds those.</para>
/// </remarks>
public static class IndexStatusCommand
{
    internal const string Warning = "index.statusCode:400";
    internal const string NoSchema = "index.statusCode:404";
    internal const string Other = "NOT index.statusCode:(200 OR 400 OR 404)";

    /// <summary>Search's default number of buckets per aggregation; a full answer may have been cut.</summary>
    internal const int BucketLimit = 1000;

    private const string Roles = "users.datalake.viewers, users.datalake.editors, users.datalake.admins or users.datalake.ops";

    private static readonly OutputSpec Columns = OutputSpec.Table("kinds",
        ("Kind", "kind"), ("Warnings (400)", "warnings"), ("No schema (404)", "noSchema"), ("Other", "other"), ("Total", "total"));

    public static Command Build()
    {
        var kind = new Option<string>("--kind", "-k")
        {
            Description = "Kinds to count, with wildcards per segment, e.g. *:*:work-product-component--WellLog:*. Defaults to every kind.",
            DefaultValueFactory = _ => "*:*:*:*",
        };

        var command = new Command("status",
            "Count, per kind, the records indexed with a warning (400), without a schema (404), or with another problem.");
        CommandRoles.Set(command, Roles);
        command.Options.Add(kind);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
            {
                var report = await CountAsync(context.Client.Search, parseResult.GetValue(kind)!, token);
                context.Output.WriteNote(Summary(report));
                return context.Output.Write(report.ToJsonString(), SpecFor(context.Output.Format));
            }, cancellationToken, Roles));

        return command;
    }

    /// <summary>The counts per kind matching <paramref name="kind"/>, with totals, as one object.</summary>
    internal static async Task<JsonObject> CountAsync(SearchClient search, string kind, CancellationToken cancellationToken)
    {
        var warnings = await ByKindAsync(search, kind, Warning, cancellationToken);
        var rest = await Task.WhenAll(
            ByKindAsync(search, kind, NoSchema, cancellationToken),
            ByKindAsync(search, kind, Other, cancellationToken));
        var columns = new[] { ("warnings", warnings), ("noSchema", rest[0]), ("other", rest[1]) };

        // A kind absent from an answer Search cut at its limit has an unknown count there.
        long? CountOf(Dictionary<string, long> answer, string name) =>
            answer.TryGetValue(name, out var count) ? count : answer.Count >= BucketLimit ? null : 0;

        var rows = columns.SelectMany(column => column.Item2.Keys).Distinct(StringComparer.Ordinal)
            .Select(name => (Name: name, Counts: columns.Select(column => CountOf(column.Item2, name)).ToArray()))
            .OrderByDescending(row => row.Counts.Sum(count => count ?? 0))
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .Select(row =>
            {
                var item = new JsonObject { ["kind"] = row.Name };
                for (var i = 0; i < columns.Length; i++)
                    item[columns[i].Item1] = row.Counts[i];
                item["total"] = row.Counts.Any(count => count is null) ? null : row.Counts.Sum();
                return (JsonNode)item;
            });

        var report = new JsonObject { ["kind"] = kind };
        foreach (var (field, answer) in columns)
            report[field] = answer.Values.Sum();
        report["total"] = columns.Sum(column => column.Item2.Values.Sum());
        report["truncated"] = columns.Any(column => column.Item2.Count >= BucketLimit);
        report["kinds"] = new JsonArray([.. rows]);
        return report;
    }

    /// <summary>The kinds table alone for a table; for JSON, the whole report, totals included.</summary>
    internal static OutputSpec SpecFor(OutputFormat format) => format == OutputFormat.Json ? OutputSpec.Raw : Columns;

    /// <summary>The one-line account of the counts, for stderr.</summary>
    internal static string Summary(JsonObject report)
    {
        var rows = report["kinds"]!.AsArray();
        if (rows.Count == 0)
            return $"No records of {report["kind"]} were indexed with a problem.";
        static long Known(JsonNode? count) => count?.GetValue<long>() ?? 0;
        string Part(string field, string what)
        {
            var kinds = rows.Count(row => Known(row![field]) > 0);
            return $"{Count(report[field]!)} {what}" + (kinds > 0 ? $" in {kinds} kind(s)" : "");
        }
        // The first row has the most; point at its commonest code.
        var top = rows[0]!;
        var code = new[] { ("warnings", Warning), ("noSchema", NoSchema), ("other", Other) }
            .MaxBy(pair => Known(top[pair.Item1])).Item2;
        var line =
            $"{Part("warnings", "with warnings (400)")}, {Part("noSchema", "without a schema (404)")}, " +
            $"{Part("other", "with another code")}. Only records you can read are counted. " +
            $"See why with: osducs record search -k {top["kind"]} -q \"{code}\" --returned-fields id,index";
        if (report["truncated"]!.GetValue<bool>())
            line += $" Search answered {BucketLimit} kinds or more, its limit, so some may be left out and a blank count is unknown; narrow it with --kind.";
        return line;
    }

    /// <summary>Records matching <paramref name="query"/>, counted per kind by one Search aggregation.</summary>
    private static async Task<Dictionary<string, long>> ByKindAsync(
        SearchClient search, string kind, string query, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["kind"] = kind,
            ["query"] = query,
            ["aggregateBy"] = "kind",
            ["returnedFields"] = new JsonArray("id"),
            ["limit"] = 1,
        };
        var result = await search.Query.PostAsync(
            (await KiotaJsonSerializer.DeserializeAsync<SearchModels.QueryRequest>(
                body.ToJsonString(), SearchModels.QueryRequest.CreateFromDiscriminatorValue, cancellationToken))!,
            cancellationToken: cancellationToken);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (JsonNode.Parse(await OsduJson.ToJsonAsync(result, cancellationToken) ?? "null")?["aggregations"] is JsonArray buckets)
            foreach (var bucket in buckets)
                if (bucket?["key"]?.GetValue<string>() is { } key)
                    counts[key] = bucket["count"]?.GetValue<long>() ?? 0;
        return counts;
    }

    private static string Count(JsonNode count) => count.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture);
}
