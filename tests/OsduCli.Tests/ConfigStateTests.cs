using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Which profile is selected, and whose selection osducs follows.
/// </summary>
/// <remarks>
/// osducs keeps its selection in its own <c>~/.osdu/state.json</c>. It used to write the
/// Python CLI's <c>~/.osducli/state</c> instead, so switching environment here switched it
/// there too. Migration runs one way, from that tool to this one, so osducs reads that file —
/// following the other tool's choice until it has one of its own — and never writes it.
/// </remarks>
[Collection(nameof(EnvironmentCollection))]
public class ConfigStateTests : ConfigTestDirectories
{
    private string NativeState => Path.Combine(Native, "state.json");
    private string PythonState => Path.Combine(Python, "state");

    [Fact]
    public void SelectingRecordsTheProfileInOsducsOwnStateFile()
    {
        CliConfig.Select("dev");

        Assert.Contains("\"Profile\": \"dev\"", File.ReadAllText(NativeState));
        Assert.Equal("dev", CliConfig.NativeSelection());
    }

    [Fact]
    public void SelectingNeverTouchesThePythonClisState()
    {
        // The point of the change: choosing an environment in osducs must not move the
        // Python CLI to it.
        var before = WritePythonProfile("prod");
        SelectInPythonCli(before);
        var original = File.ReadAllBytes(PythonState);

        CliConfig.Select("dev");

        Assert.Equal(original, File.ReadAllBytes(PythonState));
    }

    [Fact]
    public void SwitchingReplacesTheSelection()
    {
        CliConfig.Select("dev");
        CliConfig.Select("test");

        Assert.Equal("test", CliConfig.NativeSelection());
    }

    [Fact]
    public void OtherKeysInTheStateFileSurvive()
    {
        // Written now so a later version can add to the file without this dropping it.
        File.WriteAllText(NativeState, """{ "Profile": "old", "SomethingElse": "keep me" }""");

        CliConfig.Select("dev");

        var text = File.ReadAllText(NativeState);
        Assert.Contains("keep me", text);
        Assert.Equal("dev", CliConfig.NativeSelection());
    }

    [Fact]
    public void AnUnreadableStateFileIsIgnoredRatherThanFatal()
    {
        File.WriteAllText(NativeState, "not json");

        Assert.Null(CliConfig.NativeSelection());
    }

    [Fact]
    public void ThePythonClisSelectionIsFollowedUntilOsducsHasItsOwn()
    {
        SelectInPythonCli(WritePythonProfile("dev", "https://python-selected.example.com"));

        Assert.Equal(CliConfig.SelectionOrigin.Python, CliConfig.Selection().Origin);
        Assert.Equal("https://python-selected.example.com", CliConfig.Load(null).Server);
    }

    [Fact]
    public void OsducsOwnSelectionWinsOverThePythonClis()
    {
        SelectInPythonCli(WritePythonProfile("dev", "https://python-selected.example.com"));
        WriteNativeProfile("test", "https://osducs-selected.example.com");

        CliConfig.Select("test");

        Assert.Equal(CliConfig.SelectionOrigin.Osducs, CliConfig.Selection().Origin);
        Assert.Equal("https://osducs-selected.example.com", CliConfig.Load(null).Server);
    }

    [Fact]
    public void ASelectionByNameFollowsWhicheverFileWinsForThatName()
    {
        // Select a profile while it only exists in the Python CLI, migrate it, and the
        // migrated copy is what is used — no second selection.
        WritePythonProfile("dev", "https://python.example.com");
        CliConfig.Select("dev");
        Assert.Equal("https://python.example.com", CliConfig.Load(null).Server);

        WriteNativeProfile("dev", "https://migrated.example.com");

        Assert.Equal("https://migrated.example.com", CliConfig.Load(null).Server);
    }

    [Fact]
    public void AMigratedProfileTakesOverFromThePythonClisSelectionToo()
    {
        // Otherwise `config add dev --from dev` would create a profile osducs went on ignoring
        // for as long as it followed the other tool's selection.
        SelectInPythonCli(WritePythonProfile("dev", "https://python.example.com"));
        WriteNativeProfile("dev", "https://migrated.example.com");

        Assert.Equal("https://migrated.example.com", CliConfig.Load(null).Server);
    }

    [Fact]
    public void APythonSelectionOutsideItsProfileDirectoryIsUsedAsAPath()
    {
        var elsewhere = Path.Combine(Native, "..", "elsewhere-profile");
        File.WriteAllLines(elsewhere,
        [
            "[core]", "server = https://elsewhere.example.com", "data_partition_id = p",
            "authority = https://login.example.com", "client_id = c", "scopes = s",
        ]);
        SelectInPythonCli(elsewhere);

        Assert.Equal("https://elsewhere.example.com", CliConfig.Load(null).Server);
    }

    // ---- selections that must not bring osducs down ---------------------------------------

    [Fact]
    public void ASelectionOfOnlySpacesIsNoSelection()
    {
        // Was read as a selection, resolved as a blank --config, which re-entered default
        // resolution and read it again: a stack overflow on every command. If this regresses
        // the test host dies with it, which is loud enough.
        File.WriteAllText(NativeState, """{ "Profile": "   " }""");

        Assert.Null(CliConfig.NativeSelection());
        Assert.Equal(2, CliConfig.Resolve(null).Count);
    }

    [Fact]
    public void APythonSelectionNamingItsOwnDirectoryDoesNotRecurse()
    {
        // `default_config = ~/.osducli/` has no file name. Treated as a profile name it was
        // blank, with the same endless recursion as above.
        SelectInPythonCli(Python + Path.DirectorySeparatorChar);

        Assert.Contains(Python + Path.DirectorySeparatorChar, CliConfig.Resolve(null));
    }

    // ---- file-system casing ---------------------------------------------------------------

    [Fact]
    public void PathsCompareTheWayThePlatformsFileSystemDoes()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        Assert.Equal(expected, CliConfig.PathComparison);
        Assert.True(CliConfig.SamePath(Python, Python + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AMigratedProfileTakesOverWhenThePythonStateSpellsTheDirectoryInAnotherCase()
    {
        // On a case-insensitive file system `.OSDUCLI` is `.osducli`. Compared exactly, the
        // selected Python profile looked like one from elsewhere, and the migrated JSON
        // profile beside it went unused.
        var profile = WritePythonProfile("dev", "https://python.example.com");
        var respelled = Path.Combine(Path.GetDirectoryName(Python)!, Path.GetFileName(Python).ToUpperInvariant(), "dev");
        Assert.SkipUnless(File.Exists(respelled), "needs a case-insensitive file system");
        SelectInPythonCli(respelled);
        WriteNativeProfile("dev", "https://migrated.example.com");

        Assert.Equal("https://migrated.example.com", CliConfig.Load(null).Server);
        Assert.True(File.Exists(profile));
    }
}
