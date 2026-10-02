namespace Equinor.OsduCli.Runtime;

/// <summary>How a profile signs in.</summary>
internal enum SignInMethod
{
    /// <summary>As a person, through a browser, with the token cached between commands.</summary>
    Interactive,

    /// <summary>As the application itself, with a client secret and no person involved.</summary>
    ClientCredentials,

    /// <summary>
    /// Through Azure's own identity sources: an <c>az login</c> session, a managed or workload
    /// identity, or <c>AZURE_*</c> client-secret variables.
    /// </summary>
    Azure,

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
/// one per cloud provider other than Azure — which osducs reports as unsupported rather than
/// quietly replacing with a browser sign-in. That replacement is what it did before it read the mode at all: a
/// tester's <c>msal_non_interactive</c> profiles opened a browser for an app registration with
/// no redirect URI, and when an account was given, sent a person's token to an environment
/// that only accepts the application's.
/// </remarks>
internal static class AuthenticationModes
{
    internal const string Interactive = "msal_interactive";
    internal const string ClientCredentials = "msal_non_interactive";
    internal const string Azure = "azure";

    /// <summary>The modes osducs can sign in with, for messages and completion.</summary>
    internal static readonly string[] Supported = [Interactive, ClientCredentials, Azure];

    /// <summary>
    /// The method <paramref name="mode"/> names. No mode at all is interactive, which is what
    /// every profile meant before osducs read this setting.
    /// </summary>
    internal static SignInMethod Parse(string? mode) => mode?.Trim() switch
    {
        null or "" => SignInMethod.Interactive,
        var value when value.Equals(Interactive, StringComparison.OrdinalIgnoreCase) => SignInMethod.Interactive,
        var value when value.Equals(ClientCredentials, StringComparison.OrdinalIgnoreCase) => SignInMethod.ClientCredentials,
        var value when value.Equals(Azure, StringComparison.OrdinalIgnoreCase) => SignInMethod.Azure,
        _ => SignInMethod.Unsupported,
    };

    /// <summary>The usual spelling of a supported method.</summary>
    internal static string Name(SignInMethod method) => method switch
    {
        SignInMethod.ClientCredentials => ClientCredentials,
        SignInMethod.Azure => Azure,
        _ => Interactive,
    };

    /// <summary>Explains a mode osducs cannot sign in with.</summary>
    internal static string UnsupportedMessage(string mode) =>
        $"Authentication mode '{mode}' is not supported. osducs signs in with {Interactive}, through "
        + $"a browser, {ClientCredentials}, as an application with a client secret, or {Azure}, "
        + "through `az login` or an identity Azure provides. Make a profile that uses one of them "
        + "with `osducs config add <name> --from <profile> --authentication-mode <mode>`.";

    /// <summary>
    /// OpenID Connect scopes, which ask about the person signing in and so mean nothing to a
    /// sign-in that asks for one resource.
    /// </summary>
    private static readonly HashSet<string> PersonScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openid", "profile", "email", "offline_access",
    };

    /// <summary>
    /// The one scope an application or Azure sign-in asks for, and the person scopes removed to
    /// get it; or null when <paramref name="scopes"/> does not name exactly one resource's
    /// <c>/.default</c>.
    /// </summary>
    /// <remarks>
    /// Entra ID takes exactly one scope for a client-credentials sign-in, the resource's own
    /// <c>/.default</c>, and the Azure CLI asks for tokens the same way. Browser profiles
    /// commonly add <c>openid</c>, which is refused there, so a browser profile turned into
    /// either kind failed at its first request. Person scopes are removed; anything else that
    /// is not a single <c>/.default</c> is left for the caller to refuse, since which resource
    /// was meant is not something to guess.
    /// </remarks>
    internal static (string Scope, IReadOnlyList<string> Dropped)? ResourceScope(string scopes)
    {
        var all = scopes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var resources = all.Where(scope => !PersonScopes.Contains(scope)).ToList();
        return resources is [var resource] && resource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
            ? (resource, all.Where(PersonScopes.Contains).ToList())
            : null;
    }
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
