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
        var root = new RootCommand("osducs — command line for the OSDU platform.");
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
        // `osducs record get --help` must show help, not complain about --id.
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
        // The grammar decision: `osducs record get`, not `osducs storage get`. A service name
        // reappearing as a top-level noun means a manifest regressed.
        var roots = BuildRoot().Subcommands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var service in new[] { "storage", "entitlements", "search", "unit_v3", "legal" })
            Assert.DoesNotContain(service, roots);
    }

    [Fact]
    public void RequireOneOfIsCheckedAtParseTime()
    {
        // `crs get` takes --record-id or --data-id, each optional in the spec because each
        // is individually optional. Supplying neither is rejected by the service; the CLI
        // should say so first, without loading config or acquiring a token.
        var result = BuildRoot().Parse("crs get");

        var error = Assert.Single(result.Errors);
        Assert.Contains("--record-id", error.Message);
        Assert.Contains("--data-id", error.Message);
    }

    [Theory]
    [InlineData("crs get --record-id x")]
    [InlineData("crs get --data-id x")]
    [InlineData("crs transform --record-id x")]
    [InlineData("crs transform --data-id x")]
    public void SupplyingEitherSatisfiesRequireOneOf(string commandLine)
    {
        Assert.Empty(BuildRoot().Parse(commandLine).Errors);
    }

    [Fact]
    public void SupplyingBothIsAllowed()
    {
        // The rule is "at least one", not "exactly one" — the service accepts both and
        // prefers one, and inventing a stricter rule here would reject valid input.
        Assert.Empty(BuildRoot().Parse("crs get --record-id x --data-id y").Errors);
    }

    [Fact]
    public void CommandsWithoutTheRuleAreUnaffected()
    {
        // Paging params are genuinely optional; none of these should have gained a rule.
        Assert.Empty(BuildRoot().Parse("schema list").Errors);
        Assert.Empty(BuildRoot().Parse("unit list").Errors);
    }

    [Fact]
    public void AggregateIsASeparateCommandFromSearch()
    {
        // Both are POST /query, but they answer different questions and return different
        // shapes. Setting aggregateBy on `search` would show the records and discard the
        // counts the user asked for.
        var root = BuildRoot();

        Assert.Empty(root.Parse("record aggregate --kind k --by kind").Errors);
        Assert.Empty(root.Parse("record search --kind k").Errors);
    }

    [Fact]
    public void AggregateRequiresBothKindAndField()
    {
        Assert.NotEmpty(BuildRoot().Parse("record aggregate --kind k").Errors);
        Assert.NotEmpty(BuildRoot().Parse("record aggregate --by kind").Errors);
    }

    [Fact]
    public void ReadVerbsSortAheadOfDestructiveOnes()
    {
        // A group's verbs arrive in manifest order; the tree reorders so that reading comes
        // before changing and destroying sits at the bottom, where it is hard to hit by
        // accident. `headers` was landing after `delete` because it was not in the order.
        var record = Assert.Single(BuildRoot().Subcommands, c => c.Name == "record");
        var names = record.Subcommands.Select(c => c.Name).ToList();

        Assert.True(names.IndexOf("headers") < names.IndexOf("delete"),
            "a read verb must not sort below a destructive one: " + string.Join(", ", names));
        Assert.True(names.IndexOf("aggregate") < names.IndexOf("delete"));
    }

    [Fact]
    public void SearchAcceptsProjectedFields()
    {
        var root = BuildRoot();

        Assert.Empty(root.Parse("record search --kind k -f id -f data.FacilityName").Errors);
        // Still optional — the fixed columns remain the default.
        Assert.Empty(root.Parse("record search --kind k").Errors);
    }

    [Fact]
    public void SearchAcceptsSortAndTotalCount()
    {
        var root = BuildRoot();

        Assert.Empty(root.Parse(
            "record search --kind k --sort-by id --sort-order DESC --track-total-count").Errors);
        // --track-total-count is a flag, not a value.
        Assert.Empty(root.Parse("record search --kind k --track-total-count").Errors);
    }

    [Fact]
    public void SortOrderIsConstrainedToTheSpecsEnum()
    {
        // The enum lives on SortQuery.order.items, two levels below the request body.
        Assert.NotEmpty(BuildRoot().Parse("record search --kind k --sort-order SIDEWAYS").Errors);
    }

    [Fact]
    public void ContradictoryFieldOptionsAreRejectedAtParseTime()
    {
        // "only these" and "everything but these" cannot both hold, and the service does not
        // document which it believes. Better to say so before sending it.
        var result = BuildRoot().Parse("record search --kind k -f id -x data.GeoContexts");

        var error = Assert.Single(result.Errors);
        Assert.Contains("--returned-fields", error.Message);
        Assert.Contains("--excluded-fields", error.Message);
    }

    [Theory]
    [InlineData("record search --kind k -f id")]
    [InlineData("record search --kind k -x data.GeoContexts")]
    [InlineData("record search --kind k")]
    public void EitherFieldOptionAloneIsFine(string commandLine)
    {
        Assert.Empty(BuildRoot().Parse(commandLine).Errors);
    }
}
