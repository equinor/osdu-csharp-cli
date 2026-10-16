using System.CommandLine;
using Equinor.OsduCli.Runtime;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace OsduCli.Tests;

/// <summary>What a refused request says about the role it needed.</summary>
[Collection(nameof(EnvironmentCollection))]
public class AccessRefusalTests : ConfigTestDirectories
{
    private async Task<string> Refused(int status, string message)
    {
        var profile = WritePythonProfile("ops", "https://osdu.example.com",
            "authentication_mode = msal_non_interactive", "client_secret = not-used");
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            await CliRunner.RunAsync(root.Parse(["-c", profile]),
                (_, _) => throw new ApiException(message) { ResponseStatusCode = status },
                TestContext.Current.CancellationToken, "service.search.admin");
        }
        finally
        {
            Console.SetError(original);
        }
        return error.ToString();
    }

    [Theory]
    [InlineData(403)]
    // ADME refuses a missing entitlement with 401 as well, in the same words; dev did for
    // `index reindex-records`, and the role went unsaid.
    [InlineData(401)]
    public async Task ARefusedEntitlementNamesTheRole(int status)
    {
        var said = await Refused(status, "The user is not authorized to perform this action");

        Assert.Contains("this endpoint requires service.search.admin", said);
    }

    [Fact]
    public void AnUnreachableServiceIsDescribedByWhatStoppedIt()
    {
        // As dev answered once: three exceptions deep, and only the last says what happened.
        var exception = new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new IOException("Unable to read data from the transport connection: Operation timed out.",
                new System.Net.Sockets.SocketException(60)));

        Assert.StartsWith("The SSL connection could not be established, see inner exception: ", CliRunner.Reason(exception));
        Assert.EndsWith(new System.Net.Sockets.SocketException(60).Message, CliRunner.Reason(exception));
    }

    [Fact]
    public async Task AnUnreachableServiceIsOneLine()
    {
        var said = await Run(new HttpRequestException("No such host is known."));

        Assert.Equal("error: could not reach the service. No such host is known." + Environment.NewLine, said);
    }

    [Fact]
    public async Task ATimeoutIsNotACancellation()
    {
        var said = await Run(new TaskCanceledException("The request was canceled.", new TimeoutException()));

        Assert.Equal("error: the service did not answer in time." + Environment.NewLine, said);
    }

    private async Task<string> Run(Exception failure)
    {
        var profile = WritePythonProfile("ops", "https://osdu.example.com",
            "authentication_mode = msal_non_interactive", "client_secret = not-used");
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            await CliRunner.RunAsync(root.Parse(["-c", profile]), (_, _) => throw failure,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(original);
        }
        return error.ToString();
    }

    [Fact]
    public async Task ARejectedTokenDoesNot()
    {
        var said = await Refused(401, "401 - Unauthorized: Access is denied due to invalid credentials.");

        Assert.DoesNotContain("this endpoint requires", said);
    }
}
