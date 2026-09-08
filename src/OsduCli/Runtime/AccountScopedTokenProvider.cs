using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Facade.Auth;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Wraps the MSAL provider so that an ambiguous cache is reported rather than resolved by
/// guessing.
/// </summary>
/// <remarks>
/// One token cache can hold several accounts, and MSAL's own answer to "which one" is the
/// first it happens to return. For someone with a normal account and a separate privileged
/// one that is a coin toss, and a silent one: the command runs, the output looks ordinary,
/// and only the permissions differ. Whether you can see a record is not something to decide
/// by accident.
///
/// So when the caller has not said which account it means and more than one is cached, this
/// refuses and names them. `--user` (or `user` in the profile) settles it.
///
/// The check is deferred to the first token request rather than done when the context is
/// built, so that commands which never authenticate — `--help`, `completion` — do not pay
/// for it, and so that reading the cache can be async without blocking a constructor.
/// </remarks>
/// <param name="cachedUsernames">
/// Reads the cache. A delegate rather than the MSAL provider itself, so the rule this class
/// exists to enforce can be tested without a browser and a live tenant. It takes the
/// cancellation token because reading the cache waits on the provider's registration
/// semaphore, and a caller giving up should not be stuck behind it.
/// </param>
internal sealed class AccountScopedTokenProvider(
    ITokenProvider inner,
    Func<CancellationToken, Task<IReadOnlyList<string>>> cachedUsernames,
    string? username) : ITokenProvider
{
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (username is null)
        {
            var cached = await cachedUsernames(cancellationToken);
            if (cached.Count > 1)
            {
                throw new OsduException(
                    "More than one account is signed in, so which one to use is ambiguous:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, cached.Select(name => "  " + name))
                    + Environment.NewLine
                    + "Choose with --user <account>, or set `user` in your config profile "
                    + "to make it the default."
                    + Environment.NewLine
                    + "`osducs config show` reports which profile is in effect — a setting in "
                    + "a profile that is not selected has no effect.");
            }
        }

        return await inner.GetTokenAsync(cancellationToken);
    }
}
