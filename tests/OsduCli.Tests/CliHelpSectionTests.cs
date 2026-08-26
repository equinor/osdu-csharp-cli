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
    public void UncategorisedCommandsStayInTheDefaultSection()
    {
        var help = RenderRoot(("zzrecord", null!));

        Assert.Contains("Commands:", help);
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
            ("zzstatus", "CLI"),
            ("zzrecord", null!),
            ("zztrajectory", "Test DDMS"));

        Assert.True(IndexOf(help, "Test DDMS:") < IndexOf(help, "CLI:"));
        Assert.True(IndexOf(help, "zzrecord") < IndexOf(help, "CLI:"));
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
}
