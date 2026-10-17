using System.CommandLine;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// What a command needs, said in its help before it is run, rather than only after a refusal.
/// </summary>
public class CommandRolesTests
{
    private static RootCommand Root()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    private static string Help(string path)
    {
        Command command = Root();
        foreach (var name in path.Split(' '))
            command = command.Subcommands.Single(sub => sub.Name == name);
        var buffer = new StringWriter();
        CliHelp.Write(command, buffer);
        return buffer.ToString().ReplaceLineEndings("\n");
    }

    [Theory]
    // An operator command is marked as one.
    [InlineData("record purge", "service.storage.admin (an operator role)")]
    [InlineData("index reindex-partition", "users.datalake.ops (an operator role)")]
    // A data user's role among them is enough, so no mark.
    [InlineData("record get", "service.storage.viewer, service.storage.creator or service.storage.admin")]
    // Hand-written commands say theirs too.
    [InlineData("record add", "users.datalake.editors or users.datalake.admins")]
    [InlineData("welllog data add", "users.datalake.editors or users.datalake.admins")]
    [InlineData("group list", "with --all, users.datalake.ops or service.entitlements.admin (an operator role)")]
    public void HelpSaysWhatACommandRequires(string command, string roles)
    {
        var help = Help(command);

        Assert.Contains($"Requires:\n  {roles}\n", help);
        // Between what it does and how it is called, where it is read before running it.
        Assert.True(help.IndexOf("Description:", StringComparison.Ordinal) < help.IndexOf("Requires:", StringComparison.Ordinal));
        Assert.True(help.IndexOf("Requires:", StringComparison.Ordinal) < help.IndexOf("Usage:", StringComparison.Ordinal));
    }

    [Fact]
    public void ACommandWhoseSpecSaysNothingClaimsNothing() =>
        // Entitlements' spec documents no roles for GET /groups/{e}/members.
        Assert.DoesNotContain("Requires:", Help("group member list"));

    [Fact]
    public void ARoleIsSetForTheCommandItWasSetFor()
    {
        var first = new Command("first");
        var second = new Command("second");

        CommandRoles.Set(first, "users.datalake.ops", operatorRole: true);

        Assert.Equal("users.datalake.ops (an operator role)", CommandRoles.Describe(first));
        Assert.Null(CommandRoles.Describe(second));
    }
}
