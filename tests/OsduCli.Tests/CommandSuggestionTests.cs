using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Explaining a command that does not exist, against the real command tree.
/// </summary>
/// <remarks>
/// A tester trying to add a member to a group typed <c>osducs member add group</c>, the right
/// words in the wrong order. They were shown the help for <c>member group</c>, and with
/// <c>-h</c> the unknown <c>add</c> was dropped silently and the help for <c>member</c> came
/// back as though it were the answer.
/// </remarks>
public class CommandSuggestionTests
{
    private static RootCommand BuildRoot()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        root.Subcommands.Add(StatusCommand.Build());
        root.Subcommands.Add(AccountCommand.Build());
        root.Subcommands.Add(ConfigCommand.Build());
        foreach (var command in CompletionCommand.Build(root))
            root.Subcommands.Add(command);
        return root;
    }

    private static CommandSuggestions.Unknown? Find(params string[] args) =>
        CommandSuggestions.Find(BuildRoot(), args);

    [Theory]
    [InlineData("member", "add", "group")]
    [InlineData("member", "add", "group", "-h")]
    [InlineData("member", "add", "-h")]
    [InlineData("member", "add", "--group", "g@example.com")]
    public void TheTestersAttemptsLeadToGroupMemberAdd(params string[] args)
    {
        var unknown = Find(args)!;

        Assert.Equal("add", unknown.Word);
        Assert.Equal(["member"], unknown.ParentPath);
        Assert.Contains("osducs group member add", unknown.Suggestions);
    }

    [Fact]
    public void TheSameWordsInAnotherOrderAreSuggestedFirst()
    {
        // `member add group` is exactly the words of `group member add`, so nothing that merely
        // contains them comes ahead of it.
        Assert.Equal(["osducs group member add"], Find("member", "add", "group")!.Suggestions);
    }

    [Fact]
    public void AGroupCanBeSuggestedToo()
    {
        Assert.Equal(["osducs record version"], Find("version", "record")!.Suggestions);
    }

    [Fact]
    public void CasingDoesNotPreventASuggestion()
    {
        // The parser is case-sensitive, so `Record` is unknown; the suggestion need not be.
        var unknown = Find("Record", "get")!;

        Assert.Equal("Record", unknown.Word);
        Assert.Equal(["osducs record get"], unknown.Suggestions);
    }

    [Fact]
    public void AWordMatchingNothingIsReportedWithoutASuggestion()
    {
        var unknown = Find("frobnicate")!;

        Assert.Equal("frobnicate", unknown.Word);
        Assert.Empty(unknown.ParentPath);
        Assert.Empty(unknown.Suggestions);
    }

    [Theory]
    [InlineData(new[] { "record", "serch" }, "serch", "osducs record search")]
    [InlineData(new[] { "reocrd", "get" }, "reocrd", "osducs record get")]
    [InlineData(new[] { "grup", "member", "add" }, "grup", "osducs group member add")]
    [InlineData(new[] { "legaltg", "list", "-h" }, "legaltg", "osducs legaltag list")]
    public void ATypoIsCorrectedWhenNoReorderingMatches(string[] args, string word, string expected)
    {
        // COMMAND-GRAMMAR.md listed typo suggestions as in place; System.CommandLine 2.0 gives
        // none, so `record serch` got "Unrecognized command" and nothing more.
        var unknown = Find(args)!;

        Assert.Equal(word, unknown.Word);
        Assert.Equal(expected, unknown.Suggestions[0]);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("frobnicate")]
    [InlineData("zzzz")]
    public void AWordTooFarFromAnyCommandGetsNoCorrection(string word)
    {
        Assert.Empty(Find(word)!.Suggestions);
    }

    [Theory]
    [InlineData("record", "record", 0)]
    [InlineData("reocrd", "record", 1)]
    [InlineData("serch", "search", 1)]
    [InlineData("grup", "group", 1)]
    [InlineData("abc", "xyz", 3)]
    public void EditDistanceCountsASwapOfNeighbouringLettersAsOne(string a, string b, int expected)
    {
        Assert.Equal(expected, CommandSuggestions.EditDistance(a, b));
    }

    [Fact]
    public void HiddenCommandsAreNeverSuggested()
    {
        Assert.DoesNotContain(
            CommandSuggestions.Suggest(BuildRoot(), ["complete"]),
            suggestion => suggestion.Contains("complete", StringComparison.Ordinal)
                && !suggestion.Contains("completion", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("group", "member", "add", "-g", "g@example.com", "-m", "m@example.com")]
    [InlineData("group", "member", "add", "-h")]
    [InlineData("status", "storage")]
    [InlineData("config", "use", "dev")]
    [InlineData("-c", "dev", "record", "get")]
    public void ALineNamingRealCommandsIsLeftAlone(params string[] args)
    {
        // Positional arguments (`status storage`, `config use dev`) and anything after an
        // option are not command words, and are for the parser to judge.
        Assert.Null(Find(args));
    }

    [Fact]
    public void TheReportNamesTheWordSuggestsTheCommandAndShowsTheRightHelp()
    {
        var error = new StringWriter();
        var output = new StringWriter();

        var code = CommandSuggestions.Report(Find("member", "add", "group")!, error, output);

        Assert.Equal(1, code);
        Assert.Equal(
            "'add' is not a command under `osducs member`." + Environment.NewLine
            + "Did you mean `osducs group member add`?" + Environment.NewLine,
            error.ToString());
        // The help for `member`, which `add` followed, not for `member group`, where the
        // parser stopped and which is what misled the tester. Matched on the usage line's
        // ending, since inside the test host the root is named after the host, not `osducs`.
        Assert.Contains(" member [command] [options]", output.ToString());
        Assert.DoesNotContain(" member group [command]", output.ToString());
    }

    [Fact]
    public void SeveralSuggestionsAreListed()
    {
        var error = new StringWriter();
        var unknown = new CommandSuggestions.Unknown(
            "x", BuildRoot(), [], ["osducs a b", "osducs c d"]);

        CommandSuggestions.Report(unknown, error, new StringWriter());

        Assert.Contains("Did you mean one of these?", error.ToString());
        Assert.Contains("  osducs a b", error.ToString());
        Assert.Contains("  osducs c d", error.ToString());
    }
}
