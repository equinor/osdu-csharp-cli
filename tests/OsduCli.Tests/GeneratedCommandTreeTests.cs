using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Walks the real command tree, so a regression in a manifest or in the generator shows up
/// here rather than in a user's shell. Previously checked by hand at the end of a session;
/// this makes it permanent.
/// </summary>
public class GeneratedCommandTreeTests
{
    private static RootCommand BuildRoot()
    {
        var root = new RootCommand("osdu — command line for the OSDU platform.");
        GlobalOptions.AddTo(root);
        CliHelp.Install(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        root.Subcommands.Add(StatusCommand.Build());
        return root;
    }

    private static IEnumerable<(string Path, Command Command)> Walk(
        Command command, string prefix = "")
    {
        var path = string.IsNullOrEmpty(prefix) ? command.Name : $"{prefix} {command.Name}";
        yield return (path, command);
        foreach (var child in command.Subcommands)
            foreach (var descendant in Walk(child, path))
                yield return descendant;
    }

    private static List<(string Path, Command Command)> AllNodes() =>
        BuildRoot().Subcommands.SelectMany(c => Walk(c)).ToList();

    [Fact]
    public void TreeIsNotEmpty()
    {
        // Guards against a generator that silently emits nothing.
        Assert.True(AllNodes().Count > 50, "the generated tree should cover the core services");
    }

    [Fact]
    public void EveryNodeHasADescription()
    {
        var missing = AllNodes()
            .Where(n => string.IsNullOrWhiteSpace(n.Command.Description))
            .Select(n => n.Path)
            .ToList();

        Assert.True(missing.Count == 0,
            "help must be self-explanatory at every level; missing description on: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void NoTwoNodesShareACommandPath()
    {
        var duplicates = AllNodes()
            .GroupBy(n => n.Path, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            "duplicate command paths: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryLeafHasAnAction()
    {
        // A leaf without an action parses and then silently does nothing.
        var inert = AllNodes()
            .Where(n => n.Command.Subcommands.Count == 0 && n.Command.Action is null)
            .Select(n => n.Path)
            .ToList();

        Assert.True(inert.Count == 0, "leaf commands with no action: " + string.Join(", ", inert));
    }

    [Fact]
    public void EveryGroupHasChildren()
    {
        var empty = AllNodes()
            .Where(n => n.Command.Action is null && n.Command.Subcommands.Count == 0)
            .Select(n => n.Path)
            .ToList();

        Assert.Empty(empty);
    }

    [Fact]
    public void EveryNodeRendersHelpWithoutThrowing()
    {
        var buffer = new StringWriter();

        foreach (var (path, command) in AllNodes())
        {
            var before = buffer.ToString().Length;
            CliHelp.Write(command, buffer);
            Assert.True(buffer.ToString().Length > before, $"{path} rendered no help");
        }
    }

    [Fact]
    public void RequiredOptionsAreReportedBeforeAnythingElseHappens()
    {
        // A missing required option must be a parse error, not an authentication attempt.
        var root = BuildRoot();

        var result = root.Parse("record get");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void HelpOnACommandWithMissingRequiredOptionsStillShowsHelp()
    {
        // `osdu record get --help` must show help, not complain about --id.
        var root = BuildRoot();

        var result = root.Parse("record get --help");

        Assert.True(CliHelp.WantsHelp(result));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("group")]
    [InlineData("legaltag")]
    [InlineData("schema")]
    [InlineData("status")]
    public void CoreNounsArePresent(string noun)
    {
        Assert.Contains(BuildRoot().Subcommands, c => c.Name == noun);
    }

    [Fact]
    public void NounsAreNamedForResourcesNotServices()
    {
        // The grammar decision: `osdu record get`, not `osdu storage get`. A service name
        // reappearing as a top-level noun means a manifest regressed.
        var roots = BuildRoot().Subcommands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var service in new[] { "storage", "entitlements", "search", "unit_v3", "legal" })
            Assert.DoesNotContain(service, roots);
    }
}
