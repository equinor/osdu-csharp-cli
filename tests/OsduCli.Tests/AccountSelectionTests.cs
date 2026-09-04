using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Facade.Auth;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Covers the refusal to guess which account to authenticate as.
/// </summary>
/// <remarks>
/// One token cache can hold several accounts, and MSAL's answer to "which one" is whichever
/// it returns first. For someone with a normal account and a separate privileged one that is
/// a coin toss, and a silent one — the command runs, the output looks ordinary, only the
/// permissions differ.
/// </remarks>
public class AccountSelectionTests
{
    private sealed class StubTokenProvider : ITokenProvider
    {
        public bool WasCalled { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult("token");
        }
    }

    private static AccountScopedTokenProvider Provider(
        StubTokenProvider inner, string? username, params string[] cached) =>
        new(inner, _ => Task.FromResult<IReadOnlyList<string>>(cached), username);

    [Fact]
    public async Task TwoAccountsAndNoChoiceIsRefused()
    {
        var inner = new StubTokenProvider();
        var provider = Provider(inner, null, "normal@equinor.com", "azure@equinor.com");

        var error = await Assert.ThrowsAsync<OsduException>(() => provider.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.Contains("normal@equinor.com", error.Message);
        Assert.Contains("azure@equinor.com", error.Message);
        Assert.False(inner.WasCalled);
    }

    [Fact]
    public async Task TheErrorSaysHowToResolveIt()
    {
        // An error that only reports a problem makes the user go looking for the fix.
        var provider = Provider(new StubTokenProvider(), null, "a@equinor.com", "b@equinor.com");

        var error = await Assert.ThrowsAsync<OsduException>(() => provider.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.Contains("--user", error.Message);
        Assert.Contains("username", error.Message);
    }

    [Fact]
    public async Task NamingAnAccountSkipsTheCheckEntirely()
    {
        // With a choice made there is no ambiguity to report, whatever else is cached.
        var inner = new StubTokenProvider();
        var provider = Provider(inner, "azure@equinor.com", "normal@equinor.com", "azure@equinor.com");

        Assert.Equal("token", await provider.GetTokenAsync(TestContext.Current.CancellationToken));
        Assert.True(inner.WasCalled);
    }

    [Fact]
    public async Task OneAccountNeedsNoChoice()
    {
        var inner = new StubTokenProvider();
        var provider = Provider(inner, null, "normal@equinor.com");

        Assert.Equal("token", await provider.GetTokenAsync(TestContext.Current.CancellationToken));
        Assert.True(inner.WasCalled);
    }

    [Fact]
    public async Task AnEmptyCacheProceedsToSignIn()
    {
        // Nothing cached is not ambiguous — it is a first run, and the browser should open.
        var inner = new StubTokenProvider();
        var provider = Provider(inner, null);

        Assert.Equal("token", await provider.GetTokenAsync(TestContext.Current.CancellationToken));
        Assert.True(inner.WasCalled);
    }
}
