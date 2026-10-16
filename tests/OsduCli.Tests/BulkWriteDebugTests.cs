using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>--debug</c> on a bulk write leaves bodies out: the client reads a body whole to log it,
/// and these are files of curves, gigabytes of them.
/// </summary>
[Collection(nameof(EnvironmentCollection))]
public class BulkWriteDebugTests : ConfigTestDirectories
{
    [Theory]
    [InlineData("welllog data add --id dev:x:1 --file {0}")]
    [InlineData("trajectory data session send --id dev:x:1 --session 4b2e1c3a-1111-2222-3333-444455556666 --file {0}")]
    public async Task AnUploadKeepsItsBodiesOutOfDebug(string line)
    {
        // A profile signing in as an application, so nothing opens a browser; the missing file
        // stops the command after its logging is set up and before anything is sent.
        var profile = WritePythonProfile("ops", "https://osdu.example.com",
            "authentication_mode = msal_non_interactive", "client_secret = not-used");
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        var missing = Path.Combine(Path.GetTempPath(), $"osducs-missing-{Guid.NewGuid():N}.parquet");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        int code;
        try
        {
            code = await root.Parse([.. string.Format(line, missing).Split(' '), "--debug", "-c", profile])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(1, code);
        Assert.Contains($"--debug leaves out request and response bodies here: {WellboreBulkWrite.FileBodies}.", error.ToString());
        Assert.Contains("File not found", error.ToString());
    }
}
