using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Enum values are accepted in any casing and normalised to the spec's own spelling.
/// </summary>
/// <remarks>
/// The specs disagree about casing — Entitlements and Search use MEMBER and ASC, Storage and
/// Workflow use version and running — and the command line gives no clue which applies, so a
/// tester typing <c>--role member</c> was refused for a reason that was ours. What goes on the
/// wire still has to be the spec's spelling, which is the half worth a test: accepting the
/// input is no use if the service then rejects it.
/// </remarks>
public class EnumOptionCasingTests
{
    private static RootCommand BuildRoot()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    private static ParseResult Parse(params string[] args) => BuildRoot().Parse(args);

    [Theory]
    [InlineData("member", "MEMBER")]
    [InlineData("MEMBER", "MEMBER")]
    [InlineData("Owner", "OWNER")]
    public void An_upper_case_enum_accepts_any_casing(string typed, string sent)
    {
        var result = Parse("group", "member", "add", "-g", "g@example.com",
                           "-m", "m@example.com", "--role", typed);

        Assert.Empty(result.Errors);
        Assert.Equal(sent, result.GetValue<string>("--role"));
    }

    [Theory]
    [InlineData("VERSION", "version")]
    [InlineData("Kind", "kind")]
    public void A_lower_case_enum_accepts_any_casing(string typed, string sent)
    {
        var result = Parse("record", "headers", "--id", "dev:x:1", "-a", typed);

        Assert.Empty(result.Errors);
        Assert.Equal([sent], result.GetValue<string[]>("--attributes")!);
    }

    [Fact]
    public void A_value_wrong_in_more_than_casing_is_still_rejected()
    {
        var result = Parse("group", "member", "add", "-g", "g@example.com",
                           "-m", "m@example.com", "--role", "admin");

        var error = Assert.Single(result.Errors);
        Assert.Contains("'MEMBER'", error.Message);
        Assert.Contains("'OWNER'", error.Message);
        // Says the casing is not what failed, so nobody retries the same word in capitals.
        Assert.Contains("any casing", error.Message);
    }

    [Fact]
    public void The_allowed_values_are_still_offered_as_completions()
    {
        // AcceptOnlyFromAmong supplied these as well as validating, so replacing it had to
        // put them back: `osducs completion` reads them off the option.
        var completions = CompletionCommand.Candidates(
            BuildRoot(), ["group", "member", "add", "--role", ""]).ToArray();

        Assert.Contains("MEMBER", completions);
        Assert.Contains("OWNER", completions);
    }
}
