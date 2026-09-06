using Equinor.OsduCli.Runtime;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Covers recording which profile is selected.
/// </summary>
/// <remarks>
/// The file is shared with the Python CLI's `osdu config update`. Writing it is how `osducs
/// config use` avoids inventing a second, competing notion of "current environment" — which
/// means it has to leave alone whatever else that tool keeps there.
/// </remarks>
public class ConfigStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "osdu-state-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous;

    public ConfigStateTests()
    {
        Directory.CreateDirectory(_directory);
        _previous = Environment.GetEnvironmentVariable("OSDUCLI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("OSDUCLI_CONFIG_DIR", _directory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OSDUCLI_CONFIG_DIR", _previous);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string State => Path.Combine(_directory, "state");

    [Fact]
    public void SelectingAProfileRecordsItsAbsolutePath()
    {
        CliConfig.SelectProfile(Path.Combine(_directory, "dev"));

        Assert.Contains($"default_config = {Path.Combine(_directory, "dev")}",
                        File.ReadAllText(State));
    }

    [Fact]
    public void SwitchingReplacesTheSelectionRatherThanAppending()
    {
        CliConfig.SelectProfile(Path.Combine(_directory, "dev"));
        CliConfig.SelectProfile(Path.Combine(_directory, "test"));

        var lines = File.ReadAllLines(State)
            .Where(l => l.TrimStart().StartsWith("default_config", StringComparison.Ordinal))
            .ToList();

        Assert.Single(lines);
        Assert.EndsWith("test", lines[0]);
    }

    [Fact]
    public void OtherKeysInTheFileSurvive()
    {
        // Dropping a key this tool does not recognise would be a silent way to break the
        // Python CLI, which owns the file as much as osducs does.
        File.WriteAllLines(State, ["[core]", "default_config = /old", "something_else = keep me"]);

        CliConfig.SelectProfile(Path.Combine(_directory, "dev"));

        var text = File.ReadAllText(State);
        Assert.Contains("something_else = keep me", text);
        Assert.DoesNotContain("/old", text);
    }

    [Fact]
    public void AMissingStateFileIsCreatedWithItsSection()
    {
        CliConfig.SelectProfile(Path.Combine(_directory, "dev"));

        Assert.StartsWith("[core]", File.ReadAllText(State));
    }

    [Fact]
    public void TheSelectionIsReadBackByTheSameCodeThatResolvesConfig()
    {
        var profile = Path.Combine(_directory, "dev");
        CliConfig.SelectProfile(profile);

        Assert.Equal(profile, CliConfig.SelectedProfile());
    }
}
