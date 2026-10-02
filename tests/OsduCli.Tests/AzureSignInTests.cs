using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Profiles that sign in through Azure's own sources — the Python CLI's <c>azure</c> mode:
/// an <c>az login</c> session, a managed or workload identity, or <c>AZURE_*</c> variables.
/// </summary>
[Collection(nameof(EnvironmentCollection))]
public class AzureSignInTests : ConfigTestDirectories
{
    private const string Tenant = "3aa4a235-b6e2-48d5-9195-7fcf05b459b0";

    private static readonly ProfileSettings Nothing = ProfileSettings.Empty;

    private string WriteAzureProfile(string name, params string[] extra) =>
        WritePythonProfile(name, "https://python.example.com", ["authentication_mode = azure", .. extra]);

    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        return root.Parse(args);
    }

    private static ConfigCommand.AddOutcome Add(
        string name, ProfileSettings given, string? from = null,
        Func<string, string?>? ask = null) =>
        ConfigCommand.Add(new ConfigCommand.AddRequest(name, given, from, Force: false), ask,
            askSecret: _ => throw new InvalidOperationException("an azure profile has no secret to ask for"));

    private JsonObject Written(string name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Native, name + ".json")))!["Osdu"]!;

    private static OsduConfig Config(string? scopes = null, string? authority = null) => new()
    {
        Server = "https://osdu.example.com",
        DataPartitionId = "p",
        Scopes = scopes ?? string.Empty,
        Authority = authority ?? string.Empty,
    };

    // ---- reading and signing in ---------------------------------------------------------

    [Theory]
    [InlineData("azure")]
    [InlineData("AZURE")]
    public void TheModeIsRead(string mode)
    {
        WritePythonProfile("dev", extra: $"authentication_mode = {mode}");

        CliConfig.LoadWithSignIn("dev", out var signIn);

        Assert.Equal(nameof(SignInMethod.Azure), signIn.Method.ToString());
    }

    [Fact]
    public void AnAzureProfileSignsInWithoutABrowserOrAnAccount()
    {
        WriteAzureProfile("dev");

        using var context = CliContext.Create(Parse("-c", "dev"));

        Assert.Null(context.Msal);
        Assert.Null(context.Username);
        // The profile's `<resource>/.default openid`, reduced to what Azure is asked for.
        Assert.Equal("https://example.com/.default", context.Azure?.Scope);
    }

    [Fact]
    public void UserIsRefused()
    {
        WriteAzureProfile("dev");

        var exception = Assert.Throws<OsduException>(() =>
            CliContext.Create(Parse("-c", "dev", "--user", "someone@equinor.com")));

        Assert.Contains("--user does not apply", exception.Message);
        Assert.Contains("az login", exception.Message);
    }

    [Theory]
    [InlineData("https://energy.azure.com/.default", "https://energy.azure.com/.default")]
    [InlineData("https://energy.azure.com/.default openid", "https://energy.azure.com/.default")]
    [InlineData("openid 5a1178c2-5867-4a34-8fb8-216164e30b5f/.default profile",
        "5a1178c2-5867-4a34-8fb8-216164e30b5f/.default")]
    public void TheScopeIsTheProfilesOneResource(string scopes, string expected)
    {
        Assert.Equal(expected, AzureTokenProvider.ScopeFor(Config(scopes)));
    }

    [Theory]
    [InlineData("a/.default b/.default")]
    [InlineData("https://example.com/user_impersonation")]
    public void ScopesNamingNoSingleResourceAreRefused(string scopes)
    {
        var exception = Assert.Throws<OsduException>(() => AzureTokenProvider.ScopeFor(Config(scopes)));

        Assert.Contains("Correct Scopes in the profile", exception.Message);
    }

    [Fact]
    public void AProfileWithoutScopesUsesAzureResourceId()
    {
        // Where the Python CLI's azure mode found its resource, so its profiles work as they are.
        Environment.SetEnvironmentVariable("AZURE_RESOURCE_ID", "5a1178c2-5867-4a34-8fb8-216164e30b5f");

        Assert.Equal("5a1178c2-5867-4a34-8fb8-216164e30b5f/.default", AzureTokenProvider.ScopeFor(Config()));
    }

    [Fact]
    public void AProfileWithoutScopesOrAzureResourceIdSaysWhatIsMissing()
    {
        var exception = Assert.Throws<OsduException>(() => AzureTokenProvider.ScopeFor(Config()));

        Assert.Contains("AZURE_RESOURCE_ID", exception.Message);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/" + Tenant, Tenant)]
    [InlineData("https://login.microsoftonline.com/" + Tenant + "/v2.0", Tenant)]
    [InlineData("https://login.microsoftonline.com/equinor.onmicrosoft.com/", "equinor.onmicrosoft.com")]
    [InlineData("https://login.microsoftonline.com/common", null)]
    [InlineData("https://login.microsoftonline.com/organizations/", null)]
    [InlineData("https://login.microsoftonline.com/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheTenantComesFromTheAuthority(string? authority, string? expected)
    {
        Assert.Equal(expected, AzureTokenProvider.TenantOf(authority));
    }

    // ---- tokens ---------------------------------------------------------------------------

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Credential(Func<TokenRequestContext, AccessToken> answer) : TokenCredential
    {
        public List<string[]> Requests { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add(requestContext.Scopes);
            return answer(requestContext);
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ATokenIsReusedUntilItIsAboutToExpire()
    {
        // The Azure CLI source runs `az` for every token, and the client asks once per request.
        var issued = 0;
        var credential = new Credential(_ => new AccessToken($"token-{++issued}", Start.AddHours(1)));
        var clock = new Clock(Start);
        var provider = new AzureTokenProvider(credential, "https://energy.azure.com/.default", clock: clock);
        var cancel = TestContext.Current.CancellationToken;

        Assert.Equal("token-1", await provider.GetTokenAsync(cancel));
        clock.Now = Start.AddMinutes(54);
        Assert.Equal("token-1", await provider.GetTokenAsync(cancel));
        clock.Now = Start.AddMinutes(56);
        Assert.Equal("token-2", await provider.GetTokenAsync(cancel));

        Assert.Equal(2, credential.Requests.Count);
        Assert.All(credential.Requests, scopes => Assert.Equal(["https://energy.azure.com/.default"], scopes));
    }

    /// <summary>What <see cref="DefaultAzureCredential"/> says when no source can sign in.</summary>
    private const string NothingAvailable =
        "DefaultAzureCredential failed to retrieve a token from the included credentials. See the troubleshooting "
        + "guide for more information. https://aka.ms/azsdk/net/identity/defaultazurecredential/troubleshoot\n"
        + "- EnvironmentCredential authentication unavailable. Environment variables are not fully configured. "
        + "See the troubleshooting guide for more information. https://aka.ms/azsdk/net/identity/environmentcredential/troubleshoot\n"
        + "- WorkloadIdentityCredential authentication unavailable. The workload options are not fully configured. "
        + "See the troubleshooting guide for more information. https://aka.ms/azsdk/net/identity/workloadidentitycredential/troubleshoot\n"
        + "- ManagedIdentityCredential authentication unavailable. No response received from the managed identity endpoint.\n"
        + "- AzureCliCredential authentication failed: Please run 'az login' to set up account";

    [Fact]
    public async Task WithNothingToSignInWithItSaysWhatToDoAndWhatEachSourceSaid()
    {
        var provider = new AzureTokenProvider(
            new Credential(_ => throw new CredentialUnavailableException(NothingAvailable)),
            "https://energy.azure.com/.default", Tenant);

        var exception = await Assert.ThrowsAsync<OsduException>(() =>
            provider.GetTokenAsync(TestContext.Current.CancellationToken));

        var lines = exception.Message.Split(Environment.NewLine);
        Assert.Contains($"`az login --tenant {Tenant}`", lines[0]);
        Assert.Equal(
        [
            "       AZURE_* variables: Environment variables are not fully configured.",
            "       workload identity: The workload options are not fully configured.",
            "       managed identity: No response received from the managed identity endpoint.",
            "       Azure CLI: Please run 'az login' to set up account",
        ], lines[1..]);
        Assert.IsType<CredentialUnavailableException>(exception.InnerException);
    }

    [Fact]
    public void ASingleSourcesReasonIsReadToo()
    {
        // AZURE_TOKEN_CREDENTIALS=AzureCliCredential leaves one source, and no dash before it.
        Assert.Equal(["Azure CLI: Please run 'az login' to set up account"],
            AzureTokenProvider.Reasons("AzureCliCredential authentication failed: Please run 'az login' to set up account"));
    }

    [Fact]
    public async Task ARefusedSignInIsOneLineFromItsCode()
    {
        // The likely failure for prod: an app registration that has not consented to the
        // Azure CLI asking for tokens to it.
        var provider = new AzureTokenProvider(
            new Credential(_ => throw new AuthenticationFailedException(
                "AzureCliCredential authentication failed: AADSTS65001: The user or administrator has not "
                + "consented to use the application with ID '04b07795-8ddb-461a-bbee-02f9e1bf7b46'.\r\n"
                + "Trace ID: 1111")),
            "5a1178c2-5867-4a34-8fb8-216164e30b5f/.default");

        var exception = await Assert.ThrowsAsync<OsduException>(() =>
            provider.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Sign-in through Azure failed. AADSTS65001: The user or administrator has not consented to use "
            + "the application with ID '04b07795-8ddb-461a-bbee-02f9e1bf7b46'. Trace ID: 1111",
            exception.Message);
    }

    [Fact]
    public async Task ARefusedSignInWithoutACodeGivesTheSourcesReason()
    {
        var provider = new AzureTokenProvider(
            new Credential(_ => throw new AuthenticationFailedException(
                "DefaultAzureCredential failed to retrieve a token from the included credentials.\n"
                + "- AzureCliCredential authentication failed: Azure CLI authentication timed out.")),
            "https://energy.azure.com/.default");

        var exception = await Assert.ThrowsAsync<OsduException>(() =>
            provider.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Sign-in through Azure failed. Azure CLI: Azure CLI authentication timed out.", exception.Message);
    }

    // ---- who signed in --------------------------------------------------------------------

    private static string Token(object claims)
    {
        static string Part(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"none"}""")}.{Part(JsonSerializer.Serialize(claims))}.";
    }

    [Fact]
    public void APersonSignedInThroughTheAzureCliIsNamed()
    {
        var token = Token(new { upn = "steh@equinor.com", appid = "04b07795-8ddb-461a-bbee-02f9e1bf7b46" });

        Assert.Equal("steh@equinor.com, through the Azure CLI", AzureTokenProvider.Describe(token));
    }

    [Fact]
    public void AManagedIdentityIsNamedAsOne()
    {
        var token = Token(new { appid = "1111", xms_mirid = "/subscriptions/x/resourcegroups/y" });

        Assert.Equal("the managed identity 1111", AzureTokenProvider.Describe(token));
    }

    [Fact]
    public void AnApplicationIsNamedByItsId()
    {
        Assert.Equal("the application 2222", AzureTokenProvider.Describe(Token(new { azp = "2222" })));
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("a.!!!.c")]
    public void ATokenThatCannotBeReadSaysSo(string token)
    {
        Assert.Equal("(not readable from the token)", AzureTokenProvider.Describe(token));
    }

    // ---- config add, show and use ---------------------------------------------------------

    [Fact]
    public void ABrowserProfileCopiedToAzureLeavesItsClientIdAndAccountBehind()
    {
        WriteNativeProfile("dev", user: "someone@equinor.com");

        var outcome = Add("az", Nothing with { AuthenticationMode = "azure" }, from: "dev",
            ask: _ => throw new InvalidOperationException("everything needed was copied"));

        var written = Written("az");
        Assert.Equal("azure", written["AuthenticationMode"]!.GetValue<string>());
        Assert.Equal("https://example.com/.default", written["Scopes"]!.GetValue<string>());
        Assert.Null(written["ClientId"]);
        Assert.Null(written["User"]);
        Assert.Null(written["ClientSecret"]);
        Assert.Equal(["User", "ClientId"], outcome.NotCarried);
        Assert.Equal(["openid"], outcome.DroppedScopes);
    }

    [Fact]
    public void ANewAzureProfileDoesNotAskForAClientId()
    {
        var asked = new List<string>();
        var answers = new Queue<string>(["https://osdu.example.com", "p",
            $"https://login.microsoftonline.com/{Tenant}", "https://energy.azure.com/.default"]);

        Add("az", Nothing with { AuthenticationMode = "azure" },
            ask: label => { asked.Add(label); return answers.Dequeue(); });

        Assert.Equal(4, asked.Count);
        Assert.DoesNotContain(asked, label => label.StartsWith("Client ID", StringComparison.Ordinal));
    }

    [Fact]
    public void AClientIdIsRefusedForAnAzureProfile()
    {
        WriteNativeProfile("dev");

        var exception = Assert.Throws<OsduException>(() =>
            Add("az", Nothing with { AuthenticationMode = "azure", ClientId = "app" }, from: "dev"));

        Assert.Contains("--client-id does not apply", exception.Message);
    }

    [Fact]
    public void ShowSaysWhatDoesNotApply()
    {
        WriteAzureProfile("dev");
        var config = CliConfig.LoadWithSignIn("dev", out var signIn);

        var rows = ConfigCommand.Rows(config, signIn).ToDictionary(row => row.Setting, row => row.Value);

        Assert.Equal("azure", rows["authentication-mode"]);
        Assert.StartsWith("(not used", rows["client-id"]);
        Assert.Contains("account list", rows["user"]);
        Assert.False(rows.ContainsKey("client-secret"));
    }
}
