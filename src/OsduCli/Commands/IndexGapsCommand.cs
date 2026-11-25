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
    internal const string OtherKind = "other kind in storage";

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
            Description = "Re-index the records Search does not have and, with --kind, have the Indexer remove those Storage no longer has, " +
                ReindexBatch + " per request. Needs " + ReindexRoles + ".",
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
                var orphaned = Orphans(report);
                context.Output.WriteNote(Summary(report));
                // Written before anything is asked or re-indexed, so declining, or a failed
                // re-index, still leaves the result of the whole scan.
                context.Output.Write(report.ToJsonString(), SpecFor(context.Output.Format));

                if (parseResult.GetValue(reindex) && missing.Count + orphaned.Count > 0)
                {
                    if (!Confirmation.Confirm(ReindexAction(missing.Count, orphaned.Count, what),
                            parseResult.GetValue(yes), context))
                        return 1;
                    // The Indexer re-indexes what Storage has, and removes from the index what
                    // Storage does not, answering those as not found.
                    var notFound = await ReindexAsync(context.Client.Indexer, [.. missing, .. orphaned], token);
                    var expected = new HashSet<string>(orphaned, StringComparer.Ordinal);
                    var gone = notFound.Where(id => !expected.Contains(id)).ToList();
                    context.Output.WriteNote(
                        $"Asked the Indexer to re-index {Count(missing.Count - gone.Count)} record(s)" +
                        (orphaned.Count > 0 ? $" and to remove {Count(orphaned.Count)} from the index" : "") +
                        ". Run the command again in a few minutes to check.");
                    if (gone.Count > 0)
                        context.Output.WriteNote(
                            $"The Indexer did not find {Count(gone.Count)} of the records to re-index in Storage: {string.Join(", ", gone.Take(10))}{(gone.Count > 10 ? ", ..." : "")}");
                }
                return 0;
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

        // Both sides are fetched from Storage, which applies the ACL and says what each record
        // is now: Search lacking one may be its ACL, Storage's list lacking one may be that it
        // moved to another kind.
        var candidates = notIndexed.Where(id => !nowIndexed.Contains(id)).ToList();
        var orphans = notStored.Where(nowIndexed.Contains).ToList();
        var readable = new Dictionary<string, string?>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var toFetch = candidates.Concat(orphans).ToList();
        if (toFetch.Count > 0)
        {
            progress?.WriteLine($"Fetching {Count(toFetch.Count)} difference(s) from Storage");
            await FetchAsync(storage, toFetch, readable, deleted, cancellationToken);
        }

        // The last look, after the fetch, at both sides.
        var stillIndexed = await IndexedNowAsync(search, kind,
            [.. candidates.Where(readable.ContainsKey), .. orphans], cancellationToken);

        var gaps = new JsonArray();
        foreach (var id in candidates.Order(StringComparer.Ordinal))
        {
            // Deleted since the list, now of another kind, or indexed since: not a gap of this kind.
            if (deleted.Contains(id) || stillIndexed.Contains(id) || (readable.TryGetValue(id, out var of) && of != kind))
                continue;
            gaps.Add(new JsonObject { ["id"] = id, ["gap"] = readable.ContainsKey(id) ? MissingFromSearch : NoAccess });
        }
        foreach (var id in orphans.Where(stillIndexed.Contains).Order(StringComparer.Ordinal))
        {
            string? gap = deleted.Contains(id) ? MissingFromStorage
                : !readable.TryGetValue(id, out var now) ? NoAccess
                // In Storage, but no longer of this kind: its old index entry is left over.
                : now != kind ? OtherKind
                // Of this kind after all: written after Storage was listed.
                : null;
            if (gap is not null)
                gaps.Add(new JsonObject { ["id"] = id, ["gap"] = gap });
        }

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
        var indexed = await IndexedByTypeAsync(search, ids, KindOf, cancellationToken);
        var notIndexed = ids.Where(id => !indexed.Contains(id)).ToList();

        var stored = new Dictionary<string, string?>(StringComparer.Ordinal);
        var notStored = new HashSet<string>(StringComparer.Ordinal);
        if (notIndexed.Count > 0)
        {
            progress?.WriteLine($"Looking up the {Count(notIndexed.Count)} Search does not have in Storage");
            await FetchAsync(storage, notIndexed, stored, notStored, cancellationToken);
        }

        // The second look, at every id Search lacked, under the kind Storage gave where it gave one.
        var nowIndexed = await IndexedByTypeAsync(search, notIndexed,
            id => stored.TryGetValue(id, out var of) && of is not null ? of : KindOf(id), cancellationToken);

        var gaps = new JsonArray();
        foreach (var id in notIndexed.Where(id => !nowIndexed.Contains(id)).Order(StringComparer.Ordinal))
        {
            var gap = stored.ContainsKey(id) ? MissingFromSearch
                : notStored.Contains(id) ? MissingFromStorage
                : NoAccess;
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
        SearchClient search, IReadOnlyList<string> ids, Func<string, string> kindOf, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in ids.GroupBy(kindOf))
            found.UnionWith(await IndexedNowAsync(search, group.Key, [.. group], cancellationToken));
        return found;
    }

    /// <summary>
    /// The kinds a record id can belong to: <c>dev:work-product-component--WellLog:x</c> is of
    /// <c>*:*:work-product-component--WellLog:*</c>, the entity type being an id's second part.
    /// </summary>
    internal static string KindOf(string id) => $"*:*:{EntityType(id)}:*";

    /// <summary>An id's entity type, its second part, or null when it has none.</summary>
    private static string? EntityType(string id) =>
        id.Split(':') is { Length: >= 3 } parts && parts[0].Length > 0 && parts[1].Length > 0 && parts[2].Length > 0
            && !parts[1].Contains('*')
            ? parts[1] : null;

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
            // Ids Storage asks to be retried are asked for again. Still pending after that, they
            // are neither readable nor denied, so the check cannot finish rather than guess.
            for (var attempt = 0; attempt < FetchAttempts && pending.Count > 0; attempt++)
            {
                var result = await storage.Query.Records.PostAsync(
                    new StorageModels.MultiRecordIds { Records = pending, Attributes = [NoDataAttribute] }, cancellationToken: cancellationToken);
                foreach (var record in result?.Records ?? [])
                    if (record.Id is { } id)
                        stored[id] = record.Kind;
                notStored.UnionWith(result?.InvalidRecords ?? []);
                pending = result?.RetryRecords ?? [];
            }
            if (pending.Count > 0)
                throw new OsduException(
                    $"Storage asked for {Count(pending.Count)} record(s) to be fetched again {FetchAttempts} times, " +
                    $"so the check is incomplete. Run it again. The first: {pending[0]}");
        }
    }

    /// <summary>
    /// A data attribute no record has. Storage keeps only the named <c>data.*</c> fields and
    /// always returns the record's metadata, so this fetches id, kind and ACL without the data:
    /// smaller, and nothing from it, such as a signed file URL, can reach a <c>--debug</c> log.
    /// </summary>
    internal const string NoDataAttribute = "data.osducs-index-gaps-no-data";

    /// <summary>How often a batch is fetched while Storage answers some of it with retryRecords.</summary>
    internal const int FetchAttempts = 3;

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
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new OsduException($"The list starts with [ but is not JSON: {exception.Message}");
            }
            ids = (parsed as JsonArray ?? [])
                .Select(item => item is JsonValue value && value.TryGetValue<string>(out var id) ? id
                    : item is JsonObject record && record["id"] is JsonValue inner && inner.TryGetValue<string>(out var nested) ? nested
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
        // Each is searched for under its entity type; without one, only every kind would do,
        // which ADME refuses.
        var malformed = distinct.Where(id => EntityType(id) is null).ToList();
        if (malformed.Count > 0)
            throw new OsduException(
                $"{Count(malformed.Count)} entr(ies) are not record ids of the form <partition>:<entity type>:<id>: " +
                string.Join(", ", malformed.Take(5).Select(id => $"\"{id}\"")) + (malformed.Count > 5 ? ", ..." : ""));
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

    /// <summary>
    /// The records <c>--reindex</c> sends for removal: those a kind's index has and Storage no
    /// longer does. A listed record not in Storage is not in Search either, so nothing to remove.
    /// </summary>
    internal static List<string> Orphans(JsonObject report) =>
        report["listedCount"] is null ? Ids(report, MissingFromStorage) : [];

    internal static string ReindexAction(int missing, int orphaned, string what) => (missing, orphaned) switch
    {
        (_, 0) => $"Re-index {Count(missing)} record(s) of {what} that Search does not have.",
        (0, _) => $"Remove {Count(orphaned)} record(s) of {what} from the index that Storage no longer has.",
        _ => $"Re-index {Count(missing)} record(s) of {what} that Search does not have, and remove {Count(orphaned)} from the index that Storage no longer has.",
    };

    /// <summary>The gaps alone for a table; for JSON, the whole report, counts included.</summary>
    internal static OutputSpec SpecFor(OutputFormat format) => format == OutputFormat.Json ? OutputSpec.Raw : Columns;

    /// <summary>The one-line account of a comparison, for stderr.</summary>
    internal static string Summary(JsonObject report)
    {
        var missing = Ids(report, MissingFromSearch).Count;
        var orphaned = Ids(report, MissingFromStorage).Count;
        var unreadable = Ids(report, NoAccess).Count;
        var moved = Ids(report, OtherKind).Count;
        var listed = report["listedCount"] is not null;
        var line = new StringBuilder().Append(listed
            ? $"Checked {Count(report["listedCount"]!.GetValue<int>())} record(s): Search has {Count(report["searchCount"]!.GetValue<int>())}. "
            : $"Storage has {Count(report["storageCount"]!.GetValue<int>())}, Search has {Count(report["searchCount"]!.GetValue<int>())}. ");
        if (missing == 0 && orphaned == 0 && unreadable == 0 && moved == 0)
            return line.Append("No gaps.").ToString();
        line.Append(CultureInfo.InvariantCulture, $"{Count(missing)} not in Search, {Count(orphaned)} not in Storage");
        line.Append(listed || unreadable > 0 ? $", {Count(unreadable)} you have no access to" : "");
        line.Append(moved > 0 ? $", {Count(moved)} now of another kind." : ".");
        if (missing > 0)
            line.Append(" Records written in the last few minutes may still be indexing; --reindex re-indexes the missing ones.");
        if (orphaned > 0)
            line.Append(listed
                ? " Records not in Storage were never written, or are deleted."
                : " Records not in Storage are deleted there but still in the index; --reindex has the Indexer remove them.");
        if (moved > 0)
            line.Append(" Records now of another kind were written again under a newer kind, and this kind's index still has their old entry.");
        if (unreadable > 0)
            line.Append(" Records you have no access to are in Storage, but their ACL does not include your groups, so neither Search nor Storage shows them to you; they are not re-indexed.");
        return line.ToString();
    }

    internal static List<string> Ids(JsonObject report, string gap) =>
        [.. report["results"]!.AsArray()
            .Where(item => item!["gap"]!.GetValue<string>() == gap)
            .Select(item => item!["id"]!.GetValue<string>())];

    /// <summary>
    /// Every id Search has of <paramref name="kind"/>, through its cursor, page by page into
    /// one set, so no page outlives its turn. A cursor left open by an error or an
    /// interruption is released.
    /// </summary>
    private static async Task<HashSet<string>> SearchIdsAsync(
        SearchClient search, string kind, TextWriter? progress, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        long? total = null;
        try
        {
            do
            {
                var request = new JsonObject { ["kind"] = kind, ["returnedFields"] = new JsonArray("id"), ["limit"] = PageSize };
                if (cursor is not null)
                    request["cursor"] = cursor;
                var page = JsonNode.Parse(await OsduJson.ToJsonAsync(
                    await search.Query_with_cursor.PostAsync(
                        (await KiotaJsonSerializer.DeserializeAsync<SearchModels.CursorQueryRequest>(
                            request.ToJsonString(), SearchModels.CursorQueryRequest.CreateFromDiscriminatorValue, cancellationToken))!,
                        cancellationToken: cancellationToken), cancellationToken) ?? "null");
                total ??= page?["totalCount"] is JsonValue count && count.TryGetValue<long>(out var matches) ? matches : null;
                var received = ids.Count;
                ids.UnionWith(IdsOf(page, item => item?["id"]));
                cursor = page?["cursor"] is JsonValue next && next.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
                progress?.Write($"\rRead {CursorPaging.Progress(ids.Count, total)}");
                // An empty page ends it whatever the cursor says, rather than following it forever.
                if (ids.Count == received)
                    break;
            }
            while (cursor is not null);
        }
        finally
        {
            progress?.WriteLine();
            if (cursor is not null)
                await ReleaseQuietlyAsync(search, cursor);
        }
        return ids;
    }

    private static async Task ReleaseQuietlyAsync(SearchClient search, string cursor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await search.Query_with_cursor[cursor].DeleteAsync(cancellationToken: timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The scan's own outcome is what matters; an unreleased cursor expires.
        }
    }

    /// <summary>
    /// Every id Storage has of <paramref name="kind"/>, page by page into one set. Storage's
    /// limit is a most, not a promise, so a short page is not the end: only a page with no
    /// cursor is.
    /// </summary>
    private static async Task<HashSet<string>> StorageIdsAsync(
        StorageClient storage, string kind, TextWriter? progress, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            do
            {
                StorageModels.DatastoreQueryResult? page;
                try
                {
                    var asked = cursor;
                    page = await storage.Query.Records.GetAsync(configuration =>
                    {
                        configuration.QueryParameters.Kind = kind;
                        configuration.QueryParameters.Limit = PageSize;
                        configuration.QueryParameters.Cursor = asked;
                    }, cancellationToken);
                }
                // Storage answers a kind it has no records of with 404, "Kind or cursor not found".
                // On the first page there is no cursor, so it is the kind: Storage has none, and
                // whatever Search has of it is not in Storage. Later, it is the cursor, an error.
                catch (ApiException exception) when (exception.ResponseStatusCode == 404 && cursor is null)
                {
                    break;
                }
                // Storage answers bare id strings.
                ids.UnionWith(page?.Results ?? []);
                cursor = string.IsNullOrEmpty(page?.Cursor) ? null : page.Cursor;
                progress?.Write($"\rRead {CursorPaging.Progress(ids.Count, null)}");
                // A cursor seen before would read the same pages again.
                if (cursor is not null && !seen.Add(cursor))
                    throw new OsduException("Storage answered a cursor it had answered before, so its list cannot be read to the end.");
            }
            while (cursor is not null);
        }
        finally
        {
            progress?.WriteLine();
        }
        return ids;
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
            found.UnionWith(IdsOf(JsonNode.Parse(await OsduJson.ToJsonAsync(result, cancellationToken) ?? "null"), item => item?["id"]));
        }
        return found;
    }

    /// <summary>A Lucene query matching any of <paramref name="ids"/> exactly.</summary>
    internal static string IdQuery(IEnumerable<string> ids) =>
        "id:(" + string.Join(" OR ", ids.Select(id => "\"" + id.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + ")";

    private static HashSet<string> IdsOf(JsonNode? page, Func<JsonNode?, JsonNode?> id)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (page?["results"] is JsonArray results)
            foreach (var item in results)
                if (id(item) is JsonValue value && value.TryGetValue<string>(out var text))
                    ids.Add(text);
        return ids;
    }

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
