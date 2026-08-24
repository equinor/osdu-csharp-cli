using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Completion is built by hand rather than taken from System.CommandLine, which returns
/// option names and nothing else. These pin the behaviour that replaces it.
/// </summary>
public class CompletionTests
{
    private static RootCommand BuildRoot()
    {
        var root = new RootCommand("osdu — command line for the OSDU platform.");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        root.Subcommands.Add(StatusCommand.Build());
        foreach (var command in CompletionCommand.Build(root))
            root.Subcommands.Add(command);
        return root;
    }

    private static string[] Complete(params string[] typed) =>
        CompletionCommand.Candidates(BuildRoot(), typed).ToArray();

    [Fact]
    public void RootOffersNounsRatherThanOptions()
    {
        var candidates = Complete("");

        Assert.Contains("record", candidates);
        Assert.Contains("status", candidates);
        // Eleven spellings of --help would bury the nouns.
        Assert.DoesNotContain("--help", candidates);
    }

    [Fact]
    public void PartialNounIsFiltered()
    {
        Assert.Equal(["record"], Complete("rec"));
    }

    [Fact]
    public void GroupOffersItsVerbs()
    {
        var candidates = Complete("record", "");

        Assert.Contains("get", candidates);
        Assert.Contains("list", candidates);
        Assert.DoesNotContain("--config", candidates);
    }

    [Fact]
    public void PartialVerbIsFiltered()
    {
        Assert.Equal(["get"], Complete("record", "ge"));
    }

    [Fact]
    public void OptionsAppearOnceTheUserTypesADash()
    {
        var candidates = Complete("record", "-");

        Assert.Contains("--output", candidates);
        Assert.All(candidates, c => Assert.StartsWith("-", c));
    }

    [Fact]
    public void LeafWithNoSubcommandsOffersItsOptions()
    {
        var candidates = Complete("record", "get", "");

        Assert.Contains("--id", candidates);
        Assert.Contains("-id", candidates);
    }

    [Fact]
    public void OptionValuesComeFromTheSpecsEnum()
    {
        // The generator emits these from the spec; the manifest once had three of six.
        var candidates = Complete("workflow", "run", "update", "--status", "");

        Assert.Contains("running", candidates);
        Assert.Contains("finished", candidates);
        // Only values here — a sibling option would be noise.
        Assert.DoesNotContain("--name", candidates);
    }

    [Fact]
    public void PartialOptionValueIsFiltered()
    {
        var candidates = Complete("workflow", "run", "update", "--status", "f");

        Assert.All(candidates, c => Assert.StartsWith("f", c));
        Assert.Contains("failed", candidates);
    }

    [Fact]
    public void ArgumentCompletionSourcesAreUsed()
    {
        // `osdu status <service>` has a hand-written completion source.
        var candidates = Complete("status", "");

        Assert.Contains("storage", candidates);
        Assert.Contains("search", candidates);
    }

    [Fact]
    public void HiddenCommandsAreNotSuggested()
    {
        Assert.DoesNotContain("complete", Complete(""));
    }

    [Fact]
    public void CandidatesAreSortedAndDeduplicated()
    {
        var candidates = Complete("");

        Assert.Equal(candidates.OrderBy(c => c, StringComparer.Ordinal), candidates);
        Assert.Equal(candidates.Distinct(), candidates);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    public void EveryShellScriptMentionsTheWorkerCommand(string shell)
    {
        var script = CompletionCommand.ScriptFor(shell);

        Assert.Contains("osdu complete", script);
        Assert.Contains("osdu completion " + shell, script);
    }

    [Fact]
    public void BashCapturesBeforeSettingIfs()
    {
        // Setting IFS before expanding "${COMP_WORDS[@]:1}" makes bash collapse the slice
        // into one joined argument, and completion silently returns nothing beyond the
        // first level. Capture first, split second.
        var script = CompletionCommand.ScriptFor("bash");

        var capture = script.IndexOf("candidates=$(osdu complete", StringComparison.Ordinal);
        var setIfs = script.IndexOf("local IFS=", StringComparison.Ordinal);

        Assert.True(capture > 0 && setIfs > capture,
            "IFS must be set after the arguments have been expanded");
    }
}
