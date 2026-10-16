using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Writing bulk data to the four Wellbore DDMS types that carry it: one file as the whole
/// bulk, or several through an upload session that is committed, or abandoned on failure.
/// </summary>
public sealed class WellboreBulkWriteTests : IDisposable
{
    private const string Record = "dev:work-product-component--WellLog:1";
    private static readonly Guid Session = Guid.Parse("4b2e1c3a-1111-2222-3333-444455556666");
    private static readonly WellboreBulkWrite.Entity WellLog = WellboreBulkWrite.Entities[0];

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("osducs-bulk-");

    public void Dispose() => _directory.Delete(recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static readonly byte[] SplitJson = Encoding.UTF8.GetBytes("""{"columns":["MD","GR"],"index":[0,1],"data":[[1000.0,55.1],[1000.5,56.2]]}""");
    private static readonly byte[] ParquetFile = [.. "PAR1"u8, 1, 2, 3, 4, .. "PAR1"u8];

    /// <summary>Wellbore DDMS, as far as a bulk write sees it.</summary>
    private sealed class Ddms(Func<int, HttpStatusCode>? chunkStatus = null, Action<int>? onChunk = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? ContentType, byte[] Body)> Requests { get; } = [];
        private int _chunks;

        /// <summary>How a commit is answered: refused with this status, or not at all when it throws.</summary>
        public Func<HttpStatusCode>? Commit { get; init; }
        public bool AbandonFails { get; init; }
        /// <summary>The state a session read reports, for a commit that got no answer.</summary>
        public string SessionState { get; init; } = "open";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Content?.Headers.ContentType?.MediaType, body));
            var commit = Encoding.UTF8.GetString(body).Contains("commit");

            var (status, answer) = (request.Method.Method, path) switch
            {
                ("POST", var p) when p.EndsWith("/sessions") => (HttpStatusCode.OK, $$"""{"id":"{{Session}}","state":"open"}"""),
                ("POST", var p) when p.EndsWith($"/sessions/{Session}/data") => Chunk(),
                ("PATCH", _) when commit && Commit is not null => (Commit(), """{"detail":"Bulk data is inconsistent."}"""),
                ("PATCH", _) when !commit && AbandonFails => (HttpStatusCode.Conflict, """{"detail":"Session is not open."}"""),
                ("PATCH", _) => (HttpStatusCode.OK, $$"""{"id":"{{Session}}","state":"{{(commit ? "committed" : "abandoned")}}","version":2}"""),
                ("GET", var p) when p.EndsWith($"/sessions/{Session}") => (HttpStatusCode.OK, $$"""{"id":"{{Session}}","state":"{{SessionState}}","version":3}"""),
                _ => (HttpStatusCode.OK, """{"recordIds":["dev:work-product-component--WellLog:1"]}"""),
            };
            if (status == HttpStatusCode.BadGateway)
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent("<html><head><title>502 Bad Gateway</title></head><body>nginx</body></html>",
                        Encoding.UTF8, "text/html"),
                };
            }
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };

            (HttpStatusCode, string) Chunk()
            {
                _chunks++;
                onChunk?.Invoke(_chunks);
                var chunkAnswer = chunkStatus?.Invoke(_chunks) ?? HttpStatusCode.OK;
                return chunkAnswer == HttpStatusCode.OK
                    ? (chunkAnswer, """{"rowCount":2,"columnCount":2,"columns":["MD","GR"],"indexStart":"0","indexEnd":"1","indexType":"int"}""")
                    : chunkAnswer == HttpStatusCode.BadRequest
                        ? (chunkAnswer, """{"detail":"Column(s) do(es) not match any CurveID of the WellLog record."}""")
                        : (chunkAnswer, """{"detail":[{"loc":["body","data"],"msg":"bad chunk","type":"value_error"},{"loc":["body"],"msg":"and another","type":"value_error"}]}""");
            }
        }
    }

    private static HttpClientRequestAdapter Adapter(Ddms ddms) =>
        new(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(ddms))
        {
            BaseUrl = "https://osdu.example.com/api/os-wellbore-ddms",
        };

    // ---- the file -------------------------------------------------------------------------

    [Fact]
    public void ParquetIsKnownByItsContentNotItsName()
    {
        Assert.Equal(WellboreBulkWrite.Parquet, WellboreBulkWrite.Chunk(Write("curves.json", ParquetFile)).ContentType);
        Assert.Equal(WellboreBulkWrite.Json, WellboreBulkWrite.Chunk(Write("curves.parquet", SplitJson)).ContentType);
    }

    [Fact]
    public void JsonMayStartWithWhitespaceOrAByteOrderMark() =>
        Assert.Equal(WellboreBulkWrite.Json,
            WellboreBulkWrite.Chunk(Write("curves.json", [0xEF, 0xBB, 0xBF, (byte)'\n', (byte)' ', .. SplitJson])).ContentType);

    [Fact]
    public void AFileOfAnotherKindIsRefusedBeforeAnythingIsSent()
    {
        var exception = Assert.Throws<OsduException>(() =>
            WellboreBulkWrite.Chunk(Write("curves.las", Encoding.ASCII.GetBytes("~Version information\n"))));

        Assert.Contains("neither Parquet nor JSON", exception.Message);
        Assert.Contains("File not found", Assert.Throws<OsduException>(() =>
            WellboreBulkWrite.Chunk(Path.Combine(_directory.FullName, "missing.parquet"))).Message);
    }

    // ---- one file ---------------------------------------------------------------------------

    [Theory]
    [InlineData(false, WellboreBulkWrite.Json)]
    [InlineData(true, WellboreBulkWrite.Parquet)]
    public async Task OneFileIsTheWholeBulkSentAsItIs(bool parquet, string contentType)
    {
        var content = parquet ? ParquetFile : SplitJson;
        var ddms = new Ddms();

        await WellboreBulkWrite.WriteAsync(Adapter(ddms), WellLog, Record,
            WellboreBulkWrite.Chunk(Write("curves", content)), TestContext.Current.CancellationToken);

        var request = Assert.Single(ddms.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/os-wellbore-ddms/ddms/v3/welllogs/{Record}/data", Uri.UnescapeDataString(request.Path));
        Assert.Equal(contentType, request.ContentType);
        Assert.Equal(content, request.Body);
    }

    [Theory]
    [InlineData(1, "wellboretrajectories")]
    [InlineData(2, "ppfgdataset")]
    [InlineData(3, "wellpressuretestrawmeasurement")]
    public async Task EachTypeWritesToItsOwnPath(int entity, string path)
    {
        var ddms = new Ddms();

        await WellboreBulkWrite.WriteAsync(Adapter(ddms), WellboreBulkWrite.Entities[entity], "dev:x:1",
            WellboreBulkWrite.Chunk(Write("curves", SplitJson)), TestContext.Current.CancellationToken);

        Assert.Equal($"/api/os-wellbore-ddms/ddms/v3/{path}/dev:x:1/data", Uri.UnescapeDataString(ddms.Requests[0].Path));
    }

    // ---- a session -------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "overwrite")]
    [InlineData(true, "update")]
    public async Task SeveralFilesAreOneSessionCommitted(bool update, string mode)
    {
        var ddms = new Ddms();
        var parts = new[] { Write("a.parquet", ParquetFile), Write("b.json", SplitJson) }.Select(WellboreBulkWrite.Chunk).ToList();

        // Without progress, as in a script: the outcome is still said.
        var notes = new StringWriter();
        var answer = await WellboreBulkWrite.WriteSessionAsync(Adapter(ddms), WellLog, Record, parts, update, null,
            notes, TestContext.Current.CancellationToken);

        Assert.Equal(["POST /sessions", "POST /data", "POST /data", "PATCH /session"],
            ddms.Requests.Select(request => $"{request.Method} /{request.Path.Split('/')[^1].Replace(Session.ToString(), "session")}"));
        Assert.Equal(mode, JsonNode.Parse(ddms.Requests[0].Body)!["mode"]!.GetValue<string>());
        Assert.Equal(WellboreBulkWrite.Parquet, ddms.Requests[1].ContentType);
        Assert.Equal(WellboreBulkWrite.Json, ddms.Requests[2].ContentType);
        Assert.Equal($"/api/os-wellbore-ddms/ddms/v3/welllogs/{Record}/sessions/{Session}/data", Uri.UnescapeDataString(ddms.Requests[1].Path));
        Assert.Equal("commit", JsonNode.Parse(ddms.Requests[3].Body)!["state"]!.GetValue<string>());
        Assert.Equal("committed", JsonNode.Parse(answer!)!["state"]!.GetValue<string>());
        Assert.Equal($"Committed session {Session} as version 2." + Environment.NewLine, notes.ToString());
    }

    [Fact]
    public async Task AFailedChunkAbandonsTheSession()
    {
        // A half-sent upload never becomes a version.
        var ddms = new Ddms(chunkStatus: chunk => chunk == 2 ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK);
        var notes = new StringWriter();
        var parts = new[] { Write("a.json", SplitJson), Write("b.json", SplitJson), Write("c.json", SplitJson) }
            .Select(WellboreBulkWrite.Chunk).ToList();

        // Without progress, as in a script: what became of the session is still said.
        await Assert.ThrowsAnyAsync<ApiException>(() => WellboreBulkWrite.WriteSessionAsync(Adapter(ddms), WellLog, Record,
            parts, false, null, notes, TestContext.Current.CancellationToken));

        Assert.Equal(4, ddms.Requests.Count);
        Assert.Equal("abandon", JsonNode.Parse(ddms.Requests[^1].Body)!["state"]!.GetValue<string>());
        Assert.Contains($"Abandoned session {Session}", notes.ToString());
    }

    [Fact]
    public async Task ARefusalSaysWhyInTheServicesWords()
    {
        // `detail` is a sentence for most refusals; the client mapped only the list form, so
        // "400 from the service." was all anyone saw.
        var ddms = new Ddms(chunkStatus: _ => HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => WellboreBulkWrite.SendChunkAsync(Adapter(ddms),
            WellLog, Record, Session, WellboreBulkWrite.Chunk(Write("a.json", SplitJson)), TestContext.Current.CancellationToken));

        Assert.Equal(400, exception.ResponseStatusCode);
        Assert.Equal("Column(s) do(es) not match any CurveID of the WellLog record.", exception.Message);
        Assert.Equal(exception.Message, CliRunner.Describe(exception));
    }

    [Fact]
    public async Task AValidationFailureNamesWhereAndWhat()
    {
        var ddms = new Ddms(chunkStatus: _ => HttpStatusCode.UnprocessableEntity);

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => WellboreBulkWrite.SendChunkAsync(Adapter(ddms),
            WellLog, Record, Session, WellboreBulkWrite.Chunk(Write("a.json", SplitJson)), TestContext.Current.CancellationToken));

        // Where in the request, without the leading "body" that every entry has.
        Assert.Equal("data: bad chunk; and another", exception.Message);
    }

    [Fact]
    public async Task AGatewaysErrorPageIsReportedByItsTitle()
    {
        // A gateway in front of the service answers with a page, not `detail`.
        TextBodyParseNodeFactory.Register();
        var ddms = new Ddms(chunkStatus: _ => HttpStatusCode.BadGateway);

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => WellboreBulkWrite.SendChunkAsync(Adapter(ddms),
            WellLog, Record, Session, WellboreBulkWrite.Chunk(Write("a.json", SplitJson)), TestContext.Current.CancellationToken));

        Assert.Equal(502, exception.ResponseStatusCode);
        Assert.Equal("502 Bad Gateway", exception.Message);
    }

    private List<WellboreBulkWrite.Part> TwoParts() =>
        new[] { Write("a.json", SplitJson), Write("b.json", SplitJson) }.Select(WellboreBulkWrite.Chunk).ToList();

    [Fact]
    public async Task ARefusedCommitAbandonsTheSession()
    {
        // The service answered: nothing was committed, so the session is let go.
        var ddms = new Ddms { Commit = () => HttpStatusCode.BadRequest };
        var notes = new StringWriter();

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => WellboreBulkWrite.WriteSessionAsync(Adapter(ddms),
            WellLog, Record, TwoParts(), false, null, notes, TestContext.Current.CancellationToken));

        Assert.Equal("Bulk data is inconsistent.", exception.Message);
        Assert.Equal("abandon", JsonNode.Parse(ddms.Requests[^1].Body)!["state"]!.GetValue<string>());
        Assert.Contains("Abandoned session", notes.ToString());
    }

    [Fact]
    public async Task ACommitWithNoAnswerThatWentThroughIsASuccess()
    {
        // The version exists; abandoning, or reporting a failure, would both be wrong.
        var ddms = new Ddms { Commit = () => throw new HttpRequestException("Connection reset"), SessionState = "committed" };
        var notes = new StringWriter();

        var answer = await WellboreBulkWrite.WriteSessionAsync(Adapter(ddms), WellLog, Record, TwoParts(), false, null, notes,
            TestContext.Current.CancellationToken);

        Assert.Equal("committed", JsonNode.Parse(answer!)!["state"]!.GetValue<string>());
        Assert.DoesNotContain(ddms.Requests, request => Encoding.UTF8.GetString(request.Body).Contains("abandon"));
        Assert.Contains("the new version was written", notes.ToString());
    }

    [Fact]
    public async Task ACommitWithNoAnswerIsLeftToCheckNotAbandoned()
    {
        var ddms = new Ddms { Commit = () => throw new HttpRequestException("Connection reset"), SessionState = "committing" };
        var notes = new StringWriter();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => WellboreBulkWrite.WriteSessionAsync(Adapter(ddms), WellLog,
            Record, TwoParts(), false, null, notes, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(ddms.Requests, request => Encoding.UTF8.GetString(request.Body).Contains("abandon"));
        Assert.Contains($"the session is committing. Check it with `osducs welllog data session get --id {Record} --session {Session}`",
            notes.ToString());
    }

    [Fact]
    public async Task AnAbandonThatFailsIsSaidWithoutPromisingWhatItCannot()
    {
        var ddms = new Ddms(chunkStatus: _ => HttpStatusCode.BadRequest) { AbandonFails = true };
        var notes = new StringWriter();

        await Assert.ThrowsAnyAsync<ApiException>(() => WellboreBulkWrite.WriteSessionAsync(Adapter(ddms), WellLog, Record,
            TwoParts(), false, null, notes, TestContext.Current.CancellationToken));

        Assert.Contains($"Session {Session} could not be abandoned. Nothing from it is written unless it is committed", notes.ToString());
    }

    [Fact]
    public async Task AnInterruptionAbandonsTheSessionToo()
    {
        // With a token of its own: the command's is cancelled, and would cancel the abandon.
        using var interrupted = new CancellationTokenSource();
        var ddms = new Ddms(onChunk: chunk => { if (chunk == 1) interrupted.Cancel(); });
        var parts = new[] { Write("a.json", SplitJson), Write("b.json", SplitJson) }.Select(WellboreBulkWrite.Chunk).ToList();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WellboreBulkWrite.WriteSessionAsync(Adapter(ddms),
            WellLog, Record, parts, false, null, new StringWriter(), interrupted.Token));

        Assert.Equal(HttpMethod.Patch, ddms.Requests[^1].Method);
        Assert.Equal("abandon", JsonNode.Parse(ddms.Requests[^1].Body)!["state"]!.GetValue<string>());
    }

    // ---- the command tree ---------------------------------------------------------------------

    private static RootCommand Root()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    [Theory]
    [InlineData("welllog")]
    [InlineData("trajectory")]
    [InlineData("ppfg")]
    [InlineData("pressuretest")]
    public void EachTypeWritesItsBulkData(string noun)
    {
        var data = Root().Subcommands.Single(c => c.Name == noun).Subcommands.Single(c => c.Name == "data");
        var session = data.Subcommands.Single(c => c.Name == "session");

        Assert.Contains(data.Subcommands, c => c.Name == "add");
        Assert.Equal(["get", "list", "open", "send", "update"], session.Subcommands.Select(c => c.Name).Order());
    }

    [Theory]
    [InlineData("welllog version stats")]
    [InlineData("welllog version update-stats")]
    public void ARealVersionNumberIsAccepted(string command)
    {
        // A version is the record's write time in microseconds, sixteen digits: it did not fit
        // the int that `version stats` parsed it into.
        var errors = Root().Parse([.. command.Split(' '), "--id", Record, "--version", "1792150494962265"]).Errors;

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("welllog")]
    [InlineData("trajectory")]
    [InlineData("ppfg")]
    [InlineData("pressuretest")]
    public void ASessionCanStartFromARealVersion(string noun) =>
        Assert.Empty(Root().Parse([noun, "data", "session", "open", "--id", Record, "--mode", "update",
            "--from-version", "1792150494962265"]).Errors);

    [Fact]
    public async Task AVersionToStartFromSurvivesTheRequestModel()
    {
        // The generated command puts the option in JSON and reads it into the client's model.
        var model = await Microsoft.Kiota.Abstractions.Serialization.KiotaJsonSerializer
            .DeserializeAsync<Equinor.OsduCsharpClient.WellboreDdms.Models.CreateDataSessionRequest>(
                """{"mode":"update","fromVersion":1792150494962265}""",
                Equinor.OsduCsharpClient.WellboreDdms.Models.CreateDataSessionRequest.CreateFromDiscriminatorValue,
                TestContext.Current.CancellationToken);

        var json = await Microsoft.Kiota.Abstractions.Serialization.KiotaJsonSerializer.SerializeAsStringAsync(model!, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1792150494962265, JsonNode.Parse(json)!["fromVersion"]!.GetValue<long>());
    }

    [Fact]
    public void ASessionIdIsCheckedWhileParsing()
    {
        var errors = Root().Parse(["welllog", "data", "session", "send", "--id", Record, "--session", "not-a-guid", "--file", "x"]).Errors;

        Assert.NotEmpty(errors);
    }
}
