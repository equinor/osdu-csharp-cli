using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Storage;
using Equinor.OsduCsharpClient.Storage.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>record add</c> — creating and updating records of any kind, asking before it updates one
/// that exists.
/// </summary>
public class RecordAddTests
{
    private const string Partition = "dev";

    private static JsonObject Record(string? id = "dev:reference-data--Thing:1", string? kind = "osdu:wks:reference-data--Thing:1.0.0",
        JsonObject? data = null) =>
        new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["acl"] = new JsonObject { ["owners"] = new JsonArray("data.default.owners@dev.dataservices.energy"),
                                       ["viewers"] = new JsonArray("data.default.viewers@dev.dataservices.energy") },
            ["legal"] = new JsonObject { ["legaltags"] = new JsonArray("dev-public"),
                                         ["otherRelevantDataCountries"] = new JsonArray("NO") },
            ["data"] = data ?? new JsonObject { ["Name"] = "thing" },
        };

    // ---- reading ------------------------------------------------------------------------

    [Fact]
    public void ASingleRecordOrAnArrayIsRead()
    {
        Assert.Single(RecordAddCommand.Read(Record().ToJsonString(), Partition));
        Assert.Equal(2, RecordAddCommand.Read(new JsonArray(Record("dev:a:1"), Record("dev:a:2")).ToJsonString(), Partition).Count);
    }

    [Theory]
    [InlineData("42", "must hold a record")]
    [InlineData("[]", "empty array")]
    [InlineData("[1]", "record 1 is not a JSON object")]
    public void AFileWithoutRecordsIsRefused(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<OsduException>(() => RecordAddCommand.Read(json, Partition)).Message);
    }

    [Fact]
    public void MoreRecordsThanOneRequestTakesAreRefused()
    {
        var many = new JsonArray([.. Enumerable.Range(0, 501).Select(i => (JsonNode)Record($"dev:a:{i}"))]);

        var exception = Assert.Throws<OsduException>(() => RecordAddCommand.Read(many.ToJsonString(), Partition));

        Assert.Contains("501 records", exception.Message);
        Assert.Contains("at most 500", exception.Message);
    }

    [Fact]
    public void EveryProblemIsListedWithItsRecord()
    {
        // All at once, so a file is fixed in one pass rather than one error per attempt.
        var broken = Record("dev:a:1");
        broken.Remove("kind");
        ((JsonObject)broken["acl"]!).Remove("viewers");
        broken["data"] = "not an object";
        var json = new JsonArray(Record("dev:a:0"), broken).ToJsonString();

        var message = Assert.Throws<OsduException>(() => RecordAddCommand.Read(json, Partition)).Message;

        Assert.Contains("nothing was sent", message);
        Assert.Contains("record 2 (dev:a:1): kind is missing", message);
        Assert.Contains("record 2 (dev:a:1): data is missing, or not an object", message);
        Assert.Contains("record 2 (dev:a:1): acl.viewers is missing or empty", message);
        Assert.DoesNotContain("record 1", message);
    }

    [Fact]
    public void AnIdFromAnotherPartitionIsRefused()
    {
        // Storage refuses it as "does not belong to account", which says little.
        var message = Assert.Throws<OsduException>(() =>
            RecordAddCommand.Read(Record("prod:reference-data--Thing:1").ToJsonString(), Partition)).Message;

        Assert.Contains("must start with 'dev:'", message);
    }

    [Fact]
    public void AnIdTwiceInTheFileIsRefused()
    {
        var json = new JsonArray(Record("dev:a:1"), Record("dev:a:1")).ToJsonString();

        Assert.Contains("more than once", Assert.Throws<OsduException>(() => RecordAddCommand.Read(json, Partition)).Message);
    }

    [Fact]
    public void ManyProblemsAreCountedPastTheFirstTen()
    {
        var json = new JsonArray([.. Enumerable.Range(0, 12).Select(i => (JsonNode)Record($"dev:a:{i}", kind: null))]).ToJsonString();

        Assert.Contains("…and 2 more", Assert.Throws<OsduException>(() => RecordAddCommand.Read(json, Partition)).Message);
    }

    [Fact]
    public void OnlyRecordsWithAnIdAreLookedUp()
    {
        var records = RecordAddCommand.Read(new JsonArray(Record("dev:a:1"), Record(id: null)).ToJsonString(), Partition);

        Assert.Equal(["dev:a:1"], RecordAddCommand.Ids(records));
    }

    // ---- what exists ----------------------------------------------------------------------

    [Fact]
    public async Task OnlyRecordsThatExistAreReturned()
    {
        var existing = await RecordAddCommand.FetchExistingAsync(["dev:a:1", "dev:a:2"],
            (id, _) => Task.FromResult(id == "dev:a:1" ? Record(id) : null), TestContext.Current.CancellationToken);

        Assert.Equal(["dev:a:1"], existing.Keys);
    }

    [Fact]
    public async Task TheFirstIsFetchedAloneAndTheRestAFewAtATime()
    {
        // The first request may open a browser to sign in; several at once could open several.
        var active = 0;
        var most = 0;
        var firstAlone = false;
        var ids = Enumerable.Range(0, 30).Select(i => $"dev:a:{i}").ToList();

        await RecordAddCommand.FetchExistingAsync(ids, async (id, _) =>
        {
            var now = Interlocked.Increment(ref active);
            if (id == ids[0])
                firstAlone = now == 1;
            lock (ids)
                most = Math.Max(most, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref active);
            return null;
        }, TestContext.Current.CancellationToken);

        Assert.True(firstAlone);
        Assert.InRange(most, 2, 8);
    }

    // ---- what would change ---------------------------------------------------------------

    private static JsonObject Stored(JsonObject record, long version = 1700000000000000,
        string user = "someone@equinor.com", string time = "2026-09-30T08:15:00.000Z")
    {
        var stored = (JsonObject)record.DeepClone();
        stored["version"] = version;
        stored["createUser"] = "creator@equinor.com";
        stored["createTime"] = "2026-01-01T00:00:00.000Z";
        stored["modifyUser"] = user;
        stored["modifyTime"] = time;
        return stored;
    }

    [Fact]
    public void TheSameRecordChangesNothing()
    {
        // The system fields Storage adds are not part of the comparison.
        Assert.Empty(RecordAddCommand.Changes(Stored(Record()), Record()));
    }

    [Fact]
    public void ChangesAreNamedFieldByField()
    {
        var current = Stored(Record(data: new JsonObject { ["Name"] = "a", ["Configurations"] = new JsonArray(1, 2), ["Old"] = true }));
        var proposed = Record(data: new JsonObject { ["Name"] = "a", ["Configurations"] = new JsonArray(1), ["New"] = true });
        proposed["acl"]!["viewers"] = new JsonArray("someone.else@dev.dataservices.energy");

        Assert.Equal(["changes acl", "changes data.Configurations", "adds data.New", "removes data.Old"],
            RecordAddCommand.Changes(current, proposed));
    }

    [Fact]
    public void ThePropertyOrderOfAnObjectIsNotAChange()
    {
        var current = Stored(Record(data: new JsonObject { ["A"] = new JsonObject { ["x"] = 1, ["y"] = 2 } }));
        var proposed = Record(data: new JsonObject { ["A"] = new JsonObject { ["y"] = 2, ["x"] = 1 } });

        Assert.Empty(RecordAddCommand.Changes(current, proposed));
    }

    [Fact]
    public void AnEmptyPartIsTheSameAsAMissingOne()
    {
        // Storage returns some parts filled in, such as an empty `tags`, that a file leaves out.
        var current = Stored(Record());
        current["tags"] = new JsonObject();
        current["meta"] = new JsonArray();

        Assert.Empty(RecordAddCommand.Changes(current, Record()));
    }

    [Fact]
    public void AnUpdateIsDescribedWithWhatItReplaces()
    {
        var current = Stored(Record(data: new JsonObject { ["Name"] = "old" }));
        var plan = RecordAddCommand.Plan([Record()], new Dictionary<string, JsonObject> { ["dev:reference-data--Thing:1"] = current });

        Assert.True(plan[0].IsUpdate);
        Assert.Equal(
            "dev:reference-data--Thing:1: replaces version 1700000000000000, last changed by someone@equinor.com "
            + "on 2026-09-30; changes data.Name",
            RecordAddCommand.Describe(plan[0]));
    }

    [Fact]
    public void ARecordWithoutAStoredVersionIsCreated()
    {
        var plan = RecordAddCommand.Plan([Record(), Record(id: null)], new Dictionary<string, JsonObject>());

        Assert.All(plan, entry => Assert.False(entry.IsUpdate));
    }

    // ---- asking ---------------------------------------------------------------------------

    private static IReadOnlyList<RecordAddCommand.PlannedRecord> OneUpdate() =>
        RecordAddCommand.Plan([Record()],
            new Dictionary<string, JsonObject> { ["dev:reference-data--Thing:1"] = Stored(Record()) });

    private static (OutputWriter Writer, StringWriter Error) Writer()
    {
        var error = new StringWriter();
        return (new OutputWriter(OutputFormat.Table, new StringWriter(), error), error);
    }

    [Fact]
    public void NothingToUpdateGoesAheadWithoutAsking()
    {
        var plan = RecordAddCommand.Plan([Record()], new Dictionary<string, JsonObject>());

        Assert.True(RecordAddCommand.Confirm(plan, yes: false,
            _ => throw new InvalidOperationException("nothing to ask"), Writer().Writer));
    }

    [Fact]
    public void YesGoesAheadWithoutAsking()
    {
        Assert.True(RecordAddCommand.Confirm(OneUpdate(), yes: true,
            _ => throw new InvalidOperationException("--yes was given"), Writer().Writer));
    }

    [Fact]
    public void WithNobodyToAskAnUpdateIsRefused()
    {
        var (writer, error) = Writer();

        var exception = Assert.Throws<OsduException>(() => RecordAddCommand.Confirm(OneUpdate(), yes: false, ask: null, writer));

        Assert.Contains("1 record already exists", exception.Message);
        Assert.Contains("--yes", exception.Message);
        Assert.Contains("--dry-run", exception.Message);
        // Which one, and what it replaces, before the refusal.
        Assert.Contains("replaces version 1700000000000000", error.ToString());
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData(" YES ", true)]
    [InlineData("", false)]
    [InlineData("n", false)]
    [InlineData(null, false)]
    public void ThePersonAtTheTerminalDecides(string? answer, bool goesAhead)
    {
        var (writer, error) = Writer();
        var asked = new List<string>();

        var result = RecordAddCommand.Confirm(OneUpdate(), yes: false, question => { asked.Add(question); return answer; }, writer);

        Assert.Equal(goesAhead, result);
        Assert.Equal("1 record already exists. Update it? [y/N]", Assert.Single(asked));
        Assert.Equal(!goesAhead, error.ToString().Contains("Nothing was written.", StringComparison.Ordinal));
    }

    // ---- writing --------------------------------------------------------------------------

    private sealed class Capture(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static (StorageClient Client, HttpClientRequestAdapter Adapter) Storage(Capture capture)
    {
        TextBodyParseNodeFactory.Register();
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(capture))
        {
            BaseUrl = "https://osdu.example.com/api/storage/v2",
        };
        return (new StorageClient(adapter), adapter);
    }

    private const string Created = """
        {"recordCount":1,"recordIds":["dev:reference-data--Thing:1"],
         "recordIdVersions":["dev:reference-data--Thing:1:1700000000000001"],"skippedRecordIds":[]}
        """;

    [Fact]
    public async Task TheFileIsSentAsItIs()
    {
        // A timestamp and a number with a trailing zero, which a round trip through the
        // client's model could reformat, on a record other people rely on.
        const string file = """{"id":"dev:reference-data--Thing:1","kind":"k","data":{"When":"2026-10-09T10:01:37.120Z","Ratio":1.50}}""";
        var capture = new Capture(HttpStatusCode.Created, Created);
        var (client, adapter) = Storage(capture);

        var response = await RecordAddCommand.PutAsync(client.Records, adapter, file, skipUnchanged: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, capture.Request!.Method);
        Assert.Equal("https://osdu.example.com/api/storage/v2/records", capture.Request.RequestUri!.ToString());
        Assert.Equal("[" + file + "]", capture.Body);
        Assert.Equal(["dev:reference-data--Thing:1"], response!.RecordIds!);
    }

    [Fact]
    public async Task SkipUnchangedAsksStorageToSkipDuplicates()
    {
        var capture = new Capture(HttpStatusCode.Created, Created);
        var (client, adapter) = Storage(capture);

        await RecordAddCommand.PutAsync(client.Records, adapter, Record().ToJsonString(), skipUnchanged: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("?skipdupes=true", capture.Request!.RequestUri!.Query);
    }

    [Fact]
    public async Task AStorageErrorIsReportedLikeAnyOther()
    {
        var capture = new Capture(HttpStatusCode.BadRequest, """{"code":400,"reason":"Validation error.","message":"Invalid legal tags"}""");
        var (client, adapter) = Storage(capture);

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => RecordAddCommand.PutAsync(
            client.Records, adapter, Record().ToJsonString(), skipUnchanged: false, TestContext.Current.CancellationToken));

        Assert.Equal(400, exception.ResponseStatusCode);
        Assert.Equal("Invalid legal tags", CliRunner.Describe(exception));
    }

    [Fact]
    public void EachRecordIsReportedCreatedUpdatedOrUnchanged()
    {
        var response = new CreateUpdateRecordsResponse
        {
            RecordIds = ["dev:new:1", "dev:old:1"],
            RecordIdVersions = ["dev:new:1:11", "dev:old:1:22"],
            SkippedRecordIds = ["dev:same:1"],
        };
        var existing = new Dictionary<string, JsonObject>
        {
            ["dev:old:1"] = Stored(Record("dev:old:1")),
            ["dev:same:1"] = Stored(Record("dev:same:1"), version: 33),
        };

        var rows = RecordAddCommand.Results(response, existing);

        Assert.Equal(
            """[{"id":"dev:new:1","version":"11","result":"created"},{"id":"dev:old:1","version":"22","result":"updated"},{"id":"dev:same:1","version":"33","result":"unchanged"}]""",
            rows.ToJsonString());
    }

    [Fact]
    public void AVersionIsFoundForAnIdEndingInAColon()
    {
        // `dev:master-data--Field:Snorre:` is a real id shape; splitting at the last colon fails.
        Assert.Equal("123", RecordAddCommand.VersionOf("dev:master-data--Field:Snorre:", ["dev:master-data--Field:Snorre::123"]));
        Assert.Null(RecordAddCommand.VersionOf("dev:a:1", ["dev:a:10:5"]));
    }

    // ---- the command ----------------------------------------------------------------------

    [Fact]
    public void RecordAddIsUnderRecord()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);

        var add = root.Subcommands.Single(c => c.Name == "record").Subcommands.Single(c => c.Name == "add");

        Assert.Equal(["--file", "--dry-run", "--yes", "--skip-unchanged"], add.Options.Select(option => option.Name));
    }
}
