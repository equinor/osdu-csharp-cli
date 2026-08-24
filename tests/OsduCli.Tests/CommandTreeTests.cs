using System.CommandLine;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// The shared command tree is what lets a noun be fed by several services and a service
/// supply several nouns, so its assembly rules are load-bearing for every generated command.
/// </summary>
public class CommandTreeTests
{
    private static string[] Names(IEnumerable<Command> commands) =>
        commands.Select(c => c.Name).ToArray();

    [Fact]
    public void CreatesMissingAncestorsForANestedPath()
    {
        var tree = new CommandTree();

        tree.Node("record version").Subcommands.Add(new Command("get"));

        var record = Assert.Single(tree.Roots);
        Assert.Equal("record", record.Name);
        var version = Assert.Single(record.Subcommands);
        Assert.Equal("version", version.Name);
        Assert.Equal(["get"], Names(version.Subcommands));
    }

    [Fact]
    public void ReturnsTheSameNodeForARepeatedPath()
    {
        var tree = new CommandTree();

        Assert.Same(tree.Node("record"), tree.Node("record"));
    }

    [Fact]
    public void LetsTwoServicesContributeToOneNoun()
    {
        // `record list` comes from Storage and `record search` from Search. Neither service
        // owns the `record` group.
        var tree = new CommandTree();
        tree.Describe("record", "Work with records.");

        tree.Node("record").Subcommands.Add(new Command("list"));
        tree.Node("record").Subcommands.Add(new Command("search"));

        var record = Assert.Single(tree.Roots);
        Assert.Equal("Work with records.", record.Description);
        Assert.Equal(["list", "search"], Names(record.Subcommands));
    }

    [Fact]
    public void LetsOneServiceContributeSeveralNouns()
    {
        // Entitlements supplies both `group` and `member`.
        var tree = new CommandTree();
        tree.Node("group").Subcommands.Add(new Command("list"));
        tree.Node("member").Subcommands.Add(new Command("add"));

        Assert.Equal(["group", "member"], Names(tree.Roots));
    }

    [Fact]
    public void AppliesTheDescriptionRecordedBeforeTheNodeExists()
    {
        var tree = new CommandTree();
        tree.Describe("crs", "Work with coordinate reference systems.");

        Assert.Equal("Work with coordinate reference systems.", tree.Node("crs").Description);
    }

    [Fact]
    public void OrdersVerbsByMeaningRatherThanAlphabetically()
    {
        // Manifests are read in file order, so "whichever sorted first" is not an order a
        // user can learn. Reading comes before changing, changing before destroying.
        var tree = new CommandTree();
        foreach (var verb in new[] { "delete", "update", "get", "list" })
            tree.Node("record").Subcommands.Add(new Command(verb));

        var record = Assert.Single(tree.Roots);
        Assert.Equal(["list", "get", "update", "delete"], Names(record.Subcommands));
    }

    [Fact]
    public void PutsDestructiveVerbsLast()
    {
        var tree = new CommandTree();
        foreach (var verb in new[] { "purge", "add", "delete", "get" })
            tree.Node("record").Subcommands.Add(new Command(verb));

        var names = Names(Assert.Single(tree.Roots).Subcommands);
        Assert.Equal("get", names[0]);
        Assert.Equal(["delete", "purge"], names[^2..]);
    }

    [Fact]
    public void SortsUnknownVerbsAlphabeticallyAfterKnownOnes()
    {
        var tree = new CommandTree();
        foreach (var verb in new[] { "zebra", "apple", "get" })
            tree.Node("record").Subcommands.Add(new Command(verb));

        Assert.Equal(["get", "apple", "zebra"],
            Names(Assert.Single(tree.Roots).Subcommands));
    }

    [Fact]
    public void PutsGroupsAfterLeaves()
    {
        // `record version` is a group; it belongs below the verbs, not among them.
        var tree = new CommandTree();
        tree.Node("record version").Subcommands.Add(new Command("get"));
        tree.Node("record").Subcommands.Add(new Command("list"));

        Assert.Equal(["list", "version"],
            Names(Assert.Single(tree.Roots).Subcommands));
    }

    [Fact]
    public void OrdersRootsByName()
    {
        var tree = new CommandTree();
        foreach (var noun in new[] { "workflow", "crs", "record" })
            tree.Node(noun).Subcommands.Add(new Command("list"));

        Assert.Equal(["crs", "record", "workflow"], Names(tree.Roots));
    }
}
