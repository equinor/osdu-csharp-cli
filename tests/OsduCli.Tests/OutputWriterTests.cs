using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Output projection is declared as data in the manifests rather than as a JMESPath
/// expression, so these are the tests that stand in for the expression language's own.
/// </summary>
public class OutputWriterTests
{
    private static string Render(string json, OutputSpec spec,
        OutputFormat format = OutputFormat.Table)
    {
        var buffer = new StringWriter();
        var code = new OutputWriter(format, buffer).Write(json, spec);
        Assert.Equal(0, code);
        return buffer.ToString();
    }

    private const string Results = """
        {"results":[
          {"id":"opendes:w:1000","version":1668776656286617,"kind":"osdu:wks:w:1.0.0"},
          {"id":"opendes:w:1001","version":1668776656286618,"kind":"osdu:wks:w:1.0.0"}
        ]}
        """;

    private static readonly OutputSpec ListSpec =
        OutputSpec.Table("results", ("Id", "id"), ("Version", "version"), ("Kind", "kind"));

    [Fact]
    public void ProjectsColumnsIntoAnAlignedTable()
    {
        var lines = Render(Results, ListSpec).TrimEnd().Split(Environment.NewLine);

        Assert.StartsWith("Id", lines[0]);
        Assert.Contains("Version", lines[0]);
        Assert.StartsWith("--", lines[1]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("opendes:w:1000", lines[2]);
        Assert.Contains("1668776656286617", lines[2]);
    }

    [Fact]
    public void ColumnsLineUpAcrossHeaderAndRows()
    {
        var lines = Render(Results, ListSpec).TrimEnd().Split(Environment.NewLine);

        // Every row must start its second column at the same offset, which is what makes
        // the output scannable. Trailing whitespace is trimmed, so comparing full line
        // lengths would not show this — the last column's header is shorter than its
        // content, so the underline is legitimately longer than the header.
        var headerOffset = lines[0].IndexOf("Version", StringComparison.Ordinal);
        Assert.True(headerOffset > 0);
        foreach (var row in lines[2..])
            Assert.Equal(headerOffset, row.IndexOf("16687766", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonFormatSkipsTheProjectionButStillUnwraps()
    {
        var output = Render(Results, ListSpec, OutputFormat.Json);

        Assert.StartsWith("[", output.TrimStart());
        // A field the table drops must survive in JSON output.
        Assert.Contains("\"kind\"", output);
    }

    [Fact]
    public void UnwrapOnlySpecPrintsIndentedJson()
    {
        var output = Render("""{"versions":[1,2],"recordId":"opendes:x"}""",
            OutputSpec.Unwrap("versions"));

        Assert.StartsWith("[", output.TrimStart());
        Assert.DoesNotContain("recordId", output);
    }

    [Fact]
    public void MissingRootFallsBackToTheWholeDocument()
    {
        // OSDU services vary in whether they wrap a single result in an envelope. The
        // Python CLI's `results || {...}` idiom is equally forgiving.
        var output = Render("""{"id":"opendes:x","version":42,"kind":"k"}""", ListSpec);

        Assert.Contains("opendes:x", output);
        Assert.Contains("42", output);
    }

    [Fact]
    public void RendersASingleObjectAsAOneRowTable()
    {
        var lines = Render("""{"id":"a","version":1,"kind":"k"}""", ListSpec)
            .TrimEnd().Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void ReportsAnEmptyResultSetRatherThanPrintingNothing()
    {
        Assert.Contains("no results", Render("""{"results":[]}""", ListSpec));
    }

    [Fact]
    public void MissingColumnRendersEmptyRatherThanThrowing()
    {
        var output = Render("""{"results":[{"id":"a"}]}""", ListSpec);

        Assert.Contains("a", output);
    }

    [Fact]
    public void NestedPathsResolveThroughDots()
    {
        var output = Render("""{"acl":{"owners":"me@example.com"}}""",
            OutputSpec.Table(null, ("Owner", "acl.owners")));

        Assert.Contains("me@example.com", output);
    }

    [Fact]
    public void NonScalarColumnRendersAsCompactJson()
    {
        var output = Render("""{"legal":{"legaltags":["a","b"]}}""",
            OutputSpec.Table(null, ("Tags", "legal.legaltags")));

        Assert.Contains("[\"a\",\"b\"]", output);
    }

    [Fact]
    public void EmptyResponseWritesNothing()
    {
        Assert.Equal("", Render("", OutputSpec.Raw));
    }

    [Fact]
    public void MessageIsWrittenInTableModeOnly()
    {
        var table = new StringWriter();
        new OutputWriter(OutputFormat.Table, table).WriteMessage("1 record deleted");
        Assert.Contains("1 record deleted", table.ToString());

        // In JSON mode a prose line would corrupt a piped document.
        var json = new StringWriter();
        new OutputWriter(OutputFormat.Json, json).WriteMessage("1 record deleted");
        Assert.Equal("", json.ToString());
    }

    [Fact]
    public void ProjectedFieldsBecomeColumns()
    {
        // `record search --returned-fields` — asking for a field has to show it, or the
        // flag fetches data and then hides it.
        var spec = OutputSpec.FromFields("results", ["id", "data.FacilityName"]);

        var output = Render("""{"results":[{"id":"a","data":{"FacilityName":"FR SOULTZ"}}]}""", spec);

        Assert.Contains("FacilityName", output);
        Assert.Contains("FR SOULTZ", output);
    }

    [Fact]
    public void ColumnHeadersAreTheLastDottedSegment()
    {
        var spec = OutputSpec.FromFields(null, ["data.acl.owners"]);

        Assert.Equal("Owners", Assert.Single(spec.Columns).Header);
        Assert.Equal("data.acl.owners", Assert.Single(spec.Columns).Path);
    }

    [Fact]
    public void ANullProjectedValueRendersEmptyRatherThanTheWordNull()
    {
        var spec = OutputSpec.FromFields(null, ["data.Classification"]);

        var output = Render("""{"data":{"Classification":null}}""", spec);

        Assert.DoesNotContain("null", output);
    }

    private static string RenderTotal(string json, OutputFormat format = OutputFormat.Table)
    {
        var buffer = new StringWriter();
        new OutputWriter(format, buffer).WriteTotal(json, "totalCount");
        return buffer.ToString();
    }

    [Fact]
    public void TotalIsReportedAlongsideTheResults()
    {
        // totalCount sits outside the projection root, so without this the count is fetched
        // and discarded.
        Assert.Contains("1,117 matching records", RenderTotal("""{"totalCount":1117,"results":[]}"""));
    }

    [Fact]
    public void TheCappedCountIsMarkedAsACap()
    {
        // Search reports exactly 10000 when the true figure is higher. Printing a bare
        // "10,000" where the truth is 141,286 is worse than printing nothing.
        var output = RenderTotal("""{"totalCount":10000,"results":[]}""");

        Assert.Contains("10,000+", output);
        Assert.Contains("--track-total-count", output);
    }

    [Fact]
    public void TotalIsSuppressedInJsonMode()
    {
        // The field is already in the document being piped; a prose line would corrupt it.
        Assert.Equal("", RenderTotal("""{"totalCount":42,"results":[]}""", OutputFormat.Json));
    }

    [Fact]
    public void AResponseWithNoTotalPrintsNothing()
    {
        Assert.Equal("", RenderTotal("""{"results":[]}"""));
    }

    [Fact]
    public void AnEmptyArrayStillProducesJson()
    {
        // `account list` on a machine with no cached accounts has nothing to report, but
        // something parsing --output json must get valid JSON rather than an empty stream.
        var buffer = new StringWriter();
        new OutputWriter(OutputFormat.Json, buffer).Write("[]", OutputSpec.Table(
            null, ("Account", "account")));

        Assert.Equal("[]", buffer.ToString().Trim());
    }

    // ---- notes ---------------------------------------------------------------------------

    [Theory]
    [InlineData(OutputFormat.Table)]
    [InlineData(OutputFormat.Json)]
    public void ANoteReachesStderrInEveryFormat(OutputFormat format)
    {
        // "The account you selected is not signed in" cannot be expressed in the rows — every
        // row reads as not in use, exactly as it would if nothing had been selected. So it has
        // to survive --output json, and stdout is not the place for it.
        var output = new StringWriter();
        var error = new StringWriter();

        new OutputWriter(format, output, error).WriteNote("not signed in");

        Assert.Equal("not signed in", error.ToString().Trim());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void AResultMessageStaysOnStdoutAndOutOfJson()
    {
        // The other kind: WriteMessage is the result of a command with no response body, so it
        // belongs on stdout and must not corrupt JSON output.
        var table = new StringWriter();
        var json = new StringWriter();

        new OutputWriter(OutputFormat.Table, table).WriteMessage("Workflow deleted");
        new OutputWriter(OutputFormat.Json, json).WriteMessage("Workflow deleted");

        Assert.Equal("Workflow deleted", table.ToString().Trim());
        Assert.Equal("", json.ToString());
    }

    // ---- arrays of scalars ----------------------------------------------------------------

    /// <summary>Exactly what Storage's DatastoreQueryResult looks like.</summary>
    private const string RecordIdPage = """
        {"results":["opendes:master-data--Well:1","opendes:master-data--Well:2"],
         "cursor":"MjAyNQ=="}
        """;

    [Fact]
    public void AnArrayOfStringsRendersTheStringsThemselves()
    {
        // `record list` asked for `id`, `version` and `kind` on elements that are bare record
        // ids, so it printed a header and one blank row per record — data returned, nothing
        // shown. A tester found it against a kind the Python CLI listed fine.
        var buffer = new StringWriter();
        new OutputWriter(OutputFormat.Table, buffer)
            .Write(RecordIdPage, OutputSpec.Table("results", ("Id", ".")));

        var output = buffer.ToString();
        Assert.Contains("opendes:master-data--Well:1", output);
        Assert.Contains("opendes:master-data--Well:2", output);
    }

    [Fact]
    public void AskingForAPropertyOfAStringStillYieldsNothing()
    {
        // The old behaviour, pinned so the difference is visible rather than assumed.
        var buffer = new StringWriter();
        new OutputWriter(OutputFormat.Table, buffer)
            .Write(RecordIdPage, OutputSpec.Table("results", ("Id", "id")));

        Assert.DoesNotContain("opendes:master-data--Well:1", buffer.ToString());
    }

    [Fact]
    public void TheCursorIsReportedSoTheNextPageCanBeAskedFor()
    {
        var error = new StringWriter();
        new OutputWriter(OutputFormat.Table, new StringWriter(), error)
            .WriteCursor(RecordIdPage, "cursor");

        Assert.Contains("--cursor MjAyNQ==", error.ToString());
    }

    [Theory]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[],"cursor":""}""")]
    public void NoCursorMeansNoNote(string json)
    {
        // The last page carries no cursor; inviting the user to page again would be a lie.
        var error = new StringWriter();
        new OutputWriter(OutputFormat.Table, new StringWriter(), error).WriteCursor(json, "cursor");

        Assert.Equal("", error.ToString());
    }
}
