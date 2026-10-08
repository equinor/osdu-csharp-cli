using System.CommandLine;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// List options the manifest marks <c>comma-separated</c>, parsed through the real generated tree.
/// </summary>
/// <remarks>
/// <c>record search -f data.Source,data.Equinor.WellboreName</c> was sent as
/// <c>"returnedFields":["data.Source,data.Equinor.WellboreName"]</c>, one field with a comma in
/// its name, and the service answered with empty records — although the option's help said
/// values could be comma-separated. <c>--excluded-fields</c> had the same problem.
/// </remarks>
public class CommaSeparatedFieldTests
{
    private static string[] Parsed(string flag, params string[] args)
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);

        var result = root.Parse(["record", "search", "--kind", "osdu:wks:*:*", .. args]);

        Assert.Empty(result.Errors);
        var values = result.GetValue((Option<string[]>)result.CommandResult.Command.Options.Single(o => o.Name == flag));
        Assert.NotNull(values);
        return values;
    }

    [Theory]
    [InlineData("--returned-fields", "-f")]
    [InlineData("--excluded-fields", "-x")]
    public void CommasSeparateValues(string flag, string alias)
    {
        Assert.Equal(["data.Source", "data.Equinor.WellboreName"],
            Parsed(flag, alias, "data.Source,data.Equinor.WellboreName"));
    }

    [Fact]
    public void CommasAndRepeatedFlagsCombine()
    {
        Assert.Equal(["id", "data.Source", "data.Equinor.WellboreName"],
            Parsed("--returned-fields", "-f", "id,data.Source", "-f", "data.Equinor.WellboreName"));
    }

    [Fact]
    public void SpacesAroundCommasAndEmptyPiecesAreDropped()
    {
        // A quoted list typed with spaces, and a trailing comma, are not fields named " x" or "".
        Assert.Equal(["id", "data.Source"], Parsed("--returned-fields", "-f", "id, data.Source,"));
    }

    [Fact]
    public void SeveralValuesAfterOneFlagStillWork()
    {
        Assert.Equal(["id", "data.Source"], Parsed("--returned-fields", "-f", "id", "data.Source"));
    }
}
