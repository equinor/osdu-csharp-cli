using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Config resolution reads process environment variables, which are global. These run in
/// their own non-parallel collection so one test's variables cannot leak into another's.
/// </summary>
[CollectionDefinition(nameof(EnvironmentCollection), DisableParallelization = true)]
public class EnvironmentCollection;

[Collection(nameof(EnvironmentCollection))]
public class CliConfigTests : IDisposable
{
    private static readonly string[] Managed =
    [
        "OSDU_SERVER", "OSDU_DATA_PARTITION_ID", "OSDU_AUTHORITY",
        "OSDU_CLIENT_ID", "OSDU_SCOPES",
        "Osdu__Server", "Osdu__DataPartitionId", "Osdu__Authority",
        "Osdu__ClientId", "Osdu__Scopes",
    ];

    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "osdu-cli-tests-" + Guid.NewGuid().ToString("N"));

    public CliConfigTests()
    {
        // A developer's own OSDU_* variables would otherwise decide the outcome.
        foreach (var name in Managed)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteConfig(string server = "https://osdu.example.com")
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, $$"""
            {
              "Osdu": {
                "Server": "{{server}}",
                "DataPartitionId": "opendes",
                "Authority": "https://login.microsoftonline.com/common",
                "ClientId": "00000000-0000-0000-0000-000000000000",
                "Scopes": "https://osdu.example.com/.default"
              }
            }
            """);
        return path;
    }

    [Fact]
    public void LoadsFromAConfigFile()
    {
        var config = CliConfig.Load(WriteConfig());

        Assert.Equal("https://osdu.example.com", config.Server);
        Assert.Equal("opendes", config.DataPartitionId);
    }

    [Fact]
    public void ExplainsHowToConfigureWhenNothingIsSet()
    {
        var missing = Path.Combine(_directory, "absent.json");

        var exception = Assert.Throws<OsduException>(() => CliConfig.Load(missing));

        // The message has to be actionable — this is the first thing a new user hits.
        Assert.Contains(missing, exception.Message);
        Assert.Contains("OSDU_SERVER", exception.Message);
    }

    [Fact]
    public void FlatEnvironmentAliasesAreAccepted()
    {
        // OSDU_SERVER rather than .NET's Osdu__Server, so the shell that drives the Python
        // CLI drives this one too.
        Environment.SetEnvironmentVariable("OSDU_SERVER", "https://from-env.example.com");
        Environment.SetEnvironmentVariable("OSDU_DATA_PARTITION_ID", "envpartition");
        Environment.SetEnvironmentVariable("OSDU_AUTHORITY", "https://login.example.com");
        Environment.SetEnvironmentVariable("OSDU_CLIENT_ID", "client");
        Environment.SetEnvironmentVariable("OSDU_SCOPES", "scope/.default");

        var config = CliConfig.Load(Path.Combine(_directory, "absent.json"));

        Assert.Equal("https://from-env.example.com", config.Server);
        Assert.Equal("envpartition", config.DataPartitionId);
    }

    [Fact]
    public void EnvironmentOverridesTheConfigFile()
    {
        var path = WriteConfig("https://from-file.example.com");
        Environment.SetEnvironmentVariable("OSDU_SERVER", "https://from-env.example.com");

        Assert.Equal("https://from-env.example.com", CliConfig.Load(path).Server);
    }

    [Fact]
    public void DefaultPathSitsBesideThePythonClisConfig()
    {
        // Both tools use ~/.osdu, so a machine configured for one is configured for both.
        Assert.Contains(".osdu", CliConfig.DefaultConfigPath);
        Assert.EndsWith("config.json", CliConfig.DefaultConfigPath);
    }

    // ---- username normalisation ---------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankUsernameIsNoSelectionAtAll(string? value)
    {
        // The dangerous case. `--user ""` is not null, so the ambiguity guard reads it as a
        // choice and stands down, while the MSAL provider trims it to null and falls through
        // to the first cached account — the silent guess coming back through the flag that
        // exists to prevent it.
        Assert.Null(CliConfig.NormaliseUsername(value));
    }

    [Theory]
    [InlineData("azure@equinor.com", "azure@equinor.com")]
    [InlineData("  azure@equinor.com  ", "azure@equinor.com")]
    [InlineData("\tazure@equinor.com\n", "azure@equinor.com")]
    public void PaddingIsStrippedSoEveryComparisonAgrees(string value, string expected)
    {
        // A padded value matches nothing in the cache, so `account list` would report the
        // account as not signed in while the provider, which trims, uses it happily.
        Assert.Equal(expected, CliConfig.NormaliseUsername(value));
    }

    [Fact]
    public void TheDefaultAccountIsReadFromUser()
    {
        // `user`, matching the `--user` flag. It was `username` for four days and a tester
        // wrote `user`, which was ignored in silence — the flag saying one thing and the
        // profile wanting another is the whole trap.
        var path = Path.Combine(_directory, "profile");
        File.WriteAllLines(path,
        [
            "[core]",
            "server = https://example.invalid",
            "data_partition_id = test",
            "authority = https://login.microsoftonline.com/tenant",
            "client_id = client",
            "scopes = https://example.invalid/.default",
            "user = azure@equinor.com",
        ]);

        CliConfig.Load(path, out var username);

        Assert.Equal("azure@equinor.com", username);
    }
}
