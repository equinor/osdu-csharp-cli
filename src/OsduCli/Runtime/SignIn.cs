namespace Equinor.OsduCli.Runtime;

/// <summary>How a profile signs in.</summary>
internal enum SignInMethod
{
    /// <summary>As a person, through a browser, with the token cached between commands.</summary>
    Interactive,

    /// <summary>As the application itself, with a client secret and no person involved.</summary>
    ClientCredentials,

    /// <summary>A mode the Python CLI has and osducs does not.</summary>
    Unsupported,
}

/// <summary>
/// The values of a profile's authentication mode, spelled as the Python CLI spells its
/// <c>authentication_mode</c>.
/// </summary>
/// <remarks>
/// The same words in both tools, so a profile reads the same in either and a value copied from
/// one is understood by the other. The Python CLI has more modes — <c>refresh_token</c>, and
/// one per cloud provider — which osducs reports as unsupported rather than quietly replacing
/// with a browser sign-in. That replacement is what it did before it read the mode at all: a
/// tester's <c>msal_non_interactive</c> profiles opened a browser for an app registration with
/// no redirect URI, and when an account was given, sent a person's token to an environment
/// that only accepts the application's.
/// </remarks>
internal static class AuthenticationModes
{
    internal const string Interactive = "msal_interactive";
    internal const string ClientCredentials = "msal_non_interactive";

    /// <summary>The modes osducs can sign in with, for messages and completion.</summary>
    internal static readonly string[] Supported = [Interactive, ClientCredentials];

    /// <summary>
    /// The method <paramref name="mode"/> names. No mode at all is interactive, which is what
    /// every profile meant before osducs read this setting.
    /// </summary>
    internal static SignInMethod Parse(string? mode) => mode?.Trim() switch
    {
        null or "" => SignInMethod.Interactive,
        var value when value.Equals(Interactive, StringComparison.OrdinalIgnoreCase) => SignInMethod.Interactive,
        var value when value.Equals(ClientCredentials, StringComparison.OrdinalIgnoreCase) => SignInMethod.ClientCredentials,
        _ => SignInMethod.Unsupported,
    };

    /// <summary>Explains a mode osducs cannot sign in with.</summary>
    internal static string UnsupportedMessage(string mode) =>
        $"Authentication mode '{mode}' is not supported. osducs signs in with {Interactive}, through "
        + $"a browser, or {ClientCredentials}, as an application with a client secret. Make a "
        + $"profile that uses one of them with `osducs config add <name> --from <profile> "
        + "--authentication-mode <mode>`.";
}

/// <summary>A client secret, kept out of anything that prints it.</summary>
/// <remarks>
/// A record prints every property in its <c>ToString</c>, so a secret held as a plain string
/// in <see cref="ProfileSettings"/> would turn up in an assertion failure, a debugger or a log
/// line the first time anything printed the settings around it.
/// </remarks>
internal sealed record ClientSecret(string Value)
{
    public override string ToString() => "(hidden)";
}

/// <summary>How the configuration in effect signs in, and as whom.</summary>
/// <param name="Method">The method <paramref name="Mode"/> names.</param>
/// <param name="Mode">The mode as configured, or <c>msal_interactive</c> when none is.</param>
/// <param name="User">The default account, from the profile or the environment.</param>
/// <param name="Secret">The client secret, from the profile in effect or the environment.</param>
internal sealed record SignInSettings(
    SignInMethod Method, string Mode, string? User, ClientSecret? Secret);
