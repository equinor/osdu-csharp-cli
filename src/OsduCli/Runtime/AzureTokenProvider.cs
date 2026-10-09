using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Facade.Auth;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Signs in through Azure's own identity sources, as the Python CLI's <c>azure</c> mode does:
/// <c>AZURE_*</c> client-secret variables, a workload identity, a managed identity, or an
/// <c>az login</c> session, tried in that order.
/// </summary>
/// <remarks>
/// <para>Nothing about who signs in is kept in the profile: no secret, and no app registration
/// that needs a redirect URI. A person signs in once with <c>az login</c>; a pipeline or an
/// Azure-hosted job uses the identity it already has.</para>
///
/// <para>The order is <see cref="DefaultAzureCredential"/>'s with its developer-tool sources
/// left out: Visual Studio, VS Code, Azure PowerShell, the Azure Developer CLI and the
/// operating system's sign-in broker. Each of those may hold some account, and picking up whichever one did
/// is the kind of guess about identity osducs refuses to make elsewhere. The Azure CLI stays,
/// since it is the one a person signs in to on purpose. <c>AZURE_TOKEN_CREDENTIALS</c>, read by
/// the Azure library itself, narrows the order further, for instance to
/// <c>AzureCliCredential</c> alone; see <see cref="CheckSelection"/>.</para>
///
/// <para>The Python CLI read its resource only from environment variables. Here it is the
/// profile's scope, so a profile names its environment completely; <c>AZURE_RESOURCE_ID</c>
/// is the fallback for a profile that has none, as a Python <c>azure</c> profile does not.</para>
/// </remarks>
internal sealed partial class AzureTokenProvider(
    TokenCredential credential, string scope, string? tenant = null, TimeProvider? clock = null,
    AzureTokenProvider.ExpectedAccount? expected = null, string? selection = null)
    : ITokenProvider
{
    /// <summary>The account a command expects to run as, and whether <c>--user</c> said so.</summary>
    /// <remarks>
    /// Azure, not osducs, decides who signs in: the Azure CLI has one active account for every
    /// tool that uses it, so <c>az login</c> as an admin account for one task makes every
    /// <c>azure</c> profile act as admin. A browser profile's <c>user</c> prevents exactly
    /// that, by choosing the account; here it can only be checked, and a command signed in as
    /// anyone else is refused before it sends anything.
    /// </remarks>
    internal sealed record ExpectedAccount(string User, bool FromFlag);

    /// <summary>The account expected, or null when any will do.</summary>
    internal ExpectedAccount? Expected => expected;

    /// <summary>How long before it expires a token is replaced, so none expires in flight.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken? _token;

    /// <summary>The scope tokens are asked for.</summary>
    internal string Scope => scope;

    internal static AzureTokenProvider Create(OsduConfig config, ExpectedAccount? expected = null)
    {
        var selection = Environment.GetEnvironmentVariable(TokenCredentialsVariable);
        CheckSelection(selection);
        var tenant = TenantOf(config.Authority);
        return new(new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = tenant,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeBrokerCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        }), ScopeFor(config), tenant, expected: expected, selection: selection);
    }

    /// <remarks>
    /// Kept for the life of the command. The Azure CLI source runs <c>az</c> for every token it
    /// is asked for, and the client asks once per request, so a search paging through results
    /// started a process per page.
    /// </remarks>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await FetchAsync(cancellationToken);
        if (expected is not null && !IsExpected(Identify(token)))
            throw new SignInException(Mismatch(Identify(token)));
        return token;
    }

    /// <summary>Who Azure signs in as, without the check, for reporting.</summary>
    internal async Task<AzureIdentity> IdentifyAsync(CancellationToken cancellationToken = default) =>
        Identify(await FetchAsync(cancellationToken));

    /// <summary>Whether <paramref name="who"/> is the account expected, if one is.</summary>
    /// <remarks>
    /// Compared without regard to case, as Entra ID compares sign-in names. An application's
    /// token names no person, so it never matches a person expected.
    /// </remarks>
    internal bool IsExpected(AzureIdentity who) =>
        expected is null || string.Equals(who.Person, expected.User, StringComparison.OrdinalIgnoreCase);

    /// <summary>Explains signing in as someone other than the account expected.</summary>
    internal string Mismatch(AzureIdentity who)
    {
        var account = expected!.User;
        return (expected.FromFlag ? $"--user asks for {account}" : $"This profile is for {account}")
               + $", but Azure signed in as {who.Description}. "
               + (who.IssuedToAzureCli
                   ? $"Sign in as {account} with `az login`"
                   : $"Sign in as {account} with `az login` if the Azure CLI is the one answering")
               + (expected.FromFlag ? ", or leave out --user." : ", or remove `user` from the profile.");
    }

    /// <summary>The token, from the command's cache or freshly fetched.</summary>
    private async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        if (Current() is { } current)
            return current;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Current() is { } refreshed)
                return refreshed;
            _token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            return _token.Value.Token;
        }
        // Both escaped as stack traces that buried the one line that mattered, usually the
        // Azure CLI's "Please run 'az login'", among the other sources' reasons.
        catch (CredentialUnavailableException exception)
        {
            throw new SignInException(NothingToSignInWith(exception.Message), exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new SignInException(
                "Sign-in through Azure failed. "
                + (exception.Message.Contains("AADSTS", StringComparison.Ordinal)
                    ? CliRunner.SignInCause(exception.Message)
                    : string.Join(" ", Reasons(exception.Message).DefaultIfEmpty(CliRunner.SignInCause(exception.Message)))),
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The four places this mode looks for an identity, in the order it looks.</summary>
    internal enum Source { Environment, WorkloadIdentity, ManagedIdentity, AzureCli }

    /// <summary>The sources an <c>AZURE_TOKEN_CREDENTIALS</c> value leaves in play.</summary>
    /// <remarks>
    /// <c>dev</c> is the Azure CLI alone, since the other developer tools are excluded, and
    /// <c>prod</c> the other three. Values outside these were refused by
    /// <see cref="CheckSelection"/> before any were tried.
    /// </remarks>
    internal static IReadOnlyList<Source> SourcesFor(string? selection) =>
        selection?.Trim().ToLowerInvariant() switch
        {
            null or "" => [Source.Environment, Source.WorkloadIdentity, Source.ManagedIdentity, Source.AzureCli],
            "dev" or "azureclicredential" => [Source.AzureCli],
            "prod" => [Source.Environment, Source.WorkloadIdentity, Source.ManagedIdentity],
            "environmentcredential" => [Source.Environment],
            "workloadidentitycredential" => [Source.WorkloadIdentity],
            "managedidentitycredential" => [Source.ManagedIdentity],
            _ => [],
        };

    /// <summary>What to do when no source could sign in, with what each one said.</summary>
    /// <remarks>
    /// Only the sources still in play are suggested. Suggesting all four told someone who had
    /// narrowed <c>AZURE_TOKEN_CREDENTIALS</c> to <c>prod</c> to run <c>az login</c>, which it
    /// would then not try; so when it is narrowed, that is said too.
    /// </remarks>
    internal string NothingToSignInWith(string message)
    {
        var sources = SourcesFor(selection);
        var advice = new List<string>();
        if (sources.Contains(Source.AzureCli))
            advice.Add($"Run `{(tenant is null ? "az login" : $"az login --tenant {tenant}")}` to sign in as yourself");

        var identities = new List<string>();
        if (sources.Contains(Source.WorkloadIdentity))
            identities.Add("a workload identity");
        if (sources.Contains(Source.ManagedIdentity))
            identities.Add("a managed identity");
        if (sources.Contains(Source.Environment))
            identities.Add("the AZURE_CLIENT_ID, AZURE_TENANT_ID and AZURE_CLIENT_SECRET variables");
        if (identities.Count > 0)
        {
            var listed = identities.Count == 1
                ? identities[0]
                : string.Join(", ", identities[..^1]) + ", or " + identities[^1];
            advice.Add(advice.Count == 0 ? $"In a pipeline or on Azure, use {listed}" : $"in a pipeline or on Azure, use {listed}");
        }

        var narrowed = sources.Count < 4
            ? $" {TokenCredentialsVariable}={selection!.Trim()} leaves out the other sources; unset it to try them too."
            : "";
        return $"No Azure sign-in to use. {string.Join("; ", advice)}.{narrowed}"
               + string.Concat(Reasons(message).Select(reason => Environment.NewLine + "       " + reason));
    }

    /// <summary>
    /// Each source's reason from an Azure.Identity message, named as the user knows it and
    /// without the troubleshooting links. Empty when the message lists none.
    /// </summary>
    internal static IReadOnlyList<string> Reasons(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => SourceReason().Match(line.Trim()))
            .Where(match => match.Success)
            .Select(match => $"{SourceName(match.Groups["source"].Value)}: "
                             + TroubleshootingLink().Replace(match.Groups["reason"].Value, "").Trim())
            .ToList();

    private static string SourceName(string credential) => credential switch
    {
        "Environment" => "AZURE_* variables",
        "WorkloadIdentity" => "workload identity",
        "ManagedIdentity" => "managed identity",
        "AzureCli" => "Azure CLI",
        _ => credential,
    };

    // One "- " line per source when several were tried; a single line without the dash when
    // AZURE_TOKEN_CREDENTIALS left only one.
    [GeneratedRegex(@"^(-\s*)?(?<source>\w+?)Credential authentication (unavailable|failed)[.:]?\s*(?<reason>.*)$")]
    private static partial Regex SourceReason();

    [GeneratedRegex(@"\s*See the troubleshooting guide for more information\.\s*https?://\S+")]
    private static partial Regex TroubleshootingLink();

    /// <summary>The variable through which Azure.Identity lets the environment choose its sources.</summary>
    internal const string TokenCredentialsVariable = "AZURE_TOKEN_CREDENTIALS";

    /// <summary>The <c>AZURE_TOKEN_CREDENTIALS</c> values that keep to this mode's sources.</summary>
    private static readonly HashSet<string> Selections = new(StringComparer.OrdinalIgnoreCase)
    {
        // Groups, which the exclusions narrow: `dev` to the Azure CLI, `prod` to the other three.
        "dev", "prod",
        "EnvironmentCredential", "WorkloadIdentityCredential", "ManagedIdentityCredential", "AzureCliCredential",
    };

    /// <summary>
    /// Refuses an <c>AZURE_TOKEN_CREDENTIALS</c> that names a source this mode leaves out.
    /// </summary>
    /// <remarks>
    /// Azure.Identity builds a source named there whatever the exclusions say, so
    /// <c>VisualStudioCredential</c> brought back a source left out on purpose, and
    /// <c>InteractiveBrowserCredential</c> a browser sign-in; a value it does not know escaped
    /// as an unhandled exception and a stack trace. Accepted as Azure.Identity accepts it:
    /// empty is unset, and padding is trimmed, but only spaces is an error there too.
    /// </remarks>
    internal static void CheckSelection(string? selection)
    {
        if (string.IsNullOrEmpty(selection) || Selections.Contains(selection.Trim()))
            return;

        if (string.IsNullOrWhiteSpace(selection))
            throw new OsduException($"{TokenCredentialsVariable} is set to nothing but spaces. Unset it, or name a source.");

        throw new OsduException(
            $"{TokenCredentialsVariable}={selection.Trim()} would sign in through a source {AuthenticationModes.Azure} "
            + "profiles do not use. Set it to prod, dev, EnvironmentCredential, WorkloadIdentityCredential, "
            + "ManagedIdentityCredential or AzureCliCredential, or unset it.");
    }

    private string? Current() =>
        _token is { } token && token.ExpiresOn - Margin > _clock.GetUtcNow() ? token.Token : null;

    /// <summary>The scope to ask for: the profile's, or failing that <c>AZURE_RESOURCE_ID</c>'s.</summary>
    internal static string ScopeFor(OsduConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Scopes))
        {
            return AuthenticationModes.ResourceScope(config.Scopes)?.Scope
                ?? throw new OsduException(
                    $"{AuthenticationModes.Azure} signs in with one scope, a resource's /.default such as "
                    + $"https://energy.azure.com/.default, not '{config.Scopes}'. Correct Scopes in the profile.");
        }

        // Blank is unset: only spaces made the scope "/.default", refused later and less clearly.
        return Environment.GetEnvironmentVariable("AZURE_RESOURCE_ID") is { } resource
               && !string.IsNullOrWhiteSpace(resource)
            ? $"{resource.Trim().TrimEnd('/')}/.default"
            : throw new OsduException(
                $"{AuthenticationModes.Azure} needs to know which resource to sign in to. Set Scopes in the "
                + "profile, such as https://energy.azure.com/.default, or AZURE_RESOURCE_ID.");
    }

    /// <summary>
    /// The tenant an authority names, or null for none or for one of the shared endpoints.
    /// </summary>
    /// <remarks>
    /// Passed on so the Azure CLI answers for the profile's tenant rather than whichever one
    /// <c>az</c> last used. Without one it uses its own default.
    /// </remarks>
    internal static string? TenantOf(string? authority)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri))
            return null;
        var tenant = uri.Segments.Skip(1).FirstOrDefault()?.Trim('/');
        return tenant is null or "" or "common" or "organizations" or "consumers" ? null : tenant;
    }

    /// <summary>The Azure CLI's own application ID, which its tokens carry as <c>appid</c>.</summary>
    private const string AzureCliApplication = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>Who a token was issued to, for reporting.</summary>
    internal static string Describe(string token) => Identify(token).Description;

    /// <summary>Who a token was issued to, and whether it was issued to the Azure CLI.</summary>
    /// <param name="Description">The identity, as a person would name it.</param>
    /// <param name="Person">The person's sign-in name, or null for an application's token.</param>
    /// <param name="IssuedToAzureCli">
    /// Whether the token names the Azure CLI as the client it was issued to, which means the
    /// Azure CLI answered and <c>az login</c> changes who it is. False says nothing about the
    /// source: the Azure CLI signed in as a service principal, as <c>azure/login</c> does in a
    /// pipeline, issues tokens naming that principal, just as the sources ahead of it do.
    /// </param>
    internal sealed record AzureIdentity(string Description, bool IssuedToAzureCli, string? Person = null);

    /// <inheritdoc cref="Describe"/>
    /// <remarks>
    /// Read from the token's claims without checking its signature: this only says which of
    /// the sources answered and as whom, so a person can see what <c>az</c> or the environment
    /// decided. The service is what trusts the token, and it checks it.
    /// </remarks>
    internal static AzureIdentity Identify(string token)
    {
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = claims.RootElement;
            string? Claim(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var application = Claim("appid") ?? Claim("azp");
            var issuedToAzureCli = application == AzureCliApplication;
            if ((Claim("upn") ?? Claim("preferred_username") ?? Claim("unique_name")) is { } person)
                return new(issuedToAzureCli ? $"{person}, through the Azure CLI" : person, issuedToAzureCli, person);

            return new(Claim("xms_mirid") is not null
                ? $"the managed identity {application}"
                : $"the application {application}", issuedToAzureCli);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or FormatException
                                              or JsonException or ArgumentException)
        {
            return new("(not readable from the token)", false);
        }
    }
}
