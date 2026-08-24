using Microsoft.Extensions.Configuration;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>Loads <see cref="OsduConfig"/> for the CLI.</summary>
/// <remarks>
/// Reads <c>~/.osdu/config.json</c> — the same directory the Python CLI uses for its own
/// config and the same one <c>MsalInteractiveTokenProvider</c> already writes its token
/// cache into, so the two tools stay co-located on both Windows and macOS.
/// Environment variables override the file, both in .NET's <c>Osdu__Server</c> form and in
/// the flatter <c>OSDU_SERVER</c> form the Python CLI users already know.
/// </remarks>
public static class CliConfig
{
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osdu", "config.json");

    private static readonly Dictionary<string, string> EnvAliases = new()
    {
        ["OSDU_SERVER"] = "Osdu:Server",
        ["OSDU_DATA_PARTITION_ID"] = "Osdu:DataPartitionId",
        ["OSDU_AUTHORITY"] = "Osdu:Authority",
        ["OSDU_CLIENT_ID"] = "Osdu:ClientId",
        ["OSDU_SCOPES"] = "Osdu:Scopes",
    };

    public static OsduConfig Load(string? configPath)
    {
        var path = configPath ?? DefaultConfigPath;

        var aliased = EnvAliases
            .Select(pair => (pair.Value, Value: Environment.GetEnvironmentVariable(pair.Key)))
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .ToDictionary(pair => pair.Value!, pair => pair.Item2!);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(aliased!)
            .Build();

        if (!configuration.GetSection(OsduConfig.DefaultSectionName).Exists())
            throw new OsduException(
                $"No OSDU configuration found. Create {path} with an \"Osdu\" section, " +
                "or set OSDU_SERVER, OSDU_DATA_PARTITION_ID, OSDU_AUTHORITY, OSDU_CLIENT_ID and OSDU_SCOPES.");

        return OsduConfig.FromConfiguration(configuration);
    }
}
