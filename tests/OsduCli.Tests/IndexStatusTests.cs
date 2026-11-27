using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Search;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>index status</c>: per kind, the records indexed with a warning, without a schema, or with
/// another code, from one Search aggregation by kind per code.
/// </summary>
public class IndexStatusTests
{
    private const string Sample = "osdu:wks:master-data--Sample:2.1.0";
    private const string Prod = "eqnr:cwi-db:Prod:1.1.0";
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.2.0";

    /// <summary>Search, answering each query with the buckets given for it.</summary>
    private sealed class Search(Dictionary<string, (string? Key, long Count)[]> buckets) : HttpMessageHandler
    {
        public List<JsonNode> Bodies { get; } = [];
        /// <summary>"start" and "end" of each request, by query, in the order they happened.</summary>
        public List<string> Events { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var query = body["query"]!.GetValue<string>();
            lock (Bodies)
            {
                Bodies.Add(body);
                Events.Add("start " + query);
            }
            await Task.Delay(10, cancellationToken);
            lock (Bodies)
                Events.Add("end " + query);
            var answer = buckets.GetValueOrDefault(query) ?? [];
            var response = new JsonObject
            {
                ["results"] = new JsonArray(),
                ["aggregations"] = new JsonArray([.. answer.Select(b => (JsonNode)new JsonObject { ["key"] = b.Key, ["count"] = b.Count })]),
                ["totalCount"] = answer.Sum(b => b.Count),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static Task<JsonObject> Count(Search search, string kind = "*:*:*:*") =>
        IndexStatusCommand.CountAsync(
            new SearchClient(new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(search))
            {
                BaseUrl = "https://osdu.example.com/api/search/v2",
            }),
            kind, TestContext.Current.CancellationToken);

    private static RootCommand Root()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    [Theory]
    [InlineData("index status")]
    [InlineData("index status -k *:*:work-product-component--WellLog:*")]
    public void ItParses(string line) => Assert.Empty(Root().Parse(line.Split(' ')).Errors);

    [Fact]
    public async Task TheCodesAreMergedIntoOneRowPerKindMostFirst()
    {
        var search = new Search(new()
        {
            [IndexStatusCommand.Warning] = [(Sample, 7070), (WellLog, 15)],
            [IndexStatusCommand.NoSchema] = [(Prod, 12244), (WellLog, 2)],
            [IndexStatusCommand.Other] = [(WellLog, 3)],
        });

        var report = await Count(search);

        var rows = report["kinds"]!.AsArray();
        Assert.Equal([Prod, Sample, WellLog], rows.Select(row => row!["kind"]!.GetValue<string>()));
        Assert.Equal(15, rows[2]!["warnings"]!.GetValue<long>());
        Assert.Equal(2, rows[2]!["noSchema"]!.GetValue<long>());
        Assert.Equal(3, rows[2]!["other"]!.GetValue<long>());
        Assert.Equal(20, rows[2]!["total"]!.GetValue<long>());
        Assert.Equal(7085, report["warnings"]!.GetValue<long>());
        Assert.Equal(12246, report["noSchema"]!.GetValue<long>());
        Assert.Equal(3, report["other"]!.GetValue<long>());
        Assert.Equal(19334, report["total"]!.GetValue<long>());
        Assert.Equal(12244, rows[0]!["total"]!.GetValue<long>());
        Assert.False(report["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EachCodeIsOneAggregationByKindOverTheGivenKinds()
    {
        var search = new Search([]);

        await Count(search, "*:*:work-product-component--WellLog:*");

        Assert.Equal(
            new[] { IndexStatusCommand.NoSchema, IndexStatusCommand.Other, IndexStatusCommand.Warning }.Order(StringComparer.Ordinal),
            search.Bodies.Select(body => body["query"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(search.Bodies, body =>
        {
            Assert.Equal("kind", body["aggregateBy"]!.GetValue<string>());
            Assert.Equal("*:*:work-product-component--WellLog:*", body["kind"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task TheFirstRequestIsAloneSoOnlyOneSignInOpens()
    {
        var search = new Search([]);

        await Count(search);

        Assert.Equal("start " + IndexStatusCommand.Warning, search.Events[0]);
        Assert.Equal("end " + IndexStatusCommand.Warning, search.Events[1]);
        Assert.Equal(6, search.Events.Count);
    }

    [Fact]
    public async Task AKindMissingFromACutAnswerIsUnknownNotZero()
    {
        // The warnings answer reached Search's limit without WellLog; the others have it.
        var cut = Enumerable.Range(0, IndexStatusCommand.BucketLimit).Select(i => ((string?)$"osdu:wks:k{i}:1.0.0", 5L)).ToArray();
        var search = new Search(new()
        {
            [IndexStatusCommand.Warning] = cut,
            [IndexStatusCommand.NoSchema] = [(WellLog, 100)],
            [IndexStatusCommand.Other] = [(Prod, 2)],
        });

        var rows = (await Count(search))["kinds"]!.AsArray();

        var wellLog = rows.Single(row => row!["kind"]!.GetValue<string>() == WellLog)!;
        Assert.Null(wellLog["warnings"]);
        Assert.Equal(100, wellLog["noSchema"]!.GetValue<long>());
        Assert.Equal(0, wellLog["other"]!.GetValue<long>());
        Assert.Null(wellLog["total"]);
        // A kind the cut answer has is known in that column.
        var k0 = rows.Single(row => row!["kind"]!.GetValue<string>() == "osdu:wks:k0:1.0.0")!;
        Assert.Equal(5, k0["warnings"]!.GetValue<long>());
        Assert.Equal(5, k0["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task ABucketWithoutAKeyIsLeftOut()
    {
        var search = new Search(new() { [IndexStatusCommand.Other] = [(null, 9), (WellLog, 1)] });

        var rows = (await Count(search))["kinds"]!.AsArray();

        Assert.Equal([WellLog], rows.Select(row => row!["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task NothingWrongSaysSo()
    {
        var report = await Count(new Search([]), WellLog);

        Assert.Empty(report["kinds"]!.AsArray());
        Assert.Equal($"No records of {WellLog} were indexed with a problem.", IndexStatusCommand.Summary(report));
    }

    [Theory]
    [InlineData(10, 2, 1, IndexStatusCommand.Warning)]
    [InlineData(1, 10, 2, IndexStatusCommand.NoSchema)]
    [InlineData(1, 2, 10, IndexStatusCommand.Other)]
    public async Task TheSuggestedSearchIsForTheTopKindsCommonestCode(long warnings, long noSchema, long other, string expected)
    {
        var search = new Search(new()
        {
            [IndexStatusCommand.Warning] = [(WellLog, warnings)],
            [IndexStatusCommand.NoSchema] = [(WellLog, noSchema)],
            [IndexStatusCommand.Other] = [(WellLog, other)],
        });

        var summary = IndexStatusCommand.Summary(await Count(search));

        Assert.EndsWith($"-q \"{expected}\" --returned-fields id,index", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummaryPointsAtTheTopKindsCommonestCode()
    {
        var search = new Search(new()
        {
            [IndexStatusCommand.Warning] = [(WellLog, 15)],
            [IndexStatusCommand.NoSchema] = [(Prod, 12244)],
        });

        var summary = IndexStatusCommand.Summary(await Count(search));

        Assert.StartsWith(
            "15 with warnings (400) in 1 kind(s), 12,244 without a schema (404) in 1 kind(s), 0 with another code.",
            summary, StringComparison.Ordinal);
        Assert.EndsWith($"osducs record search -k {Prod} -q \"index.statusCode:404\" --returned-fields id,index",
            summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsManyKindsAsSearchAnswersAtMostIsFlagged()
    {
        var many = Enumerable.Range(0, IndexStatusCommand.BucketLimit).Select(i => ((string?)$"osdu:wks:k{i}:1.0.0", 1L)).ToArray();
        var report = await Count(new Search(new() { [IndexStatusCommand.Other] = many }));

        Assert.True(report["truncated"]!.GetValue<bool>());
        Assert.Contains("some may be left out and a blank count is unknown; narrow it with --kind.", IndexStatusCommand.Summary(report), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutputKeepsTheTotalsAndATableShowsTheKinds()
    {
        Assert.Same(OutputSpec.Raw, IndexStatusCommand.SpecFor(OutputFormat.Json));
        Assert.Equal("kinds", IndexStatusCommand.SpecFor(OutputFormat.Table).Root);
    }
}
