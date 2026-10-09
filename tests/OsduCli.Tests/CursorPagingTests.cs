using System.CommandLine;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Following Search's cursor past one page: <c>record search --limit 25000</c> and <c>--all</c>.
/// </summary>
/// <remarks>
/// One request answers at most 1000 records and <c>--offset</c> cannot reach past 10000, so a
/// larger answer had no way to be asked for.
/// </remarks>
public class CursorPagingTests
{
    /// <summary>A cursor endpoint over <paramref name="available"/> records, recording what it is asked.</summary>
    private sealed class Service(int available, long? total = null)
    {
        public List<JsonObject> Requests { get; } = [];
        public List<string> Released { get; } = [];
        public CancellationToken ReleaseToken { get; private set; }
        public Func<int, Exception?> FailOnPage { get; init; } = _ => null;
        private int _served;

        public Task<string?> Fetch(JsonObject request, CancellationToken _)
        {
            Requests.Add(request);
            if (FailOnPage(Requests.Count) is { } failure)
                throw failure;

            var size = request["limit"]!.GetValue<int>();
            var page = new JsonArray([.. Enumerable.Range(_served, Math.Min(size, available - _served))
                .Select(i => (JsonNode)new JsonObject { ["id"] = $"r{i}" })]);
            _served += page.Count;

            var response = new JsonObject
            {
                ["results"] = page,
                // A cursor while anything is left, as Search answers.
                ["cursor"] = _served < available ? $"c{Requests.Count}" : null,
            };
            if (total is not null && Requests.Count == 1)
                response["totalCount"] = total;
            return Task.FromResult<string?>(response.ToJsonString());
        }

        public Task Release(string cursor, CancellationToken cancellationToken)
        {
            Released.Add(cursor);
            ReleaseToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private static readonly JsonObject Request = new() { ["kind"] = "osdu:wks:*:*", ["returnedFields"] = new JsonArray("id") };

    private static async Task<JsonObject> Collect(Service service, int? limit, TextWriter? progress = null,
        CancellationToken cancellationToken = default) =>
        (JsonObject)JsonNode.Parse(await CursorPaging.CollectAsync(
            Request, limit, 1000, "limit", "cursor", "results", "totalCount",
            service.Fetch, service.Release, progress, cancellationToken))!;

    private static IEnumerable<string> Ids(JsonObject collected) =>
        collected["results"]!.AsArray().Select(item => item!["id"]!.GetValue<string>());

    [Fact]
    public async Task AllFollowsTheCursorToTheEnd()
    {
        var service = new Service(2500);

        var collected = await Collect(service, limit: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 2500).Select(i => $"r{i}"), Ids(collected));
        Assert.Equal([1000, 1000, 1000], service.Requests.Select(request => request["limit"]!.GetValue<int>()));
        // The last page came back short, so nothing is left open.
        Assert.Empty(service.Released);
    }

    [Fact]
    public async Task EachPageCarriesTheRequestAndTheCursorItWasGiven()
    {
        var service = new Service(2500);

        await Collect(service, limit: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(service.Requests[0]["cursor"]);
        Assert.Equal(["c1", "c2"], service.Requests.Skip(1).Select(request => request["cursor"]!.GetValue<string>()));
        Assert.All(service.Requests, request => Assert.Equal("osdu:wks:*:*", request["kind"]!.GetValue<string>()));
        // Copies: the request the caller built is not changed.
        Assert.Null(Request["cursor"]);
        Assert.Null(Request["limit"]);
    }

    [Fact]
    public async Task ALimitStopsThereAndAsksForNoMore()
    {
        var service = new Service(10_000);

        var collected = await Collect(service, limit: 2500, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2500, Ids(collected).Count());
        Assert.Equal([1000, 1000, 500], service.Requests.Select(request => request["limit"]!.GetValue<int>()));
        // Stopped with records left, so the cursor still open is released.
        Assert.Equal(["c3"], service.Released);
    }

    [Fact]
    public async Task ALimitPastTheEndReturnsWhatThereIs()
    {
        var collected = await Collect(new Service(1500), limit: 5000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1500, Ids(collected).Count());
    }

    [Fact]
    public async Task NoMatchesIsAnEmptyAnswer()
    {
        var collected = await Collect(new Service(0), limit: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(Ids(collected));
    }

    [Fact]
    public async Task TheTotalComesFromTheFirstPage()
    {
        // As --track-total-count reports it, beside the results, where the command reads it.
        var collected = await Collect(new Service(1500, total: 1500), limit: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1500, collected["totalCount"]!.GetValue<long>());
    }

    [Fact]
    public async Task AFailureReleasesTheCursorAndIsNotSwallowed()
    {
        var service = new Service(5000) { FailOnPage = page => page == 3 ? new HttpRequestException("broken") : null };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Collect(service, limit: null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["c2"], service.Released);
    }

    [Fact]
    public async Task AnInterruptionStillReleasesTheCursor()
    {
        // With a token of its own: the command's is cancelled, and would cancel the release.
        using var interrupted = new CancellationTokenSource();
        var service = new Service(5000)
        {
            FailOnPage = page =>
            {
                if (page != 2)
                    return null;
                interrupted.Cancel();
                return new OperationCanceledException(interrupted.Token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(service, limit: null, cancellationToken: interrupted.Token));

        Assert.Equal(["c1"], service.Released);
        Assert.False(service.ReleaseToken.IsCancellationRequested);
    }

    [Fact]
    public async Task AFailedReleaseDoesNotChangeTheOutcome()
    {
        var collected = JsonNode.Parse(await CursorPaging.CollectAsync(
            Request, 1500, 1000, "limit", "cursor", "results", null,
            new Service(5000).Fetch, (_, _) => throw new HttpRequestException("release refused"),
            null, TestContext.Current.CancellationToken))!;

        Assert.Equal(1500, collected["results"]!.AsArray().Count);
    }

    [Fact]
    public async Task ProgressIsReportedOnOneLine()
    {
        var progress = new StringWriter();

        await Collect(new Service(2500, total: 2500), limit: null, progress, TestContext.Current.CancellationToken);

        Assert.Equal("\rFetched 1,000 of 2,500\rFetched 2,000 of 2,500\rFetched 2,500 of 2,500" + Environment.NewLine,
            progress.ToString());
    }

    [Fact]
    public void RecordSearchHasAll()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);

        var search = root.Subcommands.Single(c => c.Name == "record").Subcommands.Single(c => c.Name == "search");

        Assert.Contains(search.Options, option => option.Name == "--all");
    }
}
