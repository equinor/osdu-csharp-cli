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
/// <c>AzureCliCredential</c> alone.</para>
///
/// <para>The Python CLI read its resource only from environment variables. Here it is the
/// profile's scope, so a profile names its environment completely; <c>AZURE_RESOURCE_ID</c>
/// is the fallback for a profile that has none, as a Python <c>azure</c> profile does not.</para>
/// </remarks>
internal sealed partial class AzureTokenProvider(
    TokenCredential credential, string scope, string? tenant = null, TimeProvider? clock = null)
    : ITokenProvider
{
    /// <summary>How long before it expires a token is replaced, so none expires in flight.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken? _token;

    /// <summary>The scope tokens are asked for.</summary>
    internal string Scope => scope;

    internal static AzureTokenProvider Create(OsduConfig config)
    {
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
        }), ScopeFor(config), tenant);
    }

    /// <remarks>
    /// Kept for the life of the command. The Azure CLI source runs <c>az</c> for every token it
    /// is asked for, and the client asks once per request, so a search paging through results
    /// started a process per page.
    /// </remarks>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
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
            throw new OsduException(NothingToSignInWith(exception.Message), exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new OsduException(
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

    /// <summary>What to do when no source could sign in, with what each one said.</summary>
    internal string NothingToSignInWith(string message)
    {
        var login = tenant is null ? "az login" : $"az login --tenant {tenant}";
        var reasons = Reasons(message);
        return $"No Azure sign-in to use. Run `{login}` to sign in as yourself; in a pipeline or on Azure, "
               + "use its managed or workload identity, or set AZURE_CLIENT_ID, AZURE_TENANT_ID and "
               + "AZURE_CLIENT_SECRET."
               + string.Concat(reasons.Select(reason => Environment.NewLine + "       " + reason));
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

        return Environment.GetEnvironmentVariable("AZURE_RESOURCE_ID") is { Length: > 0 } resource
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
    /// <remarks>
    /// Read from the token's claims without checking its signature: this only says which of
    /// the sources answered and as whom, so a person can see what <c>az</c> or the environment
    /// decided. The service is what trusts the token, and it checks it.
    /// </remarks>
    internal static string Describe(string token)
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
            if ((Claim("upn") ?? Claim("preferred_username") ?? Claim("unique_name")) is { } person)
            {
                return application == AzureCliApplication
                    ? $"{person}, through the Azure CLI"
                    : person;
            }

            return Claim("xms_mirid") is not null
                ? $"the managed identity {application}"
                : $"the application {application}";
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or FormatException
                                              or JsonException or ArgumentException)
        {
            return "(not readable from the token)";
        }
    }
}
