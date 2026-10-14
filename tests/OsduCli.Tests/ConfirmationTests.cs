using System.CommandLine;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// The question a command asks before it deletes, or reaches across a whole data partition:
/// a manifest's <c>confirm:</c>, first used by the Indexer's <c>index</c> commands.
/// </summary>
[Collection(nameof(EnvironmentCollection))]
public class ConfirmationTests : ConfigTestDirectories
{
    private const string Action = "Delete the search index of osdu:wks:x:1.0.0.";
    private const string Where = "Data partition dev on https://osdu.example.com.";

    private static (bool GoesAhead, string Said) Confirm(bool yes, Func<string, string?>? ask)
    {
        var error = new StringWriter();
        var goesAhead = Confirmation.Confirm(Action, yes, Where, new OutputWriter(OutputFormat.Table, new StringWriter(), error), ask);
        return (goesAhead, error.ToString());
    }

    [Fact]
    public void YesSkipsTheQuestionButNotWhatIsAboutToHappen()
    {
        // So a script's log still says what it did, and where.
        var (goesAhead, said) = Confirm(yes: true, ask: _ => throw new InvalidOperationException("asked"));

        Assert.True(goesAhead);
        Assert.Equal(Action + Environment.NewLine + Where + Environment.NewLine, said);
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData(" Yes ", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAYesGoesAhead(string? answer, bool expected)
    {
        string? asked = null;

        var (goesAhead, said) = Confirm(yes: false, ask: question =>
        {
            asked = question;
            return answer;
        });

        Assert.Equal("Go ahead? [y/N]", asked);
        Assert.Equal(expected, goesAhead);
        Assert.Equal(!expected, said.Contains("Nothing was done.", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNobodyToAskItRefuses()
    {
        // Guessing yes is the accident this exists to prevent.
        var exception = Assert.Throws<OsduException>(() => Confirm(yes: false, ask: null));

        Assert.Equal("This needs confirming, and there is no terminal to ask at. Pass --yes to go ahead.", exception.Message);
    }

    [Theory]
    [InlineData(new string[0], "none")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b", "c" }, "a, b, c")]
    [InlineData(new[] { "a", "b", "c", "d", "e" }, "a, b, c and 2 more")]
    public void AListIsShortenedRatherThanBuryingTheQuestion(string[] values, string expected) =>
        Assert.Equal(expected, Confirmation.List(values));

    // ---- the index commands ---------------------------------------------------------------

    private static RootCommand Root()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    [Theory]
    [InlineData("reindex", true)]
    [InlineData("reindex-records", false)]
    [InlineData("reindex-partition", true)]
    [InlineData("delete", true)]
    [InlineData("provision", true)]
    public void TheIndexCommandsAskWhereTheyShould(string verb, bool asks)
    {
        var index = Root().Subcommands.Single(command => command.Name == "index");

        var command = Assert.Single(index.Subcommands, command => command.Name == verb);
        Assert.Equal(asks, command.Options.Any(option => option.Name == "--yes"));
    }

    [Fact]
    public async Task ADeleteSaysWhatAndWhereAndStopsWithoutAYes()
    {
        // A profile signing in as an application, so nothing can open a browser; the command
        // stops before any request, so nothing signs in at all.
        var profile = WritePythonProfile("ops", "https://osdu.example.com",
            "authentication_mode = msal_non_interactive", "client_secret = not-used");
        var error = new StringWriter();
        var (originalError, originalIn) = (Console.Error, Console.In);
        Console.SetError(error);
        // Answers no if the test host's input is a terminal; refused outright if it is not.
        Console.SetIn(new StringReader("n" + Environment.NewLine));
        int code;
        try
        {
            code = await Root().Parse(["index", "delete", "--kind", "osdu:wks:x:1.0.0", "-c", profile])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }

        Assert.Equal(1, code);
        var said = error.ToString();
        Assert.Contains("Delete the search index of osdu:wks:x:1.0.0. Its records stay in Storage", said);
        Assert.Contains("Data partition pypartition on https://osdu.example.com.", said);
        Assert.True(said.Contains("Pass --yes to go ahead.", StringComparison.Ordinal)
                    || said.Contains("Nothing was done.", StringComparison.Ordinal), said);
    }
}
