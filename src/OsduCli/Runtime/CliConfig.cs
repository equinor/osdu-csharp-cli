using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>Resolves and loads <see cref="OsduConfig"/> for the CLI.</summary>
/// <remarks>
/// Two config formats are read, because existing users already have the second one:
///
/// <list type="bullet">
/// <item>This CLI's own JSON profiles in <c>~/.osdu/</c> — <c>config.json</c> and
/// <c>&lt;name&gt;.json</c>. These are the only config files osducs ever writes.</item>
/// <item>The Python CLI's profiles — the INI files in <c>~/.osducli/</c>. Read, never
/// written: migration runs from that tool to this one, so its files are a source to copy
/// from rather than a place to keep settings. See <see cref="OsduCliIniConfigurationSource"/>
/// for what is and is not mapped.</item>
/// </list>
///
/// <para><c>--config</c> takes either. A value with no directory separator is treated as a
/// profile name and looked up in both locations, the JSON one winning, so a profile migrated
/// with <c>osducs config add dev --from dev</c> takes over from the Python profile it was
/// copied from without anything else changing. A value that is a path is used as given, and
/// its format is detected from its content rather than its extension — the Python profiles
/// have no extension at all.</para>
///
/// <para>Environment variables override files, in .NET's <c>Osdu__Server</c> form and in the
/// flatter <c>OSDU_SERVER</c> form Python CLI users already know.</para>
/// </remarks>
public static class CliConfig
{
    /// <summary>
    /// Where osducs keeps its own profiles and its selection. <c>OSDU_CONFIG_DIR</c> relocates
    /// it, the counterpart of the Python CLI's <c>OSDUCLI_CONFIG_DIR</c>.
    /// </summary>
    /// <remarks>
    /// Only the config files move. The MSAL token cache is the client library's, under its own
    /// <c>OSDU_MSAL_CACHE_PATH</c>.
    /// </remarks>
    public static string NativeDirectory =>
        Environment.GetEnvironmentVariable("OSDU_CONFIG_DIR") is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osdu");

    /// <summary>This CLI's own default config file.</summary>
    public static string DefaultConfigPath => NativeProfilePath("config");

    /// <summary>The file a native profile of this name lives in.</summary>
    internal static string NativeProfilePath(string name) =>
        Path.Combine(NativeDirectory, name + ".json");

    /// <summary>The file recording which profile osducs has selected.</summary>
    /// <remarks>
    /// JSON, and in the same directory as the profiles, so it is excluded from them by name;
    /// <c>config add</c> refuses to create a profile called <c>state</c> for the same reason.
    /// </remarks>
    internal static string NativeStatePath => Path.Combine(NativeDirectory, "state.json");

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

    /// <summary>The configuration key for the default account.</summary>
    /// <remarks>
    /// <c>User</c>, matching <c>--user</c> and the Python profile's <c>user</c>. JSON profiles
    /// read <c>Username</c> until osducs began writing them, which meant the one format this
    /// tool creates spelled it differently from the flag — the trap #27 removed from profiles.
    /// </remarks>
    internal const string UserKey = OsduConfig.DefaultSectionName + ":User";

    private static readonly Dictionary<string, string> EnvAliases = new()
    {
        ["OSDU_SERVER"] = "Osdu:Server",
        ["OSDU_DATA_PARTITION_ID"] = "Osdu:DataPartitionId",
        ["OSDU_AUTHORITY"] = "Osdu:Authority",
        ["OSDU_CLIENT_ID"] = "Osdu:ClientId",
        ["OSDU_SCOPES"] = "Osdu:Scopes",
        ["OSDU_USER"] = UserKey,
    };

    public static OsduConfig Load(string? config) => Load(config, out _);

    /// <summary>
    /// Loads the configuration, and reports the default account it names, if any.
    /// </summary>
    /// <remarks>
    /// The profile's <c>user</c> is not part of <see cref="OsduConfig"/> — the client models a service
    /// endpoint, not who is talking to it — so it comes back separately rather than being
    /// forced into a type that has no place for it.
    /// </remarks>
    public static OsduConfig Load(string? config, out string? username)
    {
        var candidates = Resolve(config);

        // Maps OSDU_SERVER -> "Osdu:Server". The tuple elements are named because the
        // obvious shorthand silently produced {envValue: envValue}, which built a
        // configuration full of nonsense keys and made these aliases do nothing.
        var aliased = EnvAliases
            .Select(alias => (
                ConfigKey: alias.Value,
                Value: Environment.GetEnvironmentVariable(alias.Key)))
            .Where(entry => !string.IsNullOrEmpty(entry.Value))
            .ToDictionary(entry => entry.ConfigKey, entry => entry.Value!);

        var configuration = FromFiles(candidates)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(aliased!)
            .Build();

        if (!configuration.GetSection(OsduConfig.DefaultSectionName).Exists())
            throw new OsduException(NothingFoundMessage(config, candidates));

        username = NormaliseUsername(configuration[UserKey]);

        return OsduConfig.FromConfiguration(configuration);
    }

    /// <summary>The settings a profile's files hold, before environment variables and validation.</summary>
    /// <remarks>
    /// For reading a profile as a thing in itself — listing it, or copying it with
    /// <c>config add --from</c>. <see cref="Load(string?, out string?)"/> is the wrong tool for
    /// that twice over: it applies the environment, so an <c>OSDU_SERVER</c> exported in the
    /// shell would be copied into a new profile as if the source had said it, and it rejects
    /// an incomplete profile, which is exactly the kind someone is trying to fix.
    /// </remarks>
    internal static ProfileSettings ReadProfile(string? config) => ReadFiles(Resolve(config));

    /// <inheritdoc cref="ReadProfile"/>
    internal static ProfileSettings ReadFiles(IEnumerable<string> paths)
    {
        var configuration = FromFiles(paths).Build();
        string? Value(string key) =>
            configuration[$"{OsduConfig.DefaultSectionName}:{key}"] is { Length: > 0 } value
                ? value
                : null;

        return new ProfileSettings(
            Value("Server"), Value("DataPartitionId"), Value("Authority"),
            Value("ClientId"), Value("Scopes"), NormaliseUsername(configuration[UserKey]));
    }

    private static IConfigurationBuilder FromFiles(IEnumerable<string> paths)
    {
        var builder = new ConfigurationBuilder();
        foreach (var path in paths)
        {
            if (IsJson(path))
                builder.AddJsonFile(path, optional: true);
            else
                builder.AddOsduCliProfile(path);
        }
        return builder;
    }

    /// <summary>
    /// Reduces a username to a value or to nothing, so every part of the CLI agrees on which
    /// is which.
    /// </summary>
    /// <remarks>
    /// <c>--user ""</c> is not a selection, but it is not null either. Left alone it reads as
    /// "a choice was made" to the ambiguity guard while the MSAL provider trims it back to
    /// null and falls through to the first cached account — reintroducing the silent guess
    /// this feature exists to prevent, through the flag meant to prevent it. Padding causes a
    /// milder version: the guard and <c>account list</c> compare the padded string against
    /// cached names and find no match.
    /// </remarks>
    internal static string? NormaliseUsername(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The files to read, lowest precedence first. Later sources win, so a native config
    /// overrides a Python profile of the same name.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(string? config)
    {
        // No --config: the default files, then whatever profile is selected. A selection is an
        // explicit act by the user, so it outranks either tool's unselected default file.
        if (string.IsNullOrWhiteSpace(config))
        {
            List<string> candidates = [Path.Combine(ProfileDirectory, "config"), DefaultConfigPath];
            var (origin, value) = Selection();
            if (origin == SelectionOrigin.Osducs)
                candidates.AddRange(Resolve(value));
            else if (origin == SelectionOrigin.Python)
                candidates.AddRange(PythonSelectionCandidates(value));
            return candidates;
        }

        // A path is used as given.
        if (LooksLikePath(config))
            return [config];

        // A bare name is a profile, looked up in both conventions.
        return [Path.Combine(ProfileDirectory, config), NativeProfilePath(config)];
    }

    /// <summary>The files the Python CLI's selection stands for.</summary>
    /// <remarks>
    /// That tool records a path. When the path is one of its own profiles it is read as that
    /// profile's name, so a JSON profile migrated from it takes over here exactly as it does
    /// for <c>-c</c> — otherwise <c>config add dev --from dev</c> would create a profile
    /// osducs went on ignoring for as long as it followed the other tool. A path elsewhere is
    /// used as it is.
    /// </remarks>
    private static IReadOnlyList<string> PythonSelectionCandidates(string selected)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(selected));
        return string.Equals(directory, Path.GetFullPath(ProfileDirectory).TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.Ordinal)
            ? Resolve(Path.GetFileName(selected))
            : [selected];
    }

    /// <summary>Which tool's selection is in effect.</summary>
    internal enum SelectionOrigin { None, Osducs, Python }

    /// <summary>
    /// The selection in effect: osducs's own when it has one, otherwise the Python CLI's.
    /// </summary>
    /// <remarks>
    /// Following the Python CLI's choice until osducs has made its own is what lets someone
    /// who already works in an environment start here without selecting it again. Once they
    /// run <c>osducs config use</c>, the two are independent: osducs stops following the other
    /// tool, and never changes what that tool has selected.
    /// </remarks>
    internal static (SelectionOrigin Origin, string Value) Selection() =>
        NativeSelection() is { } native ? (SelectionOrigin.Osducs, native)
        : PythonSelectedProfile() is { } python ? (SelectionOrigin.Python, python)
        : (SelectionOrigin.None, string.Empty);

    /// <summary>The profile osducs has selected — a profile name, or an absolute path — or null.</summary>
    /// <remarks>
    /// A name rather than a file, so the selection follows whichever file currently wins for
    /// that name: select <c>dev</c> while it is only a Python profile, migrate it, and the new
    /// JSON profile is what is in use without selecting anything again.
    /// </remarks>
    internal static string? NativeSelection()
    {
        if (!File.Exists(NativeStatePath)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(NativeStatePath))?["Profile"]?.GetValue<string>()
                is { Length: > 0 } profile
                ? profile
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or InvalidOperationException or FormatException)
        {
            // An unreadable state file is not a reason to fail; the other candidates stand.
            return null;
        }
    }

    /// <summary>Records <paramref name="profile"/> as osducs's selection.</summary>
    /// <remarks>
    /// Written to osducs's own state file only. This used to write the Python CLI's
    /// <c>~/.osducli/state</c>, which meant switching environment here switched it there too;
    /// now that migration runs one way, one tool quietly moving the other is interference.
    /// Other keys in the file are kept, so a later version can add to it without this
    /// dropping them.
    /// </remarks>
    internal static void Select(string profile)
    {
        JsonObject state;
        try
        {
            state = File.Exists(NativeStatePath)
                ? JsonNode.Parse(File.ReadAllText(NativeStatePath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            state = new JsonObject();
        }

        state["Profile"] = profile;
        Directory.CreateDirectory(NativeDirectory);
        File.WriteAllText(NativeStatePath,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// The profile the Python CLI currently has selected, or null. Read, never written.
    /// </summary>
    /// <remarks>
    /// <c>osdu config update</c> records the choice in <c>~/.osducli/state</c>:
    /// <code>
    /// [core]
    /// default_config = /Users/someone/.osducli/dev
    /// </code>
    /// The value is an absolute path, so a profile selected from outside
    /// <see cref="ProfileDirectory"/> still resolves.
    /// </remarks>
    internal static string? PythonSelectedProfile()
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

    /// <summary>
    /// Every file that could supply configuration, lowest precedence first, so a command can
    /// report what was consulted rather than leaving the user to infer it.
    /// </summary>
    internal static IReadOnlyList<string> Candidates(string? config) => Resolve(config);

    internal static bool LooksLikePath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        value.StartsWith('~');

    /// <summary>
    /// Detects JSON by content, not extension: the Python profiles have no extension, and a
    /// user pointing <c>--config</c> at one should not have to rename it.
    /// </summary>
    internal static bool IsJson(string path)
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
             + "Create a profile with `osducs config add <name>`, pass --config, or set "
             + "OSDU_SERVER, OSDU_DATA_PARTITION_ID, OSDU_AUTHORITY, OSDU_CLIENT_ID and "
             + "OSDU_SCOPES. "
             + AvailableProfiles();
    }

    /// <summary>Names the profiles that exist, so a typo is one line from being fixed.</summary>
    private static string AvailableProfiles()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        if (Directory.Exists(ProfileDirectory))
        {
            // `state` records which profile is selected; `.bin` files are token caches.
            // Neither is something you can pass to --config.
            foreach (var name in Directory.EnumerateFiles(ProfileDirectory).Select(Path.GetFileName))
                if (name is not null
                    && !name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("state", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
        }

        if (Directory.Exists(NativeDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(NativeDirectory, "*.json"))
                if (!Path.GetFileName(file).Equals("state.json", StringComparison.OrdinalIgnoreCase))
                    names.Add(Path.GetFileNameWithoutExtension(file));
        }

        return names.Count == 0 ? string.Empty : $"Available profiles: {string.Join(", ", names)}.";
    }
}

/// <summary>The six settings a profile can hold, any of which may be missing.</summary>
internal sealed record ProfileSettings(
    string? Server, string? DataPartitionId, string? Authority,
    string? ClientId, string? Scopes, string? User);
