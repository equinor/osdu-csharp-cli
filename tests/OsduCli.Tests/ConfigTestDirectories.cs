using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Gives a test its own osducs and Python CLI config directories, and a clean environment.
/// </summary>
/// <remarks>
/// Both directories default to the home folder, and resolving the default config reads the
/// selection recorded in each. Without this, a test would read whatever profile the developer
/// running it last selected — and, once osducs writes its own selection, one run of
/// <c>osducs config use</c> on that machine would change what the suite sees. Tests using this
/// belong in <see cref="EnvironmentCollection"/>, since the variables are process-wide.
/// </remarks>
public abstract class ConfigTestDirectories : IDisposable
{
    private static readonly string[] Managed =
    [
        "OSDU_CONFIG_DIR", "OSDUCLI_CONFIG_DIR",
        "OSDU_SERVER", "OSDU_DATA_PARTITION_ID", "OSDU_AUTHORITY",
        "OSDU_CLIENT_ID", "OSDU_SCOPES", "OSDU_USER",
        "Osdu__Server", "Osdu__DataPartitionId", "Osdu__Authority",
        "Osdu__ClientId", "Osdu__Scopes", "Osdu__User",
    ];

    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "osducs-config-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stands in for <c>~/.osdu</c>.</summary>
    protected string Native => Path.Combine(_root, "osdu");

    /// <summary>Stands in for <c>~/.osducli</c>.</summary>
    protected string Python => Path.Combine(_root, "osducli");

    protected ConfigTestDirectories()
    {
        foreach (var name in Managed)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Directory.CreateDirectory(Native);
        Directory.CreateDirectory(Python);
        Environment.SetEnvironmentVariable("OSDU_CONFIG_DIR", Native);
        Environment.SetEnvironmentVariable("OSDUCLI_CONFIG_DIR", Python);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a complete Python CLI profile, plus any extra lines.</summary>
    protected string WritePythonProfile(string name, string server = "https://python.example.com",
        params string[] extra)
    {
        var path = Path.Combine(Python, name);
        File.WriteAllLines(path,
        [
            "[core]",
            $"server = {server}",
            "data_partition_id = pypartition",
            "authority = https://login.microsoftonline.com/tenant",
            "client_id = client",
            "scopes = https://example.com/.default openid",
            .. extra,
        ]);
        return path;
    }

    /// <summary>Writes a complete osducs JSON profile.</summary>
    protected string WriteNativeProfile(string name, string server = "https://native.example.com",
        string? user = null)
    {
        var path = Path.Combine(Native, name + ".json");
        var userLine = user is null ? "" : $",\n    \"User\": \"{user}\"";
        File.WriteAllText(path, $$"""
            {
              "Osdu": {
                "Server": "{{server}}",
                "DataPartitionId": "nativepartition",
                "Authority": "https://login.microsoftonline.com/tenant",
                "ClientId": "client",
                "Scopes": "https://example.com/.default openid"{{userLine}}
              }
            }
            """);
        return path;
    }

    /// <summary>Records a selection the way the Python CLI's <c>osdu config update</c> does.</summary>
    protected void SelectInPythonCli(string profilePath) =>
        File.WriteAllText(Path.Combine(Python, "state"), $"[core]\ndefault_config = {profilePath}\n");
}
