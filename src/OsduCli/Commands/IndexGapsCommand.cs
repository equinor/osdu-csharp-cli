using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Indexer;
using Equinor.OsduCsharpClient.Search;
using Equinor.OsduCsharpClient.Storage;
using IndexerModels = Equinor.OsduCsharpClient.Indexer.Models;
using SearchModels = Equinor.OsduCsharpClient.Search.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs index gaps</c> — the records of a kind that Storage holds and Search has not
/// indexed, and the other way round; with <c>--reindex</c>, re-indexes the missing ones.
/// </summary>
/// <remarks>
/// <para>Search only finds records that reached the index. A record whose indexing failed
/// before it was written, or whose change event was lost, is simply absent, and nothing
/// reports it: the Indexer has no status endpoint, and the <c>index</c> block Search can be
/// asked about exists only on records that were indexed. Comparing the ids each service
/// holds is the one way a client can see the gap.</para>
///
/// <para>Search is read first and Storage second, and every difference is looked up again in
/// Search before it is reported. A record written while the command runs is in Storage's
/// list and not in Search's; one deleted is the other way round. Either is resolved by the
/// second look once the Indexer has caught up, so what is left is a real gap, or a record the
/// Indexer is still working on.</para>
///
/// <para>Storage lists records only by exact kind, so the kind cannot have wildcards. Both
/// lists are held in memory: a kind of a few million records is fine, the whole partition is
/// not what this is for.</para>
/// </remarks>
public static class IndexGapsCommand
{
    /// <summary>Ids per Storage and Search page: the most Search answers, and well within Storage's.</summary>
    internal const int PageSize = 1000;

    /// <summary>Ids per second look in Search, short enough for a query string.</summary>
    internal const int RecheckBatch = 100;

    /// <summary>The most ids the Indexer re-indexes per request, from its spec.</summary>
    internal const int ReindexBatch = 1000;

    internal const string MissingFromSearch = "not in search";
    internal const string MissingFromStorage = "not in storage";

    private const string ReadRoles =
        "service.storage.admin, and one of users.datalake.viewers, editors, admins or ops for Search";
    private const string ReindexRoles = "service.search.admin";

    private static readonly OutputSpec Columns = OutputSpec.Table("results", ("Id", "id"), ("Gap", "gap"));

    public static Command Build()
    {
        var kind = new Option<string>("--kind", "-k")
        {
            Description = "Exact kind to check, e.g. osdu:wks:master-data--Well:1.0.0. No wildcards: Storage lists records only by exact kind.",
            Required = true,
        };
        var reindex = new Option<bool>("--reindex")
        {
            Description = "Re-index the records Search does not have, " + ReindexBatch + " per request. Needs " + ReindexRoles + ".",
        };
        var yes = new Option<bool>("--yes")
        {
            Description = "With --reindex, go ahead without asking.",
        };

        var command = new Command("gaps",
            "Find the records of a kind that Storage holds and Search has not indexed, and the other way round.");
        CommandRoles.Set(command, $"{ReadRoles}; with --reindex, {ReindexRoles}", operatorRole: true);
        command.Options.Add(kind);
        command.Options.Add(reindex);
        command.Options.Add(yes);

        command.Validators.Add(result =>
        {
            if (OptionValues.Usable(result, kind) is { } value && value.Contains('*'))
                result.AddError("--kind cannot have wildcards: Storage lists records only by exact kind. Run it once per version.");
            if (result.GetResult(yes) is not null && !OptionValues.Usable(result, reindex))
                result.AddError("--yes applies only with --reindex.");
        });

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
            {
                var kindValue = parseResult.GetValue(kind)!;
                var progress = Console.IsErrorRedirected ? null : Console.Error;
                var report = await FindAsync(context.Client.Storage, context.Client.Search, kindValue, progress, token);

                var missing = Ids(report, MissingFromSearch);
                context.Output.WriteNote(Summary(report));

                if (parseResult.GetValue(reindex) && missing.Count > 0)
                {
                    if (!Confirmation.Confirm(
                            $"Re-index {Count(missing.Count)} record(s) of {kindValue} that Search does not have.",
                            parseResult.GetValue(yes), context))
                        return 1;
                    var requested = await ReindexAsync(context.Client.Indexer, missing, token);
                    context.Output.WriteNote(
                        $"Asked the Indexer to re-index {Count(requested)} record(s). Run the command again in a few minutes to check.");
                }

                return context.Output.Write(report.ToJsonString(), Columns);
            }, cancellationToken, $"{ReadRoles}; with --reindex, {ReindexRoles}",
                "Storage lists a kind's records only for service.storage.admin, which most users lack. Without it, " +
                "`record search --query \"NOT index.statusCode:200\"` finds the records indexed with warnings, though not the missing ones."));

        return command;
    }

    /// <summary>
    /// Compares the ids of <paramref name="kind"/> in Search and Storage, looks every
    /// difference up again in Search, and returns the counts and the gaps as one object.
    /// </summary>
    internal static async Task<JsonObject> FindAsync(
        StorageClient storage, SearchClient search, string kind, TextWriter? progress, CancellationToken cancellationToken)
    {
        progress?.WriteLine($"Reading the ids Search has for {kind}");
        var indexed = await SearchIdsAsync(search, kind, progress, cancellationToken);
        progress?.WriteLine($"Reading the ids Storage has for {kind}");
        var stored = await StorageIdsAsync(storage, kind, progress, cancellationToken);

        var notIndexed = stored.Where(id => !indexed.Contains(id)).ToList();
        var notStored = indexed.Where(id => !stored.Contains(id)).ToList();

        // The second look: what Search has now, among the ids that differed.
        var differing = notIndexed.Concat(notStored).ToList();
        if (differing.Count > 0)
            progress?.WriteLine($"Looking up {Count(differing.Count)} difference(s) in Search again");
        var nowIndexed = await IndexedNowAsync(search, kind, differing, cancellationToken);

        var gaps = new JsonArray();
        foreach (var id in notIndexed.Where(id => !nowIndexed.Contains(id)).Order(StringComparer.Ordinal))
            gaps.Add(new JsonObject { ["id"] = id, ["gap"] = MissingFromSearch });
        foreach (var id in notStored.Where(nowIndexed.Contains).Order(StringComparer.Ordinal))
            gaps.Add(new JsonObject { ["id"] = id, ["gap"] = MissingFromStorage });

        return new JsonObject
        {
            ["kind"] = kind,
            ["storageCount"] = stored.Count,
            ["searchCount"] = indexed.Count,
            ["results"] = gaps,
        };
    }

    /// <summary>Re-indexes <paramref name="ids"/> in batches, and returns how many were sent.</summary>
    internal static async Task<int> ReindexAsync(IndexerClient indexer, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        foreach (var batch in ids.Chunk(ReindexBatch))
        {
            var body = new IndexerModels.ReindexRecordsRequest { RecordIds = [.. batch] };
            await indexer.Reindex.Records.PostAsync(body, cancellationToken: cancellationToken);
        }
        return ids.Count;
    }

    /// <summary>The one-line account of a comparison, for stderr.</summary>
    internal static string Summary(JsonObject report)
    {
        var missing = Ids(report, MissingFromSearch).Count;
        var orphaned = Ids(report, MissingFromStorage).Count;
        var line = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $"Storage has {Count(report["storageCount"]!.GetValue<int>())}, Search has {Count(report["searchCount"]!.GetValue<int>())}. ");
        if (missing == 0 && orphaned == 0)
            return line.Append("No gaps.").ToString();
        line.Append(CultureInfo.InvariantCulture, $"{Count(missing)} not in Search, {Count(orphaned)} not in Storage.");
        if (missing > 0)
            line.Append(" Records written in the last few minutes may still be indexing; --reindex re-indexes the missing ones.");
        if (orphaned > 0)
            line.Append(" Records not in Storage are deleted or inactive there but still in the index; re-indexing does not remove them.");
        return line.ToString();
    }

    internal static List<string> Ids(JsonObject report, string gap) =>
        [.. report["results"]!.AsArray()
            .Where(item => item!["gap"]!.GetValue<string>() == gap)
            .Select(item => item!["id"]!.GetValue<string>())];

    private static async Task<HashSet<string>> SearchIdsAsync(
        SearchClient search, string kind, TextWriter? progress, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["kind"] = kind, ["returnedFields"] = new JsonArray("id") };
        var json = await CursorPaging.CollectAsync(
            request, limit: null, PageSize, "limit", "cursor", "results", "totalCount",
            async (page, pageCancellation) => await OsduJson.ToJsonAsync(
                await search.Query_with_cursor.PostAsync(
                    (await KiotaJsonSerializer.DeserializeAsync<SearchModels.CursorQueryRequest>(
                        page.ToJsonString(), SearchModels.CursorQueryRequest.CreateFromDiscriminatorValue, pageCancellation))!,
                    cancellationToken: pageCancellation), pageCancellation),
            (cursor, releaseCancellation) => search.Query_with_cursor[cursor].DeleteAsync(cancellationToken: releaseCancellation),
            progress, cancellationToken);
        return IdsOf(json, item => item?["id"]);
    }

    private static async Task<HashSet<string>> StorageIdsAsync(
        StorageClient storage, string kind, TextWriter? progress, CancellationToken cancellationToken)
    {
        var json = await CursorPaging.CollectAsync(
            new JsonObject(), limit: null, PageSize, "limit", "cursor", "results", totalProperty: null,
            async (page, pageCancellation) => await OsduJson.ToJsonAsync(await storage.Query.Records.GetAsync(configuration =>
            {
                configuration.QueryParameters.Kind = kind;
                configuration.QueryParameters.Limit = page["limit"]!.GetValue<int>();
                configuration.QueryParameters.Cursor = page["cursor"]?.GetValue<string>();
            }, pageCancellation), pageCancellation),
            // Storage's cursor is a continuation token, not something it holds open.
            (_, _) => Task.CompletedTask,
            progress, cancellationToken);
        // Storage answers bare id strings, not objects.
        return IdsOf(json, item => item);
    }

    /// <summary>Which of <paramref name="ids"/> Search has indexed now.</summary>
    private static async Task<HashSet<string>> IndexedNowAsync(
        SearchClient search, string kind, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(RecheckBatch))
        {
            var body = new JsonObject
            {
                ["kind"] = kind,
                ["query"] = IdQuery(batch),
                ["returnedFields"] = new JsonArray("id"),
                ["limit"] = batch.Length,
            };
            var result = await search.Query.PostAsync(
                (await KiotaJsonSerializer.DeserializeAsync<SearchModels.QueryRequest>(
                    body.ToJsonString(), SearchModels.QueryRequest.CreateFromDiscriminatorValue, cancellationToken))!,
                cancellationToken: cancellationToken);
            found.UnionWith(IdsOf(await OsduJson.ToJsonAsync(result, cancellationToken), item => item?["id"]));
        }
        return found;
    }

    /// <summary>A Lucene query matching any of <paramref name="ids"/> exactly.</summary>
    internal static string IdQuery(IEnumerable<string> ids) =>
        "id:(" + string.Join(" OR ", ids.Select(id => "\"" + id.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + ")";

    private static HashSet<string> IdsOf(string? json, Func<JsonNode?, JsonNode?> id)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (JsonNode.Parse(json ?? "null")?["results"] is JsonArray results)
            foreach (var item in results)
                if (id(item) is JsonValue value && value.TryGetValue<string>(out var text))
                    ids.Add(text);
        return ids;
    }

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
