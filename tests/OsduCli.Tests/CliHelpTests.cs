using System.CommandLine;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Help is treated as a requirement carried over from the Python CLI: `-h` must be
/// self-explanatory at every level. The renderer is hand-written because HelpBuilder and
/// friends are internal in System.CommandLine, so its behaviour is only guaranteed by these.
/// </summary>
public class CliHelpTests
{
    /// <summary>Builds a root with the global options, so "Common Options" has content.</summary>
    private static (RootCommand Root, Command Leaf) Tree(Command leaf)
    {
        var root = new RootCommand("osducs — command line for the OSDU platform.");
        GlobalOptions.AddTo(root);
        root.Subcommands.Add(leaf);
        return (root, leaf);
    }

    private static string Render(Command leaf)
    {
        Tree(leaf);
        var buffer = new StringWriter();
        CliHelp.Write(leaf, buffer);
        return buffer.ToString();
    }

    [Fact]
    public void ListsEveryAliasOfAnOption()
    {
        // The default formatter prints one form per option, which hid `--id` even though it
        // parses and even though parse errors name it.
        var command = new Command("get", "Get a record.");
        command.Options.Add(new Option<string>("--id", "-id") { Description = "Record id." });

        var help = Render(command);

        Assert.Contains("--id", help);
        Assert.Contains("-id", help);
    }

    [Fact]
    public void MarksRequiredOptions()
    {
        var command = new Command("get", "Get a record.");
        command.Options.Add(new Option<string>("--id") { Description = "Record id.", Required = true });
        command.Options.Add(new Option<string>("--attributes") { Description = "Fields." });

        var help = Render(command);
        var idLine = help.Split(Environment.NewLine).Single(l => l.Contains("--id"));
        var attrLine = help.Split(Environment.NewLine).Single(l => l.Contains("--attributes"));

        Assert.Contains("(REQUIRED)", idLine);
        Assert.DoesNotContain("(REQUIRED)", attrLine);
    }

    [Fact]
    public void SeparatesCommandOptionsFromGlobalOnes()
    {
        // `--kind` must not be buried among `--config` and `--debug`.
        var command = new Command("list", "List records.");
        command.Options.Add(new Option<string>("--kind") { Description = "Kind." });

        var help = Render(command);

        var optionsAt = help.IndexOf("Options:", StringComparison.Ordinal);
        var commonAt = help.IndexOf("Common Options:", StringComparison.Ordinal);
        var kindAt = help.IndexOf("--kind", StringComparison.Ordinal);
        var configAt = help.IndexOf("--config", StringComparison.Ordinal);

        Assert.True(commonAt > optionsAt, "Common Options must follow the command's own");
        Assert.True(kindAt < commonAt, "a command option belongs above Common Options");
        Assert.True(configAt > commonAt, "a global option belongs below the heading");
    }

    [Fact]
    public void RendersPositionalArgumentsInTheirOwnSection()
    {
        // The renderer previously ignored arguments entirely.
        var command = new Command("get", "Get a record.");
        command.Arguments.Add(new Argument<string>("id") { Description = "Record id." });

        var help = Render(command);

        Assert.Contains("Arguments:", help);
        Assert.Contains("Record id.", help);
    }

    [Fact]
    public void UsageBracketsOptionalArgumentsButNotRequiredOnes()
    {
        var required = new Command("get", "Get.");
        required.Arguments.Add(new Argument<string>("id") { Description = "Record id." });

        var optional = new Command("list", "List.");
        optional.Arguments.Add(new Argument<string>("filter")
        {
            Description = "Filter.",
            DefaultValueFactory = _ => string.Empty,
        });

        Assert.Contains("<id>", Render(required));
        Assert.Contains("[<filter>]", Render(optional));
    }

    [Fact]
    public void UsageNamesTheFullCommandPath()
    {
        var group = new Command("record", "Records.");
        var leaf = new Command("get", "Get a record.");
        group.Subcommands.Add(leaf);
        var root = new RootCommand("osdu");
        GlobalOptions.AddTo(root);
        root.Subcommands.Add(group);

        var buffer = new StringWriter();
        CliHelp.Write(leaf, buffer);

        // The root's name comes from the assembly — `osdu` when shipped, the test host
        // here — so assert on the path below it rather than the whole line.
        var usage = buffer.ToString().Split(Environment.NewLine)
            .Single(line => line.Contains("record get", StringComparison.Ordinal));
        Assert.Contains("record get", usage);
        Assert.DoesNotContain("record get get", usage);
    }

    [Fact]
    public void ListsSubcommandsOfAGroup()
    {
        var group = new Command("record", "Work with records.");
        group.Subcommands.Add(new Command("list", "List records."));
        group.Subcommands.Add(new Command("get", "Get a record."));

        var help = Render(group);

        Assert.Contains("Commands:", help);
        Assert.Contains("List records.", help);
        Assert.Contains("Get a record.", help);
    }

    [Fact]
    public void WantsHelpDetectsEveryHelpToken()
    {
        var root = new RootCommand("osdu");
        GlobalOptions.AddTo(root);
        root.Subcommands.Add(new Command("get", "Get."));

        Assert.True(CliHelp.WantsHelp(root.Parse("get --help")));
        Assert.True(CliHelp.WantsHelp(root.Parse("get -h")));
        Assert.False(CliHelp.WantsHelp(root.Parse("get")));
    }
}
