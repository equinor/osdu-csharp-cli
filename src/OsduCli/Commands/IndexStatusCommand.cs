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
/// <para>This is a Search aggregation by kind, once per code and in parallel, merged into one
/// row per kind. Search returns no key when it aggregates by the numeric
/// <c>index.statusCode</c>, so the codes cannot be had from one aggregation by code. The
/// "other" count is every record that is not 200, less the 400s and 404s.</para>
///
/// <para>Search applies ACLs, so the counts are of the records the caller can read. Records
/// that never reached the index have no <c>index</c> block and are not counted:
/// <c>index gaps</c> finds those.</para>
/// </remarks>
public static class IndexStatusCommand
{
    internal const string Warning = "index.statusCode:400";
    internal const string NoSchema = "index.statusCode:404";
    internal const string NotClean = "NOT index.statusCode:200";

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
        var warnings = ByKindAsync(search, kind, Warning, cancellationToken);
        var noSchema = ByKindAsync(search, kind, NoSchema, cancellationToken);
        var notClean = ByKindAsync(search, kind, NotClean, cancellationToken);
        await Task.WhenAll(warnings, noSchema, notClean);

        var rows = new JsonArray();
        var kinds = notClean.Result.Keys.Union(warnings.Result.Keys).Union(noSchema.Result.Keys);
        long totalWarnings = 0, totalNoSchema = 0, totalOther = 0;
        foreach (var (name, w, n, all) in kinds
                     .Select(name => (name, W: warnings.Result.GetValueOrDefault(name), N: noSchema.Result.GetValueOrDefault(name),
                         All: notClean.Result.GetValueOrDefault(name)))
                     .Select(row => (row.name, row.W, row.N, All: Math.Max(row.All, row.W + row.N)))
                     .OrderByDescending(row => row.All).ThenBy(row => row.name, StringComparer.Ordinal))
        {
            var other = all - w - n;
            totalWarnings += w;
            totalNoSchema += n;
            totalOther += other;
            rows.Add(new JsonObject
            {
                ["kind"] = name,
                ["warnings"] = w,
                ["noSchema"] = n,
                ["other"] = other,
                ["total"] = all,
            });
        }

        return new JsonObject
        {
            ["kind"] = kind,
            ["warnings"] = totalWarnings,
            ["noSchema"] = totalNoSchema,
            ["other"] = totalOther,
            ["total"] = totalWarnings + totalNoSchema + totalOther,
            ["truncated"] = new[] { warnings, noSchema, notClean }.Any(task => task.Result.Count >= BucketLimit),
            ["kinds"] = rows,
        };
    }

    /// <summary>The kinds table alone for a table; for JSON, the whole report, totals included.</summary>
    internal static OutputSpec SpecFor(OutputFormat format) => format == OutputFormat.Json ? OutputSpec.Raw : Columns;

    /// <summary>The one-line account of the counts, for stderr.</summary>
    internal static string Summary(JsonObject report)
    {
        var rows = report["kinds"]!.AsArray();
        if (rows.Count == 0)
            return $"No records of {report["kind"]} were indexed with a problem.";
        string Part(string field, string what)
        {
            var kinds = rows.Count(row => row![field]!.GetValue<long>() > 0);
            return $"{Count(report[field]!)} {what}" + (kinds > 0 ? $" in {kinds} kind(s)" : "");
        }
        // The first row has the most; point at its commonest code.
        var top = rows[0]!;
        var code = new[] { ("warnings", Warning), ("noSchema", NoSchema), ("other", NotClean) }
            .MaxBy(pair => top[pair.Item1]!.GetValue<long>()).Item2;
        var line =
            $"{Part("warnings", "with warnings (400)")}, {Part("noSchema", "without a schema (404)")}, " +
            $"{Part("other", "with another code")}. Only records you can read are counted. " +
            $"See why with: osducs record search -k {top["kind"]} -q \"{code}\" --returned-fields id,index";
        if (report["truncated"]!.GetValue<bool>())
            line += $" Search answered {BucketLimit} kinds or more, its limit, so some may be left out; narrow it with --kind.";
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
