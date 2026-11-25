using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Indexer;
using Equinor.OsduCsharpClient.Search;
using Equinor.OsduCsharpClient.Storage;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>index gaps</c>: the records of a kind in Storage that Search has not indexed, and the
/// other way round, checked again in Search before they are reported.
/// </summary>
public class IndexGapsTests
{
    private const string Kind = "osdu:wks:master-data--Well:1.0.0";

    private static RootCommand Root()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    [Theory]
    [InlineData("index gaps --kind osdu:wks:master-data--Well:1.0.0")]
    [InlineData("index gaps -k osdu:wks:master-data--Well:1.0.0 --reindex")]
    [InlineData("index gaps -k osdu:wks:master-data--Well:1.0.0 --reindex --yes")]
    [InlineData("index gaps --ids-from loaded.txt")]
    [InlineData("index gaps --ids-from - --reindex")]
    public void WhatCanBeCombinedParses(string line) =>
        Assert.Empty(Root().Parse(line.Split(' ')).Errors);

    [Theory]
    [InlineData("index gaps --kind osdu:wks:master-data--Well:*", "--kind cannot have wildcards")]
    [InlineData("index gaps --kind osdu:wks:master-data--Well:1.0.0 --yes", "--yes applies only with --reindex.")]
    [InlineData("index gaps", "Give --kind, to check a whole kind, or --ids-from")]
    [InlineData("index gaps --kind osdu:wks:master-data--Well:1.0.0 --ids-from loaded.txt", "Give --kind, to check a whole kind, or --ids-from")]
    public void WhatCannotIsRefusedWhileParsing(string line, string expected) =>
        Assert.Contains(Root().Parse(line.Split(' ')).Errors,
            error => error.Message.StartsWith(expected, StringComparison.Ordinal));

    /// <summary>
    /// Storage and Search for one kind, paging as each does. <see cref="IndexedLater"/> are
    /// absent from Search's scan and present by the second look, as a record indexed while
    /// the command runs is.
    /// </summary>
    private sealed class Platform(IReadOnlyList<string> stored, IReadOnlyList<string> indexed) : HttpMessageHandler
    {
        public HashSet<string> IndexedLater { get; init; } = [];
        /// <summary>In Storage, but not readable by the caller: its batch fetch leaves them out.</summary>
        public HashSet<string> Unreadable { get; init; } = [];
        /// <summary>Answered as retryRecords the first time they are fetched.</summary>
        public HashSet<string> RetryOnce { get; init; } = [];
        /// <summary>Answered as retryRecords every time.</summary>
        public HashSet<string> RetryAlways { get; init; } = [];
        /// <summary>Indexed only once Storage's batch fetch has been asked for them.</summary>
        public HashSet<string> IndexedAfterFetch { get; init; } = [];
        /// <summary>The most a Storage list page holds, whatever was asked: its limit is a most.</summary>
        public int StoragePageCap { get; init; } = int.MaxValue;
        /// <summary>In Storage under another kind than the one listed: moved to a newer version.</summary>
        public Dictionary<string, string> KindNow { get; init; } = [];
        /// <summary>Gone from Search once Storage's batch fetch has been asked for them.</summary>
        public HashSet<string> UnindexedAfterFetch { get; init; } = [];
        public List<string?> FetchAttributes { get; } = [];
        private bool _storageAsked;
        private bool _fetched;
        public List<string> SearchKinds { get; } = [];
        /// <summary>Listed by Storage, then deleted before the batch fetch.</summary>
        public HashSet<string> DeletedAfterList { get; init; } = [];
        /// <summary>Storage has no record of the kind, and answers its list with 404.</summary>
        public bool KindMissing { get; init; }
        public List<string> Requests { get; } = [];
        public List<string[]> Reindexed { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add($"{request.Method} {path}");
            if (path.Contains("/api/storage/", StringComparison.Ordinal))
                _storageAsked = true;

            JsonObject response;
            if (path.EndsWith("/query/records", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var asked = body!["records"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
                Assert.True(asked.Count <= IndexGapsCommand.FetchBatch);
                _fetched = true;
                FetchAttributes.AddRange(body["attributes"]?.AsArray().Select(a => a?.GetValue<string>()) ?? [null]);
                var retry = asked.Where(id => RetryOnce.Remove(id) || RetryAlways.Contains(id)).ToList();
                var rest = asked.Except(retry).ToList();
                bool Exists(string id) => (stored.Contains(id) || KindNow.ContainsKey(id)) && !DeletedAfterList.Contains(id);
                response = new JsonObject
                {
                    ["records"] = new JsonArray([.. rest.Where(id => Exists(id) && !Unreadable.Contains(id))
                        .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["kind"] = KindNow.GetValueOrDefault(id, Kind) })]),
                    ["invalidRecords"] = new JsonArray([.. rest.Where(id => !Exists(id)).Select(id => (JsonNode)JsonValue.Create(id)!)]),
                    ["retryRecords"] = new JsonArray([.. retry.Select(id => (JsonNode)JsonValue.Create(id)!)]),
                };
            }
            else if (path.EndsWith("/query/records", StringComparison.Ordinal) && KindMissing)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"code":404,"reason":"Kind or cursor not found"}""", Encoding.UTF8, "application/json"),
                };
            }
            else if (path.EndsWith("/query/records", StringComparison.Ordinal))
            {
                var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                Assert.Equal(Kind, query["kind"]);
                response = Page(stored, query["cursor"], Math.Min(StoragePageCap, int.Parse(query["limit"]!)), id => JsonValue.Create(id)!);
            }
            else if (path.EndsWith("/query_with_cursor", StringComparison.Ordinal))
            {
                Assert.Equal(Kind, body!["kind"]!.GetValue<string>());
                response = Page(indexed, body["cursor"]?.GetValue<string>(), body["limit"]!.GetValue<int>(),
                    id => new JsonObject { ["id"] = id });
            }
            else if (path.EndsWith("/query", StringComparison.Ordinal))
            {
                SearchKinds.Add(body!["kind"]!.GetValue<string>());
                var asked = Regex.Matches(body["query"]!.GetValue<string>(), "\"([^\"]+)\"").Select(m => m.Groups[1].Value);
                // Late records turn up only after Storage was read, as they would during the run.
                var now = indexed.Concat(_storageAsked ? IndexedLater : []).Concat(_fetched ? IndexedAfterFetch : []).ToHashSet();
                if (_fetched)
                    now.ExceptWith(UnindexedAfterFetch);
                response = new JsonObject
                {
                    ["results"] = new JsonArray([.. asked.Where(now.Contains).Select(id => (JsonNode)new JsonObject { ["id"] = id })]),
                };
            }
            else if (path.EndsWith("/reindex/records", StringComparison.Ordinal))
            {
                string[] asked = [.. body!["recordIds"]!.AsArray().Select(id => id!.GetValue<string>())];
                Reindexed.Add(asked);
                response = new JsonObject
                {
                    ["reindexRecordIds"] = new JsonArray([.. asked.Where(stored.Contains).Select(id => (JsonNode)JsonValue.Create(id)!)]),
                    ["notFoundRecordIds"] = new JsonArray([.. asked.Where(id => !stored.Contains(id)).Select(id => (JsonNode)JsonValue.Create(id)!)]),
                };
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>One page of <paramref name="ids"/>, the cursor an offset, none after the last.</summary>
        private static JsonObject Page(IReadOnlyList<string> ids, string? cursor, int limit, Func<string, JsonNode> item)
        {
            var offset = int.Parse(cursor ?? "0");
            var page = ids.Skip(offset).Take(limit).Select(item).ToArray();
            var result = new JsonObject { ["results"] = new JsonArray(page), ["totalCount"] = ids.Count };
            if (offset + limit < ids.Count)
                result["cursor"] = (offset + limit).ToString();
            return result;
        }
    }

    private static HttpClientRequestAdapter Adapter(Platform platform, string service) =>
        new(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(platform))
        {
            BaseUrl = $"https://osdu.example.com/api/{service}/v2",
        };

    private static Task<JsonObject> Find(Platform platform) =>
        IndexGapsCommand.FindAsync(
            new StorageClient(Adapter(platform, "storage")), new SearchClient(Adapter(platform, "search")),
            Kind, null, TestContext.Current.CancellationToken);

    private static List<string> Wells(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => $"dev:master-data--Well:{i}")];

    [Fact]
    public async Task EveryRecordIndexedIsNoGap()
    {
        var wells = Wells(0, 2500);
        var report = await Find(new Platform(wells, wells));

        Assert.Empty(report["results"]!.AsArray());
        Assert.Equal(2500, report["storageCount"]!.GetValue<int>());
        Assert.Equal(2500, report["searchCount"]!.GetValue<int>());
        Assert.EndsWith("No gaps.", IndexGapsCommand.Summary(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothSidesAreReadPageByPage()
    {
        var wells = Wells(0, 2500);
        var platform = new Platform(wells, wells);
        await Find(platform);

        Assert.Equal(3, platform.Requests.Count(r => r.EndsWith("/query/records", StringComparison.Ordinal)));
        Assert.Equal(3, platform.Requests.Count(r => r.EndsWith("/query_with_cursor", StringComparison.Ordinal)));
        // Nothing differed, so nothing is looked up again.
        Assert.DoesNotContain(platform.Requests, r => r.EndsWith("/query", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GapsOnEitherSideAreReported()
    {
        // 0-1199 stored, 5-1204 indexed: 0-4 never indexed, 1200-1204 deleted from Storage.
        var report = await Find(new Platform(Wells(0, 1200), Wells(5, 1200)));

        Assert.Equal(Wells(0, 5), IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
        Assert.Equal(Wells(1200, 5), IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromStorage));
        Assert.Contains("5 not in Search, 5 not in Storage.", IndexGapsCommand.Summary(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReindexAlsoSendsWhatAKindsIndexHasAndStorageDoesNot()
    {
        var report = await Find(new Platform(Wells(0, 1200), Wells(5, 1200)));

        Assert.Equal(Wells(1200, 5), IndexGapsCommand.Orphans(report));
        Assert.Contains("--reindex has the Indexer remove them.", IndexGapsCommand.Summary(report), StringComparison.Ordinal);
        Assert.Equal(
            "Re-index 5 record(s) of k that Search does not have, and remove 5 from the index that Storage no longer has.",
            IndexGapsCommand.ReindexAction(5, 5, "k"));
        Assert.Equal("Remove 2 record(s) of k from the index that Storage no longer has.", IndexGapsCommand.ReindexAction(0, 2, "k"));
        Assert.Equal("Re-index 3 record(s) of k that Search does not have.", IndexGapsCommand.ReindexAction(3, 0, "k"));
    }

    [Fact]
    public async Task AListedRecordNotInStorageIsNotSentForRemoval()
    {
        // Not in Storage and not in Search either: there is nothing in the index to remove.
        var report = await Check(new Platform(Wells(0, 2), Wells(0, 2)), Wells(0, 3));

        Assert.Equal(["dev:master-data--Well:2"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromStorage));
        Assert.Empty(IndexGapsCommand.Orphans(report));
    }

    [Fact]
    public async Task ARecordIndexedDuringTheScanIsNoGap()
    {
        var stored = Wells(0, 10);
        var platform = new Platform(stored, Wells(0, 8)) { IndexedLater = ["dev:master-data--Well:8"] };

        var report = await Find(platform);

        Assert.Equal(["dev:master-data--Well:9"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
    }

    [Fact]
    public async Task MissingRecordsAreReindexedInBatches()
    {
        var ids = Wells(0, 2001);
        var platform = new Platform(Wells(0, 2000), []);

        var notFound = await IndexGapsCommand.ReindexAsync(
            new IndexerClient(Adapter(platform, "indexer")), ids, TestContext.Current.CancellationToken);

        Assert.Equal(["dev:master-data--Well:2000"], notFound);
        Assert.Equal([1000, 1000, 1], platform.Reindexed.Select(batch => batch.Length));
        Assert.Equal(ids, platform.Reindexed.SelectMany(batch => batch));
    }

    [Fact]
    public async Task ARecordAnAdminCannotReadIsNoAccessNotAGap()
    {
        // Storage's list shows every record; Search, and the batch fetch, only readable ones.
        var platform = new Platform(Wells(0, 3), Wells(0, 1)) { Unreadable = ["dev:master-data--Well:2"] };

        var report = await Find(platform);

        Assert.Equal(["dev:master-data--Well:1"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
        Assert.Equal(["dev:master-data--Well:2"], IndexGapsCommand.Ids(report, IndexGapsCommand.NoAccess));
        Assert.Contains("1 you have no access to.", IndexGapsCommand.Summary(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordDeletedAfterStorageListedItIsNoGap()
    {
        var platform = new Platform(Wells(0, 3), Wells(0, 1)) { DeletedAfterList = ["dev:master-data--Well:2"] };

        var report = await Find(platform);

        Assert.Equal(["dev:master-data--Well:1"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
        Assert.Single(report["results"]!.AsArray());
    }

    [Fact]
    public async Task AKindStorageHasNoneOfLeavesWhatSearchHasNotInStorage()
    {
        var platform = new Platform([], Wells(0, 2)) { KindMissing = true };

        var report = await Find(platform);

        Assert.Equal(0, report["storageCount"]!.GetValue<int>());
        Assert.Equal(Wells(0, 2), IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromStorage));
    }

    [Fact]
    public void JsonOutputKeepsTheCountsAndATableShowsTheGaps()
    {
        Assert.Same(OutputSpec.Raw, IndexGapsCommand.SpecFor(OutputFormat.Json));
        Assert.Equal("results", IndexGapsCommand.SpecFor(OutputFormat.Table).Root);
    }

    private static Task<JsonObject> Check(Platform platform, IReadOnlyList<string> ids) =>
        IndexGapsCommand.CheckAsync(
            new StorageClient(Adapter(platform, "storage")), new SearchClient(Adapter(platform, "search")),
            ids, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AListedRecordIsSortedByWhereItIs()
    {
        // 0-249 indexed; 250-259 stored, not indexed; 260-264 not stored; 265-269 unreadable.
        var stored = Wells(0, 260).Concat(Wells(265, 5)).ToList();
        var platform = new Platform(stored, Wells(0, 250)) { Unreadable = [.. Wells(265, 5)] };

        var report = await Check(platform, Wells(0, 270));

        Assert.Equal(Wells(250, 10), IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
        Assert.Equal(Wells(260, 5), IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromStorage));
        Assert.Equal(Wells(265, 5), IndexGapsCommand.Ids(report, IndexGapsCommand.NoAccess));
        Assert.Equal(270, report["listedCount"]!.GetValue<int>());
        Assert.Equal(250, report["searchCount"]!.GetValue<int>());
        Assert.Contains("10 not in Search, 5 not in Storage, 5 you have no access to.",
            IndexGapsCommand.Summary(report), StringComparison.Ordinal);
        // Only what Search lacks is fetched from Storage, and nothing is listed.
        Assert.DoesNotContain(platform.Requests, r => r == "GET /api/storage/v2/query/records");
        Assert.Single(platform.Requests, r => r == "POST /api/storage/v2/query/records");
    }

    [Fact]
    public async Task AListedRecordIndexedDuringTheCheckIsNoGap()
    {
        var platform = new Platform(Wells(0, 3), Wells(0, 1)) { IndexedLater = ["dev:master-data--Well:1"] };

        var report = await Check(platform, Wells(0, 3));

        Assert.Equal(["dev:master-data--Well:2"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
        Assert.Equal(2, report["searchCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ARecordStorageAsksToRetryIsAskedForAgain()
    {
        var platform = new Platform(Wells(0, 2), []) { RetryOnce = ["dev:master-data--Well:1"] };

        var report = await Check(platform, Wells(0, 2));

        Assert.Equal(Wells(0, 2), IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
        Assert.Equal(2, platform.Requests.Count(r => r == "POST /api/storage/v2/query/records"));
    }

    [Fact]
    public async Task SearchIsAskedPerEntityTypeNotForEveryKind()
    {
        var ids = new[] { "dev:work-product-component--WellLog:a", "dev:master-data--Wellbore:b", "dev:work-product-component--WellLog:c" };
        var platform = new Platform(ids, ids);

        await Check(platform, ids);

        Assert.Equal(["*:*:work-product-component--WellLog:*", "*:*:master-data--Wellbore:*"], platform.SearchKinds);
    }

    [Theory]
    [InlineData("dev:work-product-component--WellLog:OmegaSor-CompLog-34-4-19S:1", "*:*:work-product-component--WellLog:*")]
    public void AnIdsKindIsItsEntityType(string id, string kind) =>
        Assert.Equal(kind, IndexGapsCommand.KindOf(id));

    [Fact]
    public void AListIsOneIdPerLineWithoutBlanksCommentsOrRepeats() =>
        Assert.Equal(["dev:a:1", "dev:a:2"],
            IndexGapsCommand.ParseIds("# loaded 2026-11-25\r\ndev:a:1\r\n\r\n  dev:a:2  \ndev:a:1\n"));

    [Fact]
    public void AListCanBeAJsonArrayOfIdsOrRecords()
    {
        Assert.Equal(["dev:a:1", "dev:a:2"], IndexGapsCommand.ParseIds("[\"dev:a:1\", \"dev:a:2\"]"));
        Assert.Equal(["dev:a:1"], IndexGapsCommand.ParseIds("[{\"id\": \"dev:a:1\", \"kind\": \"x\"}]"));
    }

    [Theory]
    [InlineData("[123]", "A JSON list of ids must hold strings")]
    [InlineData("[[]]", "A JSON list of ids must hold strings")]
    [InlineData("[\"dev:a:1\"", "The list starts with [ but is not JSON")]
    [InlineData("dev:a:1\nnot-an-id\n", "1 entr(ies) are not record ids")]
    [InlineData("dev:*:1", "1 entr(ies) are not record ids")]
    public void AMalformedListIsRefusedNamingWhy(string text, string expected) =>
        Assert.StartsWith(expected,
            Assert.Throws<Equinor.OsduCsharpClient.Facade.OsduException>(() => IndexGapsCommand.ParseIds(text)).Message,
            StringComparison.Ordinal);

    [Fact]
    public async Task AShortStoragePageWithACursorIsNotTheEnd()
    {
        var wells = Wells(0, 2500);
        var platform = new Platform(wells, wells) { StoragePageCap = 400 };

        var report = await Find(platform);

        Assert.Equal(2500, report["storageCount"]!.GetValue<int>());
        Assert.Empty(report["results"]!.AsArray());
        Assert.Equal(7, platform.Requests.Count(r => r == "GET /api/storage/v2/query/records"));
    }

    [Fact]
    public async Task ARecordIndexedWhileStorageIsFetchedIsNoGap()
    {
        var platform = new Platform(Wells(0, 3), Wells(0, 1)) { IndexedAfterFetch = ["dev:master-data--Well:1"] };

        var report = await Find(platform);

        Assert.Equal(["dev:master-data--Well:2"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromSearch));
    }

    [Fact]
    public async Task ARecordStorageKeepsAskingToRetryStopsTheCheck()
    {
        var platform = new Platform(Wells(0, 2), []) { RetryAlways = ["dev:master-data--Well:1"] };

        var error = await Assert.ThrowsAsync<Equinor.OsduCsharpClient.Facade.OsduException>(() => Check(platform, Wells(0, 2)));

        Assert.Contains("the check is incomplete", error.Message, StringComparison.Ordinal);
        Assert.Equal(IndexGapsCommand.FetchAttempts, platform.Requests.Count(r => r == "POST /api/storage/v2/query/records"));
    }

    [Fact]
    public async Task ARecordMovedToAnotherKindIsNotCalledMissingFromStorage()
    {
        // 2 is in this kind's index, but Storage lists it under 1.1.0 now.
        var platform = new Platform(Wells(0, 2), Wells(0, 3))
        {
            KindNow = new() { ["dev:master-data--Well:2"] = "osdu:wks:master-data--Well:1.1.0" },
        };

        var report = await Find(platform);

        Assert.Equal(["dev:master-data--Well:2"], IndexGapsCommand.Ids(report, IndexGapsCommand.OtherKind));
        Assert.Empty(IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromStorage));
        Assert.Contains("1 now of another kind.", IndexGapsCommand.Summary(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIndexEntryRemovedDuringTheFetchIsNoGap()
    {
        var platform = new Platform(Wells(0, 1), Wells(0, 3)) { UnindexedAfterFetch = ["dev:master-data--Well:2"] };

        var report = await Find(platform);

        Assert.Equal(["dev:master-data--Well:1"], IndexGapsCommand.Ids(report, IndexGapsCommand.MissingFromStorage));
    }

    [Fact]
    public async Task StorageIsFetchedWithoutTheRecordsData()
    {
        var platform = new Platform(Wells(0, 2), []);

        await Check(platform, Wells(0, 2));

        Assert.Equal([IndexGapsCommand.NoDataAttribute], platform.FetchAttributes);
    }

    [Fact]
    public void AnEmptyListIsRefused() =>
        Assert.Throws<Equinor.OsduCsharpClient.Facade.OsduException>(() => IndexGapsCommand.ParseIds("# nothing\n\n"));

    [Fact]
    public void AnIdQueryQuotesEachId() =>
        Assert.Equal(
            "id:(\"dev:master-data--Well:1\" OR \"dev:a\\\"b\")",
            IndexGapsCommand.IdQuery(["dev:master-data--Well:1", "dev:a\"b"]));
}
