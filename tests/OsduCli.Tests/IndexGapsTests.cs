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
    public void WhatCanBeCombinedParses(string line) =>
        Assert.Empty(Root().Parse(line.Split(' ')).Errors);

    [Theory]
    [InlineData("index gaps --kind osdu:wks:master-data--Well:*", "--kind cannot have wildcards")]
    [InlineData("index gaps --kind osdu:wks:master-data--Well:1.0.0 --yes", "--yes applies only with --reindex.")]
    [InlineData("index gaps", "Option '--kind' is required.")]
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
        public List<string> Requests { get; } = [];
        public List<string[]> Reindexed { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add($"{request.Method} {path}");

            JsonObject response;
            if (path.EndsWith("/query/records", StringComparison.Ordinal))
            {
                var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                Assert.Equal(Kind, query["kind"]);
                response = Page(stored, query["cursor"], int.Parse(query["limit"]!), id => JsonValue.Create(id)!);
            }
            else if (path.EndsWith("/query_with_cursor", StringComparison.Ordinal))
            {
                Assert.Equal(Kind, body!["kind"]!.GetValue<string>());
                response = Page(indexed, body["cursor"]?.GetValue<string>(), body["limit"]!.GetValue<int>(),
                    id => new JsonObject { ["id"] = id });
            }
            else if (path.EndsWith("/query", StringComparison.Ordinal))
            {
                var asked = Regex.Matches(body!["query"]!.GetValue<string>(), "\"([^\"]+)\"").Select(m => m.Groups[1].Value);
                var now = indexed.Concat(IndexedLater).ToHashSet();
                response = new JsonObject
                {
                    ["results"] = new JsonArray([.. asked.Where(now.Contains).Select(id => (JsonNode)new JsonObject { ["id"] = id })]),
                };
            }
            else if (path.EndsWith("/reindex/records", StringComparison.Ordinal))
            {
                Reindexed.Add([.. body!["recordIds"]!.AsArray().Select(id => id!.GetValue<string>())]);
                response = new JsonObject();
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
        var platform = new Platform([], []);
        var ids = Wells(0, 2001);

        var sent = await IndexGapsCommand.ReindexAsync(
            new IndexerClient(Adapter(platform, "indexer")), ids, TestContext.Current.CancellationToken);

        Assert.Equal(2001, sent);
        Assert.Equal([1000, 1000, 1], platform.Reindexed.Select(batch => batch.Length));
        Assert.Equal(ids, platform.Reindexed.SelectMany(batch => batch));
    }

    [Fact]
    public void AnIdQueryQuotesEachId() =>
        Assert.Equal(
            "id:(\"dev:master-data--Well:1\" OR \"dev:a\\\"b\")",
            IndexGapsCommand.IdQuery(["dev:master-data--Well:1", "dev:a\"b"]));
}
