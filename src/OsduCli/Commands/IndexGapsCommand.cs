using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Indexer;
using Equinor.OsduCsharpClient.Search;
using Equinor.OsduCsharpClient.Storage;
using Equinor.OsduCsharpClient.Facade;
using IndexerModels = Equinor.OsduCsharpClient.Indexer.Models;
using SearchModels = Equinor.OsduCsharpClient.Search.Models;
using StorageModels = Equinor.OsduCsharpClient.Storage.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs index gaps</c> — the records of a kind that Storage holds and Search has not
/// indexed, and the other way round; or, with <c>--ids-from</c>, which of a list of records
/// Search lacks. With <c>--reindex</c>, re-indexes the missing ones.
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
/// <para>A record Storage lists and Search lacks is then fetched from Storage before it is
/// reported. Storage's list ignores ACLs and Search applies them, so an admin outside a
/// record's data groups finds it in one and not the other: the fetch applies the ACL too,
/// and leaves such a record out, so it is reported as <i>no access</i> rather than as a gap,
/// and is not re-indexed. A record deleted since Storage listed it is answered as invalid by
/// the fetch, and dropped.</para>
///
/// <para>Storage lists records only by exact kind, so the kind cannot have wildcards. Both
/// lists are held in memory: a kind of a few million records is fine, the whole partition is
/// not what this is for.</para>
///
/// <para>Listing a kind needs <c>service.storage.admin</c>, because Storage's list ignores
/// record ACLs. <c>--ids-from</c> is the check for everyone else: the ids come from the
/// caller, typically what a loader wrote, and each is looked up in Search, and only those
/// Search lacks are fetched from Storage, through the batch fetch the viewer roles admit.
/// That fetch leaves out records the caller cannot read, so those are told apart from
/// records Storage does not have.</para>
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
    internal const string NoAccess = "no access";

    /// <summary>The most ids Storage's batch fetch takes per request.</summary>
    internal const int FetchBatch = 100;

    private const string ReadRoles =
        "with --kind, service.storage.admin; with --ids-from, service.storage.viewer; and one of users.datalake.viewers, editors, admins or ops for Search";
    private const string ReindexRoles = "service.search.admin";

    private static readonly OutputSpec Columns = OutputSpec.Table("results", ("Id", "id"), ("Gap", "gap"));

    public static Command Build()
    {
        var kind = new Option<string>("--kind", "-k")
        {
            Description = "Exact kind to check, e.g. osdu:wks:master-data--Well:1.0.0. No wildcards: Storage lists records only by exact kind. Needs service.storage.admin.",
        };
        var idsFrom = new Option<string>("--ids-from")
        {
            Description = "Check only these records instead of a whole kind: a file with one record id per line, or a JSON array of ids, or - for stdin. Needs only viewer roles.",
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
            "Find the records of a kind, or in a list, that Storage holds and Search has not indexed.");
        CommandRoles.Set(command, $"{ReadRoles}; with --reindex, {ReindexRoles}", operatorRole: true);
        command.Options.Add(kind);
        command.Options.Add(idsFrom);
        command.Options.Add(reindex);
        command.Options.Add(yes);

        command.Validators.Add(result =>
        {
            var byKind = result.GetResult(kind) is not null;
            var byList = result.GetResult(idsFrom) is not null;
            if (byKind == byList)
                result.AddError("Give --kind, to check a whole kind, or --ids-from, to check a list of records, but not both.");
            if (OptionValues.Usable(result, kind) is { } value && value.Contains('*'))
                result.AddError("--kind cannot have wildcards: Storage lists records only by exact kind. Run it once per version.");
            if (result.GetResult(yes) is not null && !OptionValues.Usable(result, reindex))
                result.AddError("--yes applies only with --reindex.");
        });

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
            {
                var kindValue = parseResult.GetValue(kind);
                var progress = Console.IsErrorRedirected ? null : Console.Error;
                var report = kindValue is not null
                    ? await FindAsync(context.Client.Storage, context.Client.Search, kindValue, progress, token)
                    : await CheckAsync(context.Client.Storage, context.Client.Search,
                        ReadIds(parseResult.GetValue(idsFrom)!), progress, token);
                var what = kindValue ?? "the listed records";

                var missing = Ids(report, MissingFromSearch);
                context.Output.WriteNote(Summary(report));

                if (parseResult.GetValue(reindex) && missing.Count > 0)
                {
                    if (!Confirmation.Confirm(
                            $"Re-index {Count(missing.Count)} record(s) of {what} that Search does not have.",
                            parseResult.GetValue(yes), context))
                        return 1;
                    var notFound = await ReindexAsync(context.Client.Indexer, missing, token);
                    context.Output.WriteNote(
                        $"Asked the Indexer to re-index {Count(missing.Count - notFound.Count)} record(s). Run the command again in a few minutes to check.");
                    if (notFound.Count > 0)
                        context.Output.WriteNote(
                            $"The Indexer did not find {Count(notFound.Count)} of them in Storage: {string.Join(", ", notFound.Take(10))}{(notFound.Count > 10 ? ", ..." : "")}");
                }

                // JSON keeps the counts beside the gaps; only the table shows the gaps alone.
                return context.Output.Write(report.ToJsonString(), SpecFor(context.Output.Format));
            }, cancellationToken, $"{ReadRoles}; with --reindex, {ReindexRoles}",
                "Storage lists a kind's records only for service.storage.admin, which most users lack. Without it, " +
                "--ids-from checks a list of record ids you have, such as what a loader wrote."));

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

        // What Storage still has and lets the caller read, among the records Search lacks.
        var candidates = notIndexed.Where(id => !nowIndexed.Contains(id)).ToList();
        var readable = new Dictionary<string, string?>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count > 0)
        {
            progress?.WriteLine($"Fetching the {Count(candidates.Count)} Search lacks from Storage");
            await FetchAsync(storage, candidates, readable, deleted, cancellationToken);
        }

        var gaps = new JsonArray();
        foreach (var id in candidates.Order(StringComparer.Ordinal))
        {
            // Deleted since the list, or now of another kind: not a gap of this kind.
            if (deleted.Contains(id) || (readable.TryGetValue(id, out var of) && of != kind))
                continue;
            gaps.Add(new JsonObject { ["id"] = id, ["gap"] = readable.ContainsKey(id) ? MissingFromSearch : NoAccess });
        }
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

    /// <summary>
    /// Checks <paramref name="ids"/>: which Search has, and of the rest, which Storage has, does
    /// not have, or does not let the caller read. Those Storage has are looked up in Search once
    /// more before they are reported, as in <see cref="FindAsync"/>.
    /// </summary>
    internal static async Task<JsonObject> CheckAsync(
        StorageClient storage, SearchClient search, IReadOnlyList<string> ids, TextWriter? progress, CancellationToken cancellationToken)
    {
        progress?.WriteLine($"Looking up {Count(ids.Count)} record(s) in Search");
        var indexed = await IndexedByTypeAsync(search, ids, cancellationToken);
        var notIndexed = ids.Where(id => !indexed.Contains(id)).ToList();

        var stored = new Dictionary<string, string?>(StringComparer.Ordinal);
        var notStored = new HashSet<string>(StringComparer.Ordinal);
        if (notIndexed.Count > 0)
        {
            progress?.WriteLine($"Looking up the {Count(notIndexed.Count)} Search does not have in Storage");
            await FetchAsync(storage, notIndexed, stored, notStored, cancellationToken);
        }

        var nowIndexed = await IndexedByTypeAsync(search, [.. stored.Keys], cancellationToken);

        var gaps = new JsonArray();
        foreach (var id in notIndexed.Order(StringComparer.Ordinal))
        {
            var gap = stored.ContainsKey(id) ? (nowIndexed.Contains(id) ? null : MissingFromSearch)
                : notStored.Contains(id) ? MissingFromStorage
                : NoAccess;
            if (gap is not null)
                gaps.Add(new JsonObject { ["id"] = id, ["gap"] = gap });
        }

        return new JsonObject
        {
            ["listedCount"] = ids.Count,
            ["searchCount"] = indexed.Count + nowIndexed.Count,
            ["results"] = gaps,
        };
    }

    /// <summary>
    /// Which of <paramref name="ids"/> Search has, asked per entity type. A listed record may be
    /// of any version, so the kind is a wildcard, but not <c>*:*:*:*</c>: ADME answers that
    /// with 429, as too costly, and it searches every index in the partition.
    /// </summary>
    private static async Task<HashSet<string>> IndexedByTypeAsync(
        SearchClient search, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in ids.GroupBy(KindOf))
            found.UnionWith(await IndexedNowAsync(search, group.Key, [.. group], cancellationToken));
        return found;
    }

    /// <summary>
    /// The kinds a record id can belong to: <c>dev:work-product-component--WellLog:x</c> is of
    /// <c>*:*:work-product-component--WellLog:*</c>, the entity type being an id's second part.
    /// </summary>
    internal static string KindOf(string id) =>
        id.Split(':') is { Length: >= 3 } parts && parts[1].Length > 0 ? $"*:*:{parts[1]}:*" : "*:*:*:*";

    /// <summary>
    /// Fetches <paramref name="ids"/> from Storage in batches and sorts them into those it
    /// returned and those it reported missing. Ids it returned neither way are the ones the
    /// caller cannot read: Storage drops them after its ACL check without saying so.
    /// </summary>
    private static async Task FetchAsync(
        StorageClient storage, IReadOnlyList<string> ids, Dictionary<string, string?> stored, HashSet<string> notStored,
        CancellationToken cancellationToken)
    {
        foreach (var batch in ids.Chunk(FetchBatch))
        {
            var pending = batch.ToList();
            // Ids Storage asks to be retried are asked for once more, then left as unreadable.
            for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
            {
                var result = await storage.Query.Records.PostAsync(
                    new StorageModels.MultiRecordIds { Records = pending }, cancellationToken: cancellationToken);
                foreach (var record in result?.Records ?? [])
                    if (record.Id is { } id)
                        stored[id] = record.Kind;
                notStored.UnionWith(result?.InvalidRecords ?? []);
                pending = result?.RetryRecords ?? [];
            }
        }
    }

    /// <summary>
    /// The record ids in <paramref name="path"/>, or stdin for <c>-</c>: a JSON array of ids,
    /// or one id per line, with blank lines and lines starting <c>#</c> left out.
    /// </summary>
    internal static List<string> ReadIds(string path)
    {
        string text;
        try
        {
            text = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
        }
        catch (IOException exception)
        {
            throw new OsduException($"Cannot read {path}: {exception.Message}");
        }
        return ParseIds(text);
    }

    internal static List<string> ParseIds(string text)
    {
        IEnumerable<string> ids;
        if (text.TrimStart().StartsWith('['))
        {
            ids = (JsonNode.Parse(text) as JsonArray ?? [])
                .Select(item => item is JsonValue value && value.TryGetValue<string>(out var id) ? id
                    : item?["id"] is JsonValue inner && inner.TryGetValue<string>(out var nested) ? nested
                    : throw new OsduException("A JSON list of ids must hold strings, or objects with an \"id\"."));
        }
        else
        {
            ids = text.Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'));
        }
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            throw new OsduException("The list has no record ids.");
        return distinct;
    }

    /// <summary>
    /// Re-indexes <paramref name="ids"/> in batches, and returns those the Indexer answered it
    /// could not find in Storage.
    /// </summary>
    internal static async Task<List<string>> ReindexAsync(IndexerClient indexer, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var notFound = new List<string>();
        foreach (var batch in ids.Chunk(ReindexBatch))
        {
            var body = new IndexerModels.ReindexRecordsRequest { RecordIds = [.. batch] };
            // Read rather than discarded, so a page answered with 200 fails instead of passing.
            var json = await OsduJson.ToJsonAsync(
                await indexer.Reindex.Records.PostAsync(body, cancellationToken: cancellationToken), cancellationToken);
            if (JsonNode.Parse(json ?? "null")?["notFoundRecordIds"] is JsonArray missing)
                notFound.AddRange(missing.Select(id => id?.GetValue<string>()).OfType<string>());
        }
        return notFound;
    }

    /// <summary>The gaps alone for a table; for JSON, the whole report, counts included.</summary>
    internal static OutputSpec SpecFor(OutputFormat format) => format == OutputFormat.Json ? OutputSpec.Raw : Columns;

    /// <summary>The one-line account of a comparison, for stderr.</summary>
    internal static string Summary(JsonObject report)
    {
        var missing = Ids(report, MissingFromSearch).Count;
        var orphaned = Ids(report, MissingFromStorage).Count;
        var unreadable = Ids(report, NoAccess).Count;
        var listed = report["listedCount"] is not null;
        var line = new StringBuilder().Append(listed
            ? $"Checked {Count(report["listedCount"]!.GetValue<int>())} record(s): Search has {Count(report["searchCount"]!.GetValue<int>())}. "
            : $"Storage has {Count(report["storageCount"]!.GetValue<int>())}, Search has {Count(report["searchCount"]!.GetValue<int>())}. ");
        if (missing == 0 && orphaned == 0 && unreadable == 0)
            return line.Append("No gaps.").ToString();
        line.Append(CultureInfo.InvariantCulture, $"{Count(missing)} not in Search, {Count(orphaned)} not in Storage");
        line.Append(listed || unreadable > 0 ? $", {Count(unreadable)} you have no access to." : ".");
        if (missing > 0)
            line.Append(" Records written in the last few minutes may still be indexing; --reindex re-indexes the missing ones.");
        if (orphaned > 0)
            line.Append(listed
                ? " Records not in Storage were never written, or are deleted."
                : " Records not in Storage are deleted or inactive there but still in the index; re-indexing does not remove them.");
        if (unreadable > 0)
            line.Append(" Records you have no access to are in Storage, but their ACL does not include your groups, so neither Search nor Storage shows them to you; they are not re-indexed.");
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
            async (page, pageCancellation) =>
            {
                try
                {
                    return await OsduJson.ToJsonAsync(await storage.Query.Records.GetAsync(configuration =>
                    {
                        configuration.QueryParameters.Kind = kind;
                        configuration.QueryParameters.Limit = page["limit"]!.GetValue<int>();
                        configuration.QueryParameters.Cursor = page["cursor"]?.GetValue<string>();
                    }, pageCancellation), pageCancellation);
                }
                // Storage answers a kind it has no records of with 404, "Kind or cursor not found".
                // On the first page there is no cursor, so it is the kind: Storage has none, and
                // whatever Search has of it is not in Storage. Later, it is the cursor, an error.
                catch (ApiException exception) when (exception.ResponseStatusCode == 404 && page["cursor"] is null)
                {
                    return """{"results":[]}""";
                }
            },
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
