using System.CommandLine;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Grouping of the root command list into named sections.
/// </summary>
/// <remarks>
/// A flat list stops being a list once it is long enough. Widening Wellbore DDMS to all nine
/// of its record types put nine of twenty-two entries on the front page for a service most
/// users did not come for, with `record`, `schema` and `search` sorted in among them.
/// </remarks>
public class CliHelpSectionTests
{
    private static string RenderRoot(params (string Name, string Section)[] commands)
    {
        var root = new RootCommand("osducs — command line for the OSDU platform.");
        GlobalOptions.AddTo(root);

        foreach (var (name, section) in commands)
        {
            root.Subcommands.Add(new Command(name, $"Help for {name}."));
            if (section is not null)
                CliHelp.Categorise(name, section);
        }

        var buffer = new StringWriter();
        CliHelp.Write(root, buffer);
        return buffer.ToString();
    }

    private static int IndexOf(string help, string needle)
    {
        var at = help.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"expected to find '{needle}' in:\n{help}");
        return at;
    }

    [Fact]
    public void TheDefaultHeadingNamesResourcesRatherThanCommands()
    {
        // The entries under it are nouns; the command is a noun plus a verb. "Commands" was
        // System.CommandLine's word for a subcommand, not this CLI's word for what is listed.
        Assert.Equal("Core resources", CliHelp.DefaultSection);
    }

    [Fact]
    public void UncategorisedCommandsStayInTheDefaultSection()
    {
        var help = RenderRoot(("zzrecord", null!));

        Assert.Contains($"{CliHelp.DefaultSection}:", help);
        Assert.DoesNotContain("Wellbore DDMS:", help);
    }

    [Fact]
    public void ACategorisedCommandGetsItsOwnHeading()
    {
        var help = RenderRoot(("zzwelllog", "Test DDMS"));

        Assert.Contains("Test DDMS:", help);
        Assert.True(IndexOf(help, "Test DDMS:") < IndexOf(help, "zzwelllog"));
    }

    [Fact]
    public void TheDefaultSectionComesBeforeNamedOnes()
    {
        // The point of the change: core nouns must not be pushed below a service section.
        var help = RenderRoot(("zzrecord", null!), ("zztrajectory", "Test DDMS"));

        Assert.True(IndexOf(help, "zzrecord") < IndexOf(help, "Test DDMS:"));
    }

    [Fact]
    public void TheToolSectionIsRenderedLast()
    {
        var help = RenderRoot(
            ("zzstatus", CliHelp.ToolSection),
            ("zzrecord", null!),
            ("zztrajectory", "Test DDMS"));

        Assert.True(IndexOf(help, "Test DDMS:") < IndexOf(help, $"{CliHelp.ToolSection}:"));
        Assert.True(IndexOf(help, "zzrecord") < IndexOf(help, $"{CliHelp.ToolSection}:"));
    }

    [Fact]
    public void NamedSectionsSortAlphabeticallySoTheOrderIsPredictable()
    {
        var help = RenderRoot(("zza", "Test Alpha"), ("zzb", "Test Beta"));

        Assert.True(IndexOf(help, "Test Alpha:") < IndexOf(help, "Test Beta:"));
    }

    [Fact]
    public void DescriptionsStillLineUpAcrossSections()
    {
        // One column width spans every section; a per-section width would stagger the page.
        var help = RenderRoot(("zzshort", null!), ("zzmuchlongername", "Test DDMS"));

        var shortLine = Array.Find(help.Split('\n'), l => l.Contains("zzshort"))!;
        var longLine = Array.Find(help.Split('\n'), l => l.Contains("zzmuchlongername"))!;

        Assert.Equal(shortLine.IndexOf("Help for", StringComparison.Ordinal),
                     longLine.IndexOf("Help for", StringComparison.Ordinal));
    }

    [Fact]
    public void ANounsOwnPageListsItsVerbsAsCommands()
    {
        // `osducs record --help` showed `list`, `get` and `search` under "Core resources",
        // because nested pages borrowed the root's heading and the root's heading was renamed.
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        var noun = new Command("zznoun", "A noun.");
        noun.Subcommands.Add(new Command("list", "List them."));
        root.Subcommands.Add(noun);

        var buffer = new StringWriter();
        CliHelp.Write(noun, buffer);
        var help = buffer.ToString();

        Assert.Contains($"{CliHelp.NestedSection}:", help);
        Assert.DoesNotContain($"{CliHelp.DefaultSection}:", help);
    }

    [Fact]
    public void ANestedCommandIsNotFiledUnderARootCommandsHeadingThatSharesItsName()
    {
        // Categories are keyed by name. A nested `zzshared` must not inherit the heading
        // given to a root command of the same name.
        CliHelp.Categorise("zzshared", CliHelp.ToolSection);
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        var noun = new Command("zzparent", "A noun.");
        noun.Subcommands.Add(new Command("zzshared", "Nested, not the tool command."));
        root.Subcommands.Add(noun);

        var buffer = new StringWriter();
        CliHelp.Write(noun, buffer);

        Assert.DoesNotContain($"{CliHelp.ToolSection}:", buffer.ToString());
    }
}
