using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>osducs config add</c> — the way to set osducs up without the Python CLI's files, and the
/// way to migrate from them.
/// </summary>
[Collection(nameof(EnvironmentCollection))]
public class ConfigAddTests : ConfigTestDirectories
{
    private static readonly ProfileSettings Complete = new(
        "https://osdu.example.com", "dev", "https://login.microsoftonline.com/tenant",
        "client", "https://example.com/.default openid", null);

    private static readonly ProfileSettings Nothing = new(null, null, null, null, null, null);

    private static ConfigCommand.AddOutcome Add(
        string name, ProfileSettings? given = null, string? from = null, bool force = false,
        Func<string, string?>? ask = null) =>
        ConfigCommand.Add(new ConfigCommand.AddRequest(name, given ?? Complete, from, force), ask);

    private JsonObject Written(string name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Native, name + ".json")))!["Osdu"]!;

    [Fact]
    public void WritesAJsonProfileThatLoads()
    {
        var outcome = Add("dev");

        Assert.Equal(Path.Combine(Native, "dev.json"), outcome.Path);
        var config = CliConfig.Load("dev");
        Assert.Equal("https://osdu.example.com", config.Server);
        Assert.Equal("dev", config.DataPartitionId);
        Assert.Equal("https://example.com/.default openid", config.Scopes);
    }

    [Fact]
    public void TheDefaultAccountIsWrittenAsUserAndReadBack()
    {
        // `User`, matching --user. JSON profiles read `Username` before osducs wrote them.
        Add("dev", Complete with { User = "azure@equinor.com" });

        Assert.Equal("azure@equinor.com", Written("dev")["User"]!.GetValue<string>());
        Assert.Null(Written("dev")["Username"]);
        CliConfig.Load("dev", out var user);
        Assert.Equal("azure@equinor.com", user);
    }

    [Fact]
    public void NoUserKeyIsWrittenWhenNoneWasGiven()
    {
        Add("dev");

        Assert.Null(Written("dev")["User"]);
    }

    [Fact]
    public void AnExistingProfileIsNotReplacedWithoutForce()
    {
        Add("dev");

        var exception = Assert.Throws<OsduException>(() => Add("dev", Complete with { Server = "https://other.example.com" }));
        Assert.Contains("--force", exception.Message);
        Assert.Equal("https://osdu.example.com", CliConfig.Load("dev").Server);

        Add("dev", Complete with { Server = "https://other.example.com" }, force: true);
        Assert.Equal("https://other.example.com", CliConfig.Load("dev").Server);
    }

    [Fact]
    public void MissingSettingsNameTheirFlagsWhenNobodyCanBeAsked()
    {
        var exception = Assert.Throws<OsduException>(() =>
            Add("dev", Nothing with { Server = "https://osdu.example.com" }));

        Assert.Contains("--partition", exception.Message);
        Assert.Contains("--authority", exception.Message);
        Assert.Contains("--client-id", exception.Message);
        Assert.Contains("--scopes", exception.Message);
        Assert.DoesNotContain("--server", exception.Message);
        Assert.False(File.Exists(Path.Combine(Native, "dev.json")));
    }

    [Fact]
    public void OnlyWhatIsMissingIsAskedFor()
    {
        var asked = new List<string>();
        var answers = new Queue<string>(["https://login.microsoftonline.com/tenant", "client", "scope/.default"]);

        Add("dev", Nothing with { Server = "https://osdu.example.com", DataPartitionId = "dev" },
            ask: label => { asked.Add(label); return answers.Dequeue(); });

        Assert.Equal(3, asked.Count);
        Assert.StartsWith("Authority", asked[0]);
        Assert.Equal("scope/.default", CliConfig.Load("dev").Scopes);
    }

    [Fact]
    public void ABlankAnswerIsAskedAgain()
    {
        // An accidental Enter must not skip the setting and fail only after every other
        // value has been typed.
        var answers = new Queue<string>(["", "   ", "https://osdu.example.com", "dev",
            "https://login.microsoftonline.com/tenant", "client", "scope/.default"]);
        var asked = new List<string>();

        Add("dev", Nothing, ask: label => { asked.Add(label); return answers.Dequeue(); });

        Assert.Equal(7, asked.Count);
        Assert.All(asked.Take(3), label => Assert.StartsWith("Server", label));
        Assert.Equal("https://osdu.example.com", CliConfig.Load("dev").Server);
    }

    [Fact]
    public void TheEndOfInputStopsTheAskingAndNamesWhatIsMissing()
    {
        var answers = new Queue<string?>(["https://osdu.example.com", null]);

        var exception = Assert.Throws<OsduException>(() =>
            Add("dev", Nothing, ask: _ => answers.Count > 0 ? answers.Dequeue() : null));

        Assert.Contains("--partition", exception.Message);
        Assert.DoesNotContain("--server", exception.Message);
    }

    [Fact]
    public void FromCopiesAPythonProfileAndNamesWhatItLeftBehind()
    {
        WritePythonProfile("dev", "https://python.example.com",
            "user = azure@equinor.com", "storage_url = /api/storage/v2/",
            "acl_owner = data.default.owners@x", "authentication_mode = msal_interactive");

        var outcome = Add("dev", Nothing, from: "dev");

        var config = CliConfig.Load("dev", out var user);
        Assert.Equal("https://python.example.com", config.Server);
        Assert.Equal("pypartition", config.DataPartitionId);
        Assert.Equal("azure@equinor.com", user);
        Assert.Equal(Path.Combine(Python, "dev"), outcome.NotCarriedFrom);
        Assert.Equal(["storage_url", "acl_owner", "authentication_mode"], outcome.NotCarried);
    }

    [Fact]
    public void FromAJsonProfileLeavesNothingBehind()
    {
        WriteNativeProfile("dev");

        var outcome = Add("test", Nothing with { Server = "https://test.example.com", DataPartitionId = "test" }, from: "dev");

        Assert.Null(outcome.NotCarriedFrom);
        Assert.Empty(outcome.NotCarried);
    }

    [Fact]
    public void OptionsOverrideWhatIsCopied()
    {
        // The everyday case: a new environment on the same tenant needs only these two.
        WritePythonProfile("dev");

        Add("test", Nothing with { Server = "https://test.example.com", DataPartitionId = "test" }, from: "dev");

        var config = CliConfig.Load("test");
        Assert.Equal("https://test.example.com", config.Server);
        Assert.Equal("test", config.DataPartitionId);
        Assert.Equal("client", config.ClientId);
    }

    [Fact]
    public void CopyingIgnoresTheEnvironment()
    {
        // An OSDU_SERVER exported in the shell must not end up in the new profile as if the
        // source had said it.
        WritePythonProfile("dev", "https://from-file.example.com");
        Environment.SetEnvironmentVariable("OSDU_SERVER", "https://from-env.example.com");

        Add("copy", Nothing, from: "dev");

        Assert.Equal("https://from-file.example.com", Written("copy")["Server"]!.GetValue<string>());
    }

    [Fact]
    public void CopyingFromAProfileThatDoesNotExistFails()
    {
        var exception = Assert.Throws<OsduException>(() => Add("dev", Nothing, from: "nosuch"));

        Assert.Contains("nosuch", exception.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("state")]
    public void ANameThatCannotBeAProfileIsRefused(string name)
    {
        Assert.Throws<OsduException>(() => Add(name));
        Assert.Empty(Directory.EnumerateFiles(Native));
    }

    [Theory]
    [InlineData("osdu.example.com")]
    [InlineData("ftp://osdu.example.com")]
    public void TheServerMustBeAnAbsoluteHttpUrl(string server)
    {
        var exception = Assert.Throws<OsduException>(() => Add("dev", Complete with { Server = server }));

        Assert.Contains("--server", exception.Message);
    }

    [Fact]
    public void APartitionWithSpacesIsRefused()
    {
        Assert.Throws<OsduException>(() => Add("dev", Complete with { DataPartitionId = "my partition" }));
    }

    [Fact]
    public void TheFirstProfileIsSelectedWhenNothingElseIsConfigured()
    {
        var outcome = Add("dev");

        Assert.True(outcome.Selected);
        Assert.Equal("dev", CliConfig.NativeSelection());
    }

    [Fact]
    public void NothingIsSelectedWhenAnEnvironmentIsAlreadyInUse()
    {
        // Adding a profile must not move someone off the environment they are working in.
        SelectInPythonCli(WritePythonProfile("prod"));

        var outcome = Add("dev");

        Assert.False(outcome.Selected);
        Assert.Null(CliConfig.NativeSelection());
    }

    [Fact]
    public void MigratingTheSelectedPythonProfileIsReportedAsInUse()
    {
        SelectInPythonCli(WritePythonProfile("dev"));

        var outcome = Add("dev", Nothing, from: "dev");

        Assert.False(outcome.Selected);
        Assert.True(outcome.AlreadyInUse);
    }

    [Fact]
    public void ADefaultConfigBehindASelectionIsNotReportedAsInUse()
    {
        // config.json is always read, but a selected profile is layered over it, so creating
        // it while something is selected changes nothing about what osducs uses.
        SelectInPythonCli(WritePythonProfile("prod"));

        var outcome = Add("config");

        Assert.False(outcome.AlreadyInUse);
    }

    [Fact]
    public void NothingIsEverWrittenToThePythonClisDirectory()
    {
        var profile = WritePythonProfile("dev");
        SelectInPythonCli(profile);
        var before = Directory.EnumerateFiles(Python)
            .ToDictionary(file => file, File.ReadAllBytes);

        Add("dev", Nothing, from: "dev");
        Add("test", Complete);
        CliConfig.Select("test");

        var after = Directory.EnumerateFiles(Python).ToDictionary(file => file, File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (file, bytes) in before)
            Assert.Equal(bytes, after[file]);
    }

    // ---- listing ------------------------------------------------------------------------

    [Fact]
    public void ProfilesFromBothToolsAreListedWithOsducsFirstForTheSameName()
    {
        WritePythonProfile("dev");
        WritePythonProfile("prod");
        WriteNativeProfile("dev");

        var entries = ConfigCommand.Profiles().Select(e => (e.Name, e.Source)).ToList();

        Assert.Equal(
            [("dev", ConfigCommand.Source.Osducs), ("dev", ConfigCommand.Source.Python), ("prod", ConfigCommand.Source.Python)],
            entries);
    }

    [Fact]
    public void StateFilesAndTokenCachesAreNotListedAsProfiles()
    {
        WritePythonProfile("dev");
        SelectInPythonCli(Path.Combine(Python, "dev"));
        CliConfig.Select("dev");
        File.WriteAllText(Path.Combine(Native, "msal_cache.bin"), "{}");

        var names = ConfigCommand.Profiles().Select(e => e.Name).ToList();

        Assert.Equal(["dev"], names);
    }

    [Fact]
    public void AnUnreadableJsonProfileIsStillListed()
    {
        // A .json file in osducs's directory is meant as a profile; hiding a broken one would
        // leave someone wondering where it went.
        File.WriteAllText(Path.Combine(Native, "broken.json"), "{ not json");

        Assert.Contains(ConfigCommand.Profiles(), e => e.Name == "broken");
    }
}
