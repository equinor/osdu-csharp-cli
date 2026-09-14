using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Reading the Python CLI's profiles is what lets an existing user run this tool without
/// writing any new config, so the mapping and the lookup rules are worth pinning.
/// </summary>
[Collection(nameof(EnvironmentCollection))]
public class OsduCliProfileTests : IDisposable
{
    private static readonly string[] Managed =
    [
        "OSDUCLI_CONFIG_DIR", "OSDU_CONFIG_DIR",
        "OSDU_SERVER", "OSDU_DATA_PARTITION_ID", "OSDU_AUTHORITY",
        "OSDU_CLIENT_ID", "OSDU_SCOPES",
        "Osdu__Server", "Osdu__DataPartitionId", "Osdu__Authority",
        "Osdu__ClientId", "Osdu__Scopes",
    ];

    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "osducli-profile-tests-" + Guid.NewGuid().ToString("N"));

    public OsduCliProfileTests()
    {
        foreach (var name in Managed)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("OSDUCLI_CONFIG_DIR", _directory);
        // osducs's own directory too: resolving the default config reads the selection kept
        // there, and the developer's real one must not decide these tests.
        Environment.SetEnvironmentVariable("OSDU_CONFIG_DIR", Path.Combine(_directory, "native"));
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A profile in the shape the Python CLI actually writes.</summary>
    private string WriteProfile(string name, string server = "https://dev.example.com")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"""
            [core]
            server = {server}
            storage_url = /api/storage/v2/
            unit_url = /api/unit/v3/
            data_partition_id = dev
            legal_tag = dev-equinor-osdu-reference-default
            acl_viewer = data.default.viewers@dev.dataservices.energy
            authentication_mode = msal_interactive
            authority = https://login.microsoftonline.com/3aa4a235
            scopes = https://energy.azure.com/.default openid
            client_id = 7a414874-4b27-4378-b34f-bc9e5a5faa4f
            """);
        return path;
    }

    [Fact]
    public void LoadsAProfileByBareName()
    {
        WriteProfile("dev");

        var config = CliConfig.Load("dev");

        Assert.Equal("https://dev.example.com", config.Server);
        Assert.Equal("dev", config.DataPartitionId);
        Assert.Equal("7a414874-4b27-4378-b34f-bc9e5a5faa4f", config.ClientId);
        Assert.Equal("https://energy.azure.com/.default openid", config.Scopes);
    }

    [Fact]
    public void PerServiceUrlsAreDeliberatelyIgnored()
    {
        // Honouring storage_url = /api/storage/v2/ would double the version, because the
        // CLI already appends spec paths that contain it. The specs are also fresher.
        WriteProfile("dev");

        var config = CliConfig.Load("dev");

        Assert.Empty(config.EndpointOverrides);
    }

    [Fact]
    public void EnvironmentStillOverridesAProfile()
    {
        WriteProfile("dev");
        Environment.SetEnvironmentVariable("OSDU_SERVER", "https://from-env.example.com");

        Assert.Equal("https://from-env.example.com", CliConfig.Load("dev").Server);
    }

    [Fact]
    public void UnknownProfileNamesTheOnesThatExist()
    {
        WriteProfile("dev");
        WriteProfile("prod");

        var exception = Assert.Throws<OsduException>(() => CliConfig.Load("nosuch"));

        Assert.Contains("nosuch", exception.Message);
        Assert.Contains("dev", exception.Message);
        Assert.Contains("prod", exception.Message);
    }

    [Fact]
    public void TokenCachesAreNotOfferedAsProfiles()
    {
        WriteProfile("dev");
        File.WriteAllText(Path.Combine(_directory, "msal_token_cache.bin"), "{}");

        var exception = Assert.Throws<OsduException>(() => CliConfig.Load("nosuch"));

        Assert.DoesNotContain("msal_token_cache.bin", exception.Message);
    }

    [Fact]
    public void APathIsUsedAsGivenRatherThanTreatedAsAProfile()
    {
        var path = WriteProfile("dev");

        Assert.Equal([path], CliConfig.Resolve(path));
    }

    [Fact]
    public void NoArgumentLooksInBothConventions()
    {
        var candidates = CliConfig.Resolve(null);

        Assert.Contains(candidates, c => c.EndsWith("config", StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.EndsWith("config.json", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileFormatIsDetectedByContentNotExtension()
    {
        // The Python profiles have no extension at all, so extension-sniffing would fail.
        var path = WriteProfile("dev");
        Assert.False(Path.HasExtension(path));

        Assert.Equal("https://dev.example.com", CliConfig.Load(path).Server);
    }

    [Theory]
    [InlineData("# comment", 0)]
    [InlineData("; comment", 0)]
    [InlineData("[core]", 0)]
    [InlineData("", 0)]
    [InlineData("server = https://x", 1)]
    [InlineData("wellbore_ddms_url= /api/os-wellbore-ddms", 1)]
    public void ParseSkipsWhatIsNotAKeyValuePair(string line, int expected)
    {
        Assert.Equal(expected, OsduCliIniConfigurationProvider.Parse([line]).Count());
    }

    [Fact]
    public void ParseSplitsOnTheFirstEqualsOnly()
    {
        // Scopes and authorities are URLs; a naive split would truncate them.
        var pairs = OsduCliIniConfigurationProvider
            .Parse(["scopes = https://energy.azure.com/.default openid"]).ToList();

        var (key, value) = Assert.Single(pairs);
        Assert.Equal("scopes", key);
        Assert.Equal("https://energy.azure.com/.default openid", value);
    }

    [Fact]
    public void FollowsTheProfileThePythonCliHasSelected()
    {
        // `osdu config update` writes the choice here; honouring it means no -c on every
        // command, and no second notion of "current environment" to disagree with it.
        WriteProfile("dev", "https://selected.example.com");
        File.WriteAllText(Path.Combine(_directory, "state"),
            $"[core]\ndefault_config = {Path.Combine(_directory, "dev")}\n");

        Assert.Equal("https://selected.example.com", CliConfig.Load(null).Server);
    }

    [Fact]
    public void AnExplicitConfigStillWinsOverTheSelectedProfile()
    {
        WriteProfile("dev", "https://selected.example.com");
        WriteProfile("prod", "https://explicit.example.com");
        File.WriteAllText(Path.Combine(_directory, "state"),
            $"[core]\ndefault_config = {Path.Combine(_directory, "dev")}\n");

        Assert.Equal("https://explicit.example.com", CliConfig.Load("prod").Server);
    }

    [Fact]
    public void NoStateFileIsNotAnError()
    {
        Assert.Null(CliConfig.PythonSelectedProfile());
    }

    [Fact]
    public void StateIsNotOfferedAsAProfile()
    {
        WriteProfile("dev");
        File.WriteAllText(Path.Combine(_directory, "state"), "[core]\n");

        var exception = Assert.Throws<OsduException>(() => CliConfig.Load("nosuch"));

        Assert.Contains("dev", exception.Message);
        Assert.DoesNotContain(" state", exception.Message);
    }
}
