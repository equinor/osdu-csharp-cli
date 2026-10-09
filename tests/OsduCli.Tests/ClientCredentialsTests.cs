using System.CommandLine;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Microsoft.Identity.Client;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Profiles that sign in as an application with a client secret — the Python CLI's
/// <c>msal_non_interactive</c>.
/// </summary>
/// <remarks>
/// osducs read neither <c>authentication_mode</c> nor <c>client_secret</c>, and signed every
/// profile in through a browser. A tester's two admin profiles failed both ways that can go:
/// one opened a browser for an app registration with no redirect URI for it, and the other,
/// given <c>--user</c>, sent a person's token to an environment that answered with an HTML
/// error page.
/// </remarks>
[Collection(nameof(EnvironmentCollection))]
public class ClientCredentialsTests : ConfigTestDirectories
{
    private const string Secret = "abc~123.secret_value";

    private static readonly ProfileSettings Nothing = ProfileSettings.Empty;

    private string WriteApplicationProfile(string name, params string[] extra) =>
        WritePythonProfile(name, "https://python.example.com",
            ["authentication_mode = msal_non_interactive", $"client_secret = {Secret}", .. extra]);

    private static SignInSettings SignIn(string? config)
    {
        CliConfig.LoadWithSignIn(config, out var signIn);
        return signIn;
    }

    private static ConfigCommand.AddOutcome Add(
        string name, ProfileSettings given, string? from = null, bool force = false,
        Func<string, string?>? ask = null, Func<string, string?>? askSecret = null) =>
        ConfigCommand.Add(new ConfigCommand.AddRequest(name, given, from, force), ask, askSecret);

    private JsonObject Written(string name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Native, name + ".json")))!["Osdu"]!;

    // ---- reading ------------------------------------------------------------------------

    [Fact]
    public void APythonProfileSetToMsalNonInteractiveSignsInWithItsSecret()
    {
        WriteApplicationProfile("test_admin");

        var signIn = SignIn("test_admin");

        Assert.Equal(SignInMethod.ClientCredentials, signIn.Method);
        Assert.Equal("msal_non_interactive", signIn.Mode);
        Assert.Equal(Secret, signIn.Secret?.Value);
    }

    [Fact]
    public void AJsonProfileCanHoldTheModeAndTheSecret()
    {
        File.WriteAllText(Path.Combine(Native, "ci.json"), $$"""
            { "Osdu": { "Server": "https://osdu.example.com", "DataPartitionId": "p",
                        "Authority": "https://login.microsoftonline.com/t", "ClientId": "c",
                        "Scopes": "c/.default", "AuthenticationMode": "msal_non_interactive",
                        "ClientSecret": "{{Secret}}" } }
            """);

        var signIn = SignIn("ci");

        Assert.Equal(SignInMethod.ClientCredentials, signIn.Method);
        Assert.Equal(Secret, signIn.Secret?.Value);
    }

    [Fact]
    public void AProfileWithoutAModeSignsInInteractively()
    {
        // What every profile meant before osducs read the setting.
        WriteNativeProfile("dev");

        var signIn = SignIn("dev");

        Assert.Equal(SignInMethod.Interactive, signIn.Method);
        Assert.Equal("msal_interactive", signIn.Mode);
        Assert.Null(signIn.Secret);
    }

    [Theory]
    // The method by name: SignInMethod is internal, and a public test cannot take one.
    [InlineData("MSAL_NON_INTERACTIVE", nameof(SignInMethod.ClientCredentials))]
    [InlineData(" msal_interactive ", nameof(SignInMethod.Interactive))]
    [InlineData("refresh_token", nameof(SignInMethod.Unsupported))]
    [InlineData("gc", nameof(SignInMethod.Unsupported))]
    public void TheModeIsReadInAnyCasingAndAnUnknownOneIsReported(string mode, string expected)
    {
        WritePythonProfile("dev", extra: $"authentication_mode = {mode}");

        Assert.Equal(expected, SignIn("dev").Method.ToString());
    }

    [Fact]
    public void TheEnvironmentSuppliesOrOverridesTheSecret()
    {
        // The pipeline case: the profile holds everything but the secret.
        WritePythonProfile("ci", extra: "authentication_mode = msal_non_interactive");
        Assert.Null(SignIn("ci").Secret);

        Environment.SetEnvironmentVariable("OSDU_CLIENT_SECRET", "from-env");
        Assert.Equal("from-env", SignIn("ci").Secret?.Value);

        Environment.SetEnvironmentVariable("OSDU_CLIENT_SECRET", null);
        Environment.SetEnvironmentVariable("Osdu__ClientSecret", "from-dotnet-env");
        Assert.Equal("from-dotnet-env", SignIn("ci").Secret?.Value);
    }

    [Fact]
    public void TheEnvironmentCanSetTheMode()
    {
        WriteNativeProfile("dev");
        Environment.SetEnvironmentVariable("OSDU_AUTHENTICATION_MODE", "msal_non_interactive");

        Assert.Equal(SignInMethod.ClientCredentials, SignIn("dev").Method);
    }

    [Fact]
    public void TheModeAndSecretAreNotInheritedFromAFileBeneathTheProfile()
    {
        // Layered like the other settings, a Python default config set to msal_non_interactive
        // would turn a JSON profile written before osducs had the setting into an application
        // sign-in, with that file's secret.
        File.WriteAllLines(Path.Combine(Python, "config"),
            ["[core]", "server = https://default.example.com", "data_partition_id = p",
             "authority = https://login.microsoftonline.com/t", "client_id = app", "scopes = app/.default",
             "authentication_mode = msal_non_interactive", $"client_secret = {Secret}"]);
        WriteNativeProfile("dev");
        CliConfig.Select("dev");

        var signIn = SignIn(null);

        Assert.Equal(SignInMethod.Interactive, signIn.Method);
        Assert.Null(signIn.Secret);
    }

    [Fact]
    public void NothingThatPrintsTheSettingsPrintsTheSecret()
    {
        WriteApplicationProfile("test_admin");

        Assert.DoesNotContain(Secret, CliConfig.ReadProfile("test_admin").ToString());
        Assert.DoesNotContain(Secret, SignIn("test_admin").ToString());
    }

    [Fact]
    public void TheEnvironmentNoteNamesTheSecretVariableButNotItsValue()
    {
        Environment.SetEnvironmentVariable("OSDU_CLIENT_SECRET", Secret);

        var overrides = CliConfig.EnvironmentOverrides();

        Assert.Contains("OSDU_CLIENT_SECRET", overrides);
        Assert.DoesNotContain(overrides, name => name.Contains(Secret, StringComparison.Ordinal));
    }

    // ---- signing in ---------------------------------------------------------------------

    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        return root.Parse(args);
    }

    [Fact]
    public void AnApplicationProfileSignsInWithoutABrowserOrAnAccount()
    {
        WriteApplicationProfile("test_admin");

        using var context = CliContext.Create(Parse("-c", "test_admin"));

        Assert.Null(context.Msal);
        Assert.Null(context.Username);
    }

    [Fact]
    public void UserIsRefusedForAnApplicationProfile()
    {
        // The tester added --user to settle which account to use; with an application
        // profile there is none, and acting as the application instead would be a surprise.
        WriteApplicationProfile("test_admin");

        var exception = Assert.Throws<OsduException>(() =>
            CliContext.Create(Parse("-c", "test_admin", "--user", "someone@equinor.com")));

        Assert.Contains("--user does not apply", exception.Message);
    }

    [Fact]
    public void AUserInTheProfileOrEnvironmentIsPassedOver()
    {
        // Only a default, so not a reason to fail; an OSDU_USER exported for another profile
        // must not break a pipeline.
        WriteApplicationProfile("test_admin", "user = someone@equinor.com");

        using var context = CliContext.Create(Parse("-c", "test_admin"));

        Assert.Null(context.Username);
    }

    [Fact]
    public void AMissingSecretSaysWhereToPutOne()
    {
        WritePythonProfile("ci", extra: "authentication_mode = msal_non_interactive");

        var exception = Assert.Throws<OsduException>(() => CliContext.Create(Parse("-c", "ci")));

        Assert.Contains("OSDU_CLIENT_SECRET", exception.Message);
        Assert.Contains("ClientSecret", exception.Message);
    }

    [Fact]
    public void AnUnsupportedModeIsRefusedRatherThanReplacedWithABrowser()
    {
        WritePythonProfile("old", extra: "authentication_mode = refresh_token");

        var exception = Assert.Throws<OsduException>(() => CliContext.Create(Parse("-c", "old")));

        Assert.Contains("'refresh_token' is not supported", exception.Message);
    }

    [Fact]
    public async Task ASignInRefusedOverSeveralLinesIsReportedOnOne()
    {
        // Entra ID can put the trace and correlation IDs on lines of their own. Kept, since
        // support asks for them, but on the one line.
        WriteApplicationProfile("test_admin");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            await CliRunner.RunAsync(Parse("-c", "test_admin"),
                (_, _) => throw new MsalServiceException("invalid_client",
                    "Original exception: AADSTS7000215: Invalid client secret provided.\r\n"
                    + "Trace ID: 1111\r\nCorrelation ID: 2222\r\nTimestamp: 2026-10-02 12:00:00Z"),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(
            "error: sign-in failed. AADSTS7000215: Invalid client secret provided. Trace ID: 1111 "
            + "Correlation ID: 2222 Timestamp: 2026-10-02 12:00:00Z" + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public async Task ARefusedSignInIsOneLineNamingTheCause()
    {
        // What a rotated secret looks like. It escaped as a stack trace.
        WriteApplicationProfile("test_admin");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            var code = await CliRunner.RunAsync(Parse("-c", "test_admin"),
                (_, _) => throw new MsalServiceException("invalid_client",
                    "A configuration issue is preventing authentication. See https://aka.ms/msal-net-invalid-client "
                    + "for details.  Original exception: AADSTS7000215: Invalid client secret provided."),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("error: sign-in failed. AADSTS7000215: Invalid client secret provided." + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public async Task AFileTheUserCannotReadOrWriteIsOneLine()
    {
        // A file to upload without read access, or a directory to download into without write
        // access: Run handled this, RunAsync let it escape as a stack trace.
        WriteApplicationProfile("test_admin");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            var code = await CliRunner.RunAsync(Parse("-c", "test_admin"),
                (_, _) => throw new UnauthorizedAccessException("Access to the path '/data/log.las' is denied."),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("error: Access to the path '/data/log.las' is denied." + Environment.NewLine, error.ToString());
    }

    // ---- config add ---------------------------------------------------------------------

    [Fact]
    public void FromCopiesTheModeAndTheSecret()
    {
        WriteApplicationProfile("test_admin");

        var outcome = Add("test_admin", Nothing, from: "test_admin");

        Assert.Equal("msal_non_interactive", Written("test_admin")["AuthenticationMode"]!.GetValue<string>());
        Assert.Equal(Secret, Written("test_admin")["ClientSecret"]!.GetValue<string>());
        Assert.Empty(outcome.NotCarried);
        Assert.Equal(Secret, SignIn("test_admin").Secret?.Value);
    }

    [Fact]
    public void TheModeIsWrittenInItsUsualSpelling()
    {
        WritePythonProfile("app", extra: ["authentication_mode = MSAL_Non_Interactive", $"client_secret = {Secret}"]);

        Add("app", Nothing, from: "app");

        Assert.Equal("msal_non_interactive", Written("app")["AuthenticationMode"]!.GetValue<string>());
    }

    [Fact]
    public void TheModeIsWrittenEvenWhenItIsTheDefault()
    {
        Add("dev", Nothing with
        {
            Server = "https://osdu.example.com", DataPartitionId = "dev",
            Authority = "https://login.microsoftonline.com/t", ClientId = "c", Scopes = "c/.default",
        });

        Assert.Equal("msal_interactive", Written("dev")["AuthenticationMode"]!.GetValue<string>());
        Assert.Null(Written("dev")["ClientSecret"]);
    }

    [Fact]
    public void ANewApplicationProfileAsksForTheSecretWithoutShowingIt()
    {
        WritePythonProfile("dev");
        var asked = new List<string>();

        Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive" }, from: "dev",
            ask: _ => throw new InvalidOperationException("only the secret is missing"),
            askSecret: label => { asked.Add(label); return Secret; });

        Assert.StartsWith("Client secret", Assert.Single(asked));
        Assert.Equal(Secret, Written("ci")["ClientSecret"]!.GetValue<string>());
    }

    [Fact]
    public void TheSecretIsAskedForLast()
    {
        // Asked first, a mistyped server refused afterwards threw the typed secret away.
        var asked = new List<string>();
        var answers = new Queue<string>(["https://osdu.example.com", "p",
            "https://login.microsoftonline.com/t", "app", "app/.default"]);

        Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive" },
            ask: label => { asked.Add(label); return answers.Dequeue(); },
            askSecret: label => { asked.Add(label); return Secret; });

        Assert.Equal(6, asked.Count);
        Assert.StartsWith("Client secret for app", asked[^1]);
    }

    [Fact]
    public void ASecretIsNotCopiedToAnotherApplication()
    {
        // It belongs to the client ID it was issued for.
        WriteApplicationProfile("test_admin");
        var asked = new List<string>();

        var outcome = Add("other", Nothing with { ClientId = "another-app" }, from: "test_admin",
            askSecret: label => { asked.Add(label); return "another-secret"; });

        Assert.StartsWith("Client secret for another-app", Assert.Single(asked));
        Assert.Equal("another-secret", Written("other")["ClientSecret"]!.GetValue<string>());
        Assert.Contains("client_secret", outcome.NotCarried);
    }

    [Theory]
    [InlineData("authentication_mode = msal_interactive")]
    // No mode is a browser sign-in too.
    [InlineData("")]
    public void ABrowserProfilesLeftoverSecretIsNotPromoted(string mode)
    {
        // Switching the copy to an application sign-in used the leftover, unchecked secret.
        WritePythonProfile("dev", extra: [mode, $"client_secret = {Secret}"]);
        var asked = new List<string>();

        var outcome = Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive" }, from: "dev",
            askSecret: label => { asked.Add(label); return "the-application-secret"; });

        Assert.Single(asked);
        Assert.Equal("the-application-secret", Written("ci")["ClientSecret"]!.GetValue<string>());
        Assert.Contains("client_secret", outcome.NotCarried);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WithNoSecretGivenTheProfileIsWrittenWithoutOne(string? answer)
    {
        // Left to OSDU_CLIENT_SECRET, as in a pipeline, and said so when it is used.
        WritePythonProfile("dev");

        Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive" }, from: "dev",
            askSecret: _ => answer);

        Assert.Null(Written("ci")["ClientSecret"]);
        Assert.Equal(SignInMethod.ClientCredentials, SignIn("ci").Method);
    }

    [Fact]
    public void ABrowserProfileDoesNotCarryALeftoverSecret()
    {
        WritePythonProfile("dev", extra: ["authentication_mode = msal_interactive", $"client_secret = {Secret}"]);

        var outcome = Add("dev", Nothing, from: "dev");

        Assert.Null(Written("dev")["ClientSecret"]);
        Assert.Contains("client_secret", outcome.NotCarried);
    }

    [Fact]
    public void SwitchingACopyToABrowserSignInDropsTheSecret()
    {
        WriteApplicationProfile("test_admin");

        var outcome = Add("mine", Nothing with { AuthenticationMode = "msal_interactive" }, from: "test_admin");

        Assert.Null(Written("mine")["ClientSecret"]);
        Assert.Contains("client_secret", outcome.NotCarried);
    }

    [Fact]
    public void CopyingAnUnsupportedModeIsRefusedUnlessAnotherIsChosen()
    {
        WritePythonProfile("old", extra: "authentication_mode = refresh_token");

        var exception = Assert.Throws<OsduException>(() => Add("old", Nothing, from: "old"));
        Assert.Contains("--authentication-mode", exception.Message);
        Assert.False(File.Exists(Path.Combine(Native, "old.json")));

        Add("old", Nothing with { AuthenticationMode = "msal_interactive" }, from: "old");
        Assert.Equal(SignInMethod.Interactive, SignIn("old").Method);
    }

    [Fact]
    public void UserIsRefusedWhenAddingAnApplicationProfile()
    {
        WriteApplicationProfile("test_admin");

        var exception = Assert.Throws<OsduException>(() =>
            Add("test_admin", Nothing with { User = "someone@equinor.com" }, from: "test_admin"));

        Assert.Contains("--user does not apply", exception.Message);
    }

    [Fact]
    public void ACopiedDefaultAccountIsLeftBehindWhenTheCopySignsInAsAnApplication()
    {
        // Refused at first, with nothing on the command line able to clear it, so a browser
        // profile with a default account could not be turned into an application one.
        WriteNativeProfile("dev", user: "someone@equinor.com");

        var outcome = Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive" }, from: "dev",
            askSecret: _ => Secret);

        Assert.Null(Written("ci")["User"]);
        Assert.Equal(Path.Combine(Native, "dev.json"), outcome.NotCarriedFrom);
        Assert.Equal(["User"], outcome.NotCarried);
    }

    [Fact]
    public void PersonScopesAreRemovedFromAnApplicationProfile()
    {
        // A browser profile's `<resource>/.default openid`: Entra ID refuses openid in a
        // client-credentials sign-in, so the copy failed at its first request.
        WritePythonProfile("dev");

        var outcome = Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive" }, from: "dev",
            askSecret: _ => Secret);

        Assert.Equal("https://example.com/.default", Written("ci")["Scopes"]!.GetValue<string>());
        Assert.Equal(["openid"], outcome.DroppedScopes);
    }

    [Theory]
    [InlineData("https://example.com/.default https://other.example.com/.default")]
    [InlineData("https://example.com/user_impersonation")]
    [InlineData("openid profile")]
    public void ScopesAnApplicationCannotSignInWithAreRefused(string scopes)
    {
        WritePythonProfile("dev");

        var exception = Assert.Throws<OsduException>(() =>
            Add("ci", Nothing with { AuthenticationMode = "msal_non_interactive", Scopes = scopes }, from: "dev",
                askSecret: _ => throw new InvalidOperationException("refused before the secret is asked for")));

        Assert.Contains("--scopes", exception.Message);
        Assert.False(File.Exists(Path.Combine(Native, "ci.json")));
    }

    [Fact]
    public void ABrowserProfileKeepsItsScopes()
    {
        WritePythonProfile("dev");

        var outcome = Add("dev", Nothing, from: "dev");

        Assert.Equal("https://example.com/.default openid", Written("dev")["Scopes"]!.GetValue<string>());
        Assert.Empty(outcome.DroppedScopes);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ProfilesAreWrittenOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        WriteApplicationProfile("test_admin");

        Add("test_admin", Nothing, from: "test_admin");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(Native, "test_admin.json")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindowsOnlyTheOwnerIsGrantedAccess()
    {
        // Explicit rather than inherited: OSDU_CONFIG_DIR can point at a shared folder.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows access lists only.");
        var existing = WriteNativeProfile("test_admin");
        WriteApplicationProfile("source");

        Add("test_admin", Nothing, from: "source", force: true);
        Add("fresh", Nothing, from: "source");

        foreach (var path in new[] { existing, Path.Combine(Native, "fresh.json") })
        {
            var security = new FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(rules);
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AReplacedProfileIsRestrictedToo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        var existing = WriteNativeProfile("test_admin");
        File.SetUnixFileMode(existing, (UnixFileMode)0b110_100_100);
        WriteApplicationProfile("source");

        Add("test_admin", Nothing, from: "source", force: true);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(existing));
        Assert.Equal(Secret, Written("test_admin")["ClientSecret"]!.GetValue<string>());
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ALinkInThePlaceOfAProfileIsReplacedNotWrittenThrough()
    {
        // Someone who can write to a shared OSDU_CONFIG_DIR puts a link where the profile goes,
        // pointing at a file they can read. Writing in place sent the secret there.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating links needs a privilege on Windows.");
        var elsewhere = Path.Combine(Python, "elsewhere.txt");
        File.WriteAllText(elsewhere, "untouched");
        var target = Path.Combine(Native, "test_admin.json");
        File.CreateSymbolicLink(target, elsewhere);
        WriteApplicationProfile("source");

        Add("test_admin", Nothing, from: "source", force: true);

        Assert.Equal("untouched", File.ReadAllText(elsewhere));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal(Secret, Written("test_admin")["ClientSecret"]!.GetValue<string>());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ALinkToAFileNotYetThereIsNotFollowed()
    {
        // The same, for a new profile: the link points at a file that does not exist yet, which
        // creating the profile in place would have created, secret and all.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating links needs a privilege on Windows.");
        var stolen = Path.Combine(Python, "stolen.json");
        File.CreateSymbolicLink(Path.Combine(Native, "test_admin.json"), stolen);
        WriteApplicationProfile("source");

        // .NET counts a link as existing even when what it points at does not, so this is the
        // check for an existing profile; a link made after that check fails the move instead.
        var exception = Assert.Throws<OsduException>(() => Add("test_admin", Nothing, from: "source"));

        Assert.Contains("already exists", exception.Message);
        Assert.False(File.Exists(stolen));
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        WriteApplicationProfile("source");

        Add("test_admin", Nothing, from: "source");
        Add("test_admin", Nothing, from: "source", force: true);

        Assert.Equal(["test_admin.json"],
            Directory.EnumerateFileSystemEntries(Native).Select(Path.GetFileName).Where(name => name != "state.json"));
    }

    // ---- config show --------------------------------------------------------------------

    private static Dictionary<string, string?> Shown(string profile)
    {
        var config = CliConfig.LoadWithSignIn(profile, out var signIn);
        return ConfigCommand.Rows(config, signIn).ToDictionary(row => row.Setting, row => row.Value);
    }

    [Fact]
    public void ShowSaysASecretIsSetWithoutShowingIt()
    {
        WriteApplicationProfile("test_admin");

        var rows = Shown("test_admin");

        Assert.Equal("msal_non_interactive", rows["authentication-mode"]);
        Assert.Equal("(set, hidden)", rows["client-secret"]);
        Assert.DoesNotContain(rows.Values, value => value?.Contains(Secret, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ShowSaysWhereToPutAMissingSecret()
    {
        WritePythonProfile("ci", extra: "authentication_mode = msal_non_interactive");

        Assert.Contains("OSDU_CLIENT_SECRET", Shown("ci")["client-secret"]);
    }

    [Fact]
    public void ShowHasNoSecretRowForABrowserProfile()
    {
        WriteNativeProfile("dev");

        var rows = Shown("dev");

        Assert.Equal("msal_interactive", rows["authentication-mode"]);
        Assert.False(rows.ContainsKey("client-secret"));
    }

    [Fact]
    public void ShowMarksAnUnsupportedMode()
    {
        WritePythonProfile("old", extra: "authentication_mode = refresh_token");

        Assert.Equal("refresh_token (not supported by osducs)", Shown("old")["authentication-mode"]);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ASecretOthersCanReadIsPointedOut()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        // The Python CLI restricts only the profiles it writes itself.
        var exposed = WriteApplicationProfile("exposed");
        File.SetUnixFileMode(exposed, (UnixFileMode)0b110_100_100);
        var restricted = WriteApplicationProfile("restricted");
        File.SetUnixFileMode(restricted, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var noSecret = WritePythonProfile("dev");
        File.SetUnixFileMode(noSecret, (UnixFileMode)0b110_100_100);

        Assert.Equal([exposed], ConfigCommand.ReadableByOthers([exposed, restricted, noSecret]));
    }
}
