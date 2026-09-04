using Microsoft.Extensions.Configuration;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>Resolves and loads <see cref="OsduConfig"/> for the CLI.</summary>
/// <remarks>
/// Two config formats are read, because existing users already have the second one:
///
/// <list type="bullet">
/// <item>This CLI's own <c>~/.osdu/config.json</c>.</item>
/// <item>The Python CLI's profiles — the INI files in <c>~/.osducli/</c>. Reading them
/// directly means no migration step and both tools usable side by side. See
/// <see cref="OsduCliIniConfigurationSource"/> for what is and is not mapped.</item>
/// </list>
///
/// <para><c>--config</c> takes either. A value with no directory separator is treated as a
/// profile name and looked up in both locations, so <c>osducs -c dev</c> finds
/// <c>~/.osducli/dev</c> the way the Python CLI does. A value that is a path is used as
/// given, and its format is detected from its content rather than its extension — the
/// Python profiles have no extension at all.</para>
///
/// <para>Environment variables override files, in .NET's <c>Osdu__Server</c> form and in the
/// flatter <c>OSDU_SERVER</c> form Python CLI users already know.</para>
/// </remarks>
public static class CliConfig
{
    /// <summary>This CLI's own config file.</summary>
    public static string DefaultConfigPath => InOsduDirectory("config.json");

    /// <summary>
    /// Where the Python CLI keeps its profiles. <c>OSDUCLI_CONFIG_DIR</c> relocates it, the
    /// same variable the Python CLI reads, so a machine that has already moved its profiles
    /// does not have to move them twice.
    /// </summary>
    public static string ProfileDirectory =>
        Environment.GetEnvironmentVariable("OSDUCLI_CONFIG_DIR") is { Length: > 0 } directory
            ? directory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osducli");

    private static string InOsduDirectory(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osdu", name);

    private static readonly Dictionary<string, string> EnvAliases = new()
    {
        ["OSDU_SERVER"] = "Osdu:Server",
        ["OSDU_DATA_PARTITION_ID"] = "Osdu:DataPartitionId",
        ["OSDU_AUTHORITY"] = "Osdu:Authority",
        ["OSDU_CLIENT_ID"] = "Osdu:ClientId",
        ["OSDU_SCOPES"] = "Osdu:Scopes",
        ["OSDU_USERNAME"] = "Osdu:Username",
    };

    public static OsduConfig Load(string? config) => Load(config, out _);

    /// <summary>
    /// Loads the configuration, and reports the default account it names, if any.
    /// </summary>
    /// <remarks>
    /// <c>username</c> is not part of <see cref="OsduConfig"/> — the client models a service
    /// endpoint, not who is talking to it — so it comes back separately rather than being
    /// forced into a type that has no place for it.
    /// </remarks>
    public static OsduConfig Load(string? config, out string? username)
    {
        var candidates = Resolve(config);

        var builder = new ConfigurationBuilder();
        foreach (var candidate in candidates)
        {
            if (IsJson(candidate))
                builder.AddJsonFile(candidate, optional: true);
            else
                builder.AddOsduCliProfile(candidate);
        }

        // Maps OSDU_SERVER -> "Osdu:Server". The tuple elements are named because the
        // obvious shorthand silently produced {envValue: envValue}, which built a
        // configuration full of nonsense keys and made these aliases do nothing.
        var aliased = EnvAliases
            .Select(alias => (
                ConfigKey: alias.Value,
                Value: Environment.GetEnvironmentVariable(alias.Key)))
            .Where(entry => !string.IsNullOrEmpty(entry.Value))
            .ToDictionary(entry => entry.ConfigKey, entry => entry.Value!);

        var configuration = builder
            .AddEnvironmentVariables()
            .AddInMemoryCollection(aliased!)
            .Build();

        if (!configuration.GetSection(OsduConfig.DefaultSectionName).Exists())
            throw new OsduException(NothingFoundMessage(config, candidates));

        username = configuration[$"{OsduConfig.DefaultSectionName}:Username"];
        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();

        return OsduConfig.FromConfiguration(configuration);
    }

    /// <summary>
    /// The files to read, lowest precedence first. Later sources win, so a native config
    /// overrides a Python profile of the same name.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(string? config)
    {
        // No --config: fall back through the defaults, then honour whatever profile the
        // Python CLI currently has selected. That selection is an explicit act by the user,
        // so it outranks either tool's unselected default file.
        if (string.IsNullOrWhiteSpace(config))
        {
            List<string> candidates = [Path.Combine(ProfileDirectory, "config"), DefaultConfigPath];
            if (SelectedProfile() is { } selected)
                candidates.Add(selected);
            return candidates;
        }

        // A path is used as given.
        if (LooksLikePath(config))
            return [config];

        // A bare name is a profile, looked up in both conventions.
        return [Path.Combine(ProfileDirectory, config), InOsduDirectory(config + ".json")];
    }


    /// <summary>
    /// The profile the Python CLI currently has selected, or null.
    /// </summary>
    /// <remarks>
    /// <c>osdu config update</c> records the choice in <c>~/.osducli/state</c>:
    /// <code>
    /// [core]
    /// default_config = /Users/someone/.osducli/dev
    /// </code>
    /// Reading it means <c>osducs</c> follows the environment already selected instead of
    /// asking for <c>-c dev</c> on every command, and instead of inventing a second,
    /// competing notion of "current environment" that could silently disagree.
    ///
    /// The value is an absolute path, so a profile selected from outside
    /// <see cref="ProfileDirectory"/> still resolves.
    /// </remarks>
    internal static string? SelectedProfile()
    {
        var statePath = Path.Combine(ProfileDirectory, "state");
        if (!File.Exists(statePath)) return null;

        try
        {
            foreach (var (key, value) in
                     OsduCliIniConfigurationProvider.Parse(File.ReadAllLines(statePath)))
                if (key.Equals("default_config", StringComparison.OrdinalIgnoreCase))
                    return value;
        }
        catch (IOException)
        {
            // An unreadable state file is not a reason to fail; the other candidates stand.
        }
        return null;
    }

    private static bool LooksLikePath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        value.StartsWith('~');

    /// <summary>
    /// Detects JSON by content, not extension: the Python profiles have no extension, and a
    /// user pointing <c>--config</c> at one should not have to rename it.
    /// </summary>
    private static bool IsJson(string path)
    {
        if (!File.Exists(path))
            return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            return trimmed[0] == '{';
        }
        return false;
    }

    private static string NothingFoundMessage(string? config, IReadOnlyList<string> candidates)
    {
        var looked = string.Join(" or ", candidates);

        if (!string.IsNullOrWhiteSpace(config) && !LooksLikePath(config))
            return $"No OSDU configuration found for profile '{config}'. Looked in {looked}. "
                 + AvailableProfiles();

        return $"No OSDU configuration found. Looked in {looked}. "
             + "Create one with an \"Osdu\" section, pass --config, or set OSDU_SERVER, "
             + "OSDU_DATA_PARTITION_ID, OSDU_AUTHORITY, OSDU_CLIENT_ID and OSDU_SCOPES. "
             + AvailableProfiles();
    }

    /// <summary>Names the profiles that exist, so a typo is one line from being fixed.</summary>
    private static string AvailableProfiles()
    {
        if (!Directory.Exists(ProfileDirectory)) return string.Empty;

        var profiles = Directory.EnumerateFiles(ProfileDirectory)
            .Select(Path.GetFileName)
            // `state` records which profile is selected; `.bin` files are token caches.
            // Neither is something you can pass to --config.
            .Where(name => name is not null
                && !name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("state", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return profiles.Count == 0
            ? string.Empty
            : $"Available profiles: {string.Join(", ", profiles)}.";
    }
}
