using System.CommandLine;
using System.CommandLine.Completions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs config</c> — create profiles, see which environment is selected, and change it.
/// </summary>
/// <remarks>
/// Everything this writes lives in osducs's own directory, <c>~/.osdu/</c>: profiles as
/// <c>&lt;name&gt;.json</c> and the selection in <c>state.json</c>. The Python CLI's
/// <c>~/.osducli/</c> is read, so an existing user has nothing to set up, but never written.
/// Migration runs from that tool to this one, and each keeping to its own files is what stops
/// one from quietly changing the other — which <c>config use</c> did until it stopped writing
/// the Python CLI's <c>state</c>.
///
/// Hand-written: it reports on the CLI's own configuration rather than calling a service, and
/// runs without authenticating — you must be able to see and fix your settings when they are
/// wrong, which is exactly when authentication will not work.
/// </remarks>
public static class ConfigCommand
{
    public static Command Build()
    {
        var command = new Command("config", "Create, inspect and switch the environments osducs talks to.");
        command.Subcommands.Add(BuildAdd());
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildUse());
        command.Subcommands.Add(BuildShow());
        return command;
    }

    // ---- config add ---------------------------------------------------------------------

    private static Command BuildAdd()
    {
        var name = new Argument<string>("profile")
        {
            Description = "Name for the new profile. Written to ~/.osdu/<profile>.json.",
        };
        var server = new Option<string>("--server") { Description = "OSDU base URL, e.g. https://<instance>.energy.azure.com." };
        var partition = new Option<string>("--partition") { Description = "Data partition ID." };
        var authority = new Option<string>("--authority") { Description = "Entra ID authority, e.g. https://login.microsoftonline.com/<tenant-id>." };
        var clientId = new Option<string>("--client-id") { Description = "Application (client) ID of the app registration to sign in through." };
        var scopes = new Option<string>("--scopes") { Description = "OAuth scopes, space-separated." };
        var from = new Option<string>("--from")
        {
            Description = "Copy settings from an existing profile, osducs or Python. Options given alongside override what is copied.",
        };
        from.CompletionSources.Add(_ => Profiles().Select(p => p.Name).Distinct().Select(n => new CompletionItem(n)));
        var force = new Option<bool>("--force") { Description = "Replace a profile of the same name." };

        var add = new Command("add", "Create a profile. Prompts for any setting not given when run in a terminal. --user sets the profile's default account.")
        {
            name, server, partition, authority, clientId, scopes, from, force,
        };

        add.SetAction(parseResult => CliRunner.Run(parseResult, () =>
        {
            var request = new AddRequest(
                parseResult.GetValue(name)!,
                new ProfileSettings(
                    parseResult.GetValue(server), parseResult.GetValue(partition),
                    parseResult.GetValue(authority), parseResult.GetValue(clientId),
                    parseResult.GetValue(scopes),
                    // The global --user, read as the profile's default account: the same flag
                    // with the same meaning, remembered instead of used once.
                    CliConfig.NormaliseUsername(parseResult.GetValue(GlobalOptions.User))),
                parseResult.GetValue(from),
                parseResult.GetValue(force));

            // Prompting needs someone to answer. With input redirected, a missing setting is
            // an error naming the flag instead of a read that waits on nothing.
            var outcome = Add(request, Console.IsInputRedirected ? null : Ask);

            var output = Writer(parseResult);
            output.WriteMessage(
                $"Created {outcome.Path}: {outcome.Written.Server}, partition {outcome.Written.DataPartitionId}");
            if (outcome.NotCarried is { Count: > 0 } notCarried)
            {
                // Named rather than dropped silently: someone migrating should be able to see
                // that nothing they relied on went missing without being told.
                output.WriteNote(
                    $"Not copied from {outcome.NotCarriedFrom}, because osducs does not use them: "
                    + string.Join(", ", notCarried));
            }

            if (outcome.Selected)
                output.WriteNote($"Selected {request.Name}, since osducs had no other configuration to use.");
            else if (outcome.AlreadyInUse)
                output.WriteNote("osducs uses it from now on.");
            else
                output.WriteNote($"Select it with `osducs config use {request.Name}`, or use it once with `-c {request.Name}`.");
            output.WriteNote($"Check it works: osducs status -c {request.Name}");
            return 0;
        }));

        return add;
    }

    internal sealed record AddRequest(string Name, ProfileSettings Given, string? From, bool Force);

    internal sealed record AddOutcome(
        string Path, ProfileSettings Written, string? NotCarriedFrom, IReadOnlyList<string> NotCarried,
        bool Selected, bool AlreadyInUse);

    /// <summary>The Python profile keys osducs reads. Anything else in one is not copied.</summary>
    private static readonly HashSet<string> Carried = new(StringComparer.OrdinalIgnoreCase)
    {
        "server", "data_partition_id", "authority", "client_id", "scopes", "user",
    };

    /// <summary>
    /// Creates a JSON profile. Separated from the command so the rules can be tested without a
    /// console; <paramref name="ask"/> is null when nobody is there to answer a prompt.
    /// </summary>
    internal static AddOutcome Add(AddRequest request, Func<string, string?>? ask)
    {
        CheckName(request.Name);

        var target = CliConfig.NativeProfilePath(request.Name);
        if (File.Exists(target) && !request.Force)
            throw new OsduException($"{target} already exists. Pass --force to replace it.");

        // Decided before anything is written: after the write there is always a configuration.
        var nothingConfigured = CliConfig.Selection().Origin == CliConfig.SelectionOrigin.None
            && !CliConfig.Resolve(null).Any(File.Exists);

        var (seed, notCarriedFrom, notCarried) = Copy(request.From);
        var given = request.Given;
        var settings = new ProfileSettings(
            Blank(given.Server) ?? seed.Server,
            Blank(given.DataPartitionId) ?? seed.DataPartitionId,
            Blank(given.Authority) ?? seed.Authority,
            Blank(given.ClientId) ?? seed.ClientId,
            Blank(given.Scopes) ?? seed.Scopes,
            given.User ?? seed.User);

        settings = Complete(settings, ask);
        Validate(settings);

        Directory.CreateDirectory(CliConfig.NativeDirectory);
        File.WriteAllText(target, ToJson(settings));

        if (nothingConfigured)
            CliConfig.Select(request.Name);

        var inUse = !nothingConfigured && InEffect(target);

        return new AddOutcome(target, settings, notCarriedFrom, notCarried, nothingConfigured, inUse);
    }

    /// <summary>Whether osducs now reads <paramref name="file"/> by default, ahead of anything it replaced.</summary>
    /// <remarks>
    /// Not merely whether it is one of the files read: the two default files always are, and a
    /// selection is layered over them, so a new <c>config.json</c> is only in effect when
    /// nothing is selected.
    /// </remarks>
    private static bool InEffect(string file)
    {
        static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

        return CliConfig.Selection().Origin == CliConfig.SelectionOrigin.None
            ? Same(file, CliConfig.DefaultConfigPath)
            // Everything after the two defaults is the selection.
            : CliConfig.Resolve(null).Skip(2).Any(path => Same(path, file));
    }

    /// <summary>
    /// Profile names become file names, and are later typed after <c>-c</c> where anything with
    /// a separator is taken for a path, so both constrain them.
    /// </summary>
    private static void CheckName(string name)
    {
        if (name.Length == 0 || !char.IsAsciiLetterOrDigit(name[0])
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new OsduException(
                $"'{name}' cannot be a profile name. Use letters, digits, '-', '_' and '.', starting with a letter or digit.");
        }
        if (name.Equals("state", StringComparison.OrdinalIgnoreCase))
            throw new OsduException("'state' is reserved: ~/.osdu/state.json records which profile is selected.");
    }

    private static (ProfileSettings Seed, string? NotCarriedFrom, IReadOnlyList<string> NotCarried) Copy(string? from)
    {
        var empty = new ProfileSettings(null, null, null, null, null, null);
        if (from is null)
            return (empty, null, []);

        var files = CliConfig.Candidates(from).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            throw new OsduException(
                $"No profile named '{from}' to copy from. Run `osducs config list` to see what there is.");
        }

        // Only a Python profile carries keys osducs has no use for; a JSON profile holds the
        // six settings and nothing else.
        var python = files.FirstOrDefault(file => !CliConfig.IsJson(file));
        var notCarried = python is null
            ? []
            : OsduCliIniConfigurationProvider.Parse(File.ReadAllLines(python))
                .Select(pair => pair.Key)
                .Where(key => !Carried.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return (CliConfig.ReadFiles(files), python, notCarried);
    }

    private static readonly (string Label, Func<ProfileSettings, string?> Get, Func<ProfileSettings, string, ProfileSettings> With, string Flag)[] Required =
    [
        ("Server URL, e.g. https://<instance>.energy.azure.com", s => s.Server, (s, v) => s with { Server = v }, "--server"),
        ("Data partition ID", s => s.DataPartitionId, (s, v) => s with { DataPartitionId = v }, "--partition"),
        ("Authority, e.g. https://login.microsoftonline.com/<tenant-id>", s => s.Authority, (s, v) => s with { Authority = v }, "--authority"),
        ("Client ID of the app registration", s => s.ClientId, (s, v) => s with { ClientId = v }, "--client-id"),
        ("Scopes, space-separated, e.g. https://energy.azure.com/.default openid", s => s.Scopes, (s, v) => s with { Scopes = v }, "--scopes"),
    ];

    /// <summary>
    /// Asks for each required setting that is still missing, and fails naming the flags for
    /// any that remain. Settings already known are not asked for again, so
    /// <c>--from dev --server … --partition test</c> runs without a single prompt.
    /// </summary>
    /// <remarks>
    /// A blank answer asks again. Skipping to the next setting instead meant an accidental
    /// Enter on the first prompt was only reported after every other value had been typed, and
    /// then thrown away with them. Only the end of input — <paramref name="ask"/> returning
    /// null — stops the asking.
    /// </remarks>
    private static ProfileSettings Complete(ProfileSettings settings, Func<string, string?>? ask)
    {
        foreach (var (label, get, with, _) in Required)
        {
            while (get(settings) is null && ask?.Invoke(label) is { } answer)
            {
                if (Blank(answer) is { } value)
                    settings = with(settings, value);
            }
        }

        var missing = Required.Where(field => field.Get(settings) is null).Select(field => field.Flag).ToList();
        if (missing.Count > 0)
            throw new OsduException($"Missing {string.Join(", ", missing)}. Pass them, copy them with --from, or run in a terminal to be asked.");

        return settings;
    }

    /// <summary>
    /// Checked before writing, so a profile that cannot work is refused when it is created
    /// rather than discovered at the first sign-in.
    /// </summary>
    private static void Validate(ProfileSettings settings)
    {
        foreach (var (flag, value) in new[] { ("--server", settings.Server!), ("--authority", settings.Authority!) })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                throw new OsduException($"{flag} must be an absolute http or https URL, not '{value}'.");
        }
        foreach (var (flag, value) in new[] { ("--partition", settings.DataPartitionId!), ("--client-id", settings.ClientId!) })
        {
            if (value.Any(char.IsWhiteSpace))
                throw new OsduException($"{flag} cannot contain spaces: '{value}'.");
        }
    }

    private static string ToJson(ProfileSettings settings)
    {
        var osdu = new JsonObject
        {
            ["Server"] = settings.Server,
            ["DataPartitionId"] = settings.DataPartitionId,
            ["Authority"] = settings.Authority,
            ["ClientId"] = settings.ClientId,
            ["Scopes"] = settings.Scopes,
        };
        if (settings.User is not null)
            osdu["User"] = settings.User;

        return new JsonObject { [OsduConfig.DefaultSectionName] = osdu }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Ask(string label)
    {
        Console.Out.Write($"{label}: ");
        return Console.ReadLine();
    }

    // ---- config list --------------------------------------------------------------------

    private static Command BuildList()
    {
        var list = new Command("list", "List the available profiles, marking the selected one.");

        list.SetAction(parseResult => CliRunner.Run(parseResult, () =>
        {
            var output = Writer(parseResult);
            var entries = Profiles().ToList();
            if (entries.Count == 0)
            {
                output.Write("[]", Spec);
                output.WriteNote(
                    $"No profiles found in {CliConfig.NativeDirectory} or {CliConfig.ProfileDirectory}. "
                    + "Create one with `osducs config add <profile>`.");
                return 0;
            }

            var selected = SelectedFile();
            var native = entries.Where(e => e.Source == Source.Osducs).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var rows = new JsonArray();
            foreach (var entry in entries)
            {
                // Reading each profile costs a file parse, and buys the only thing that makes
                // a list of thirteen names useful: which environment each one points at.
                var settings = Read(entry.Path);
                rows.Add(new JsonObject
                {
                    ["profile"] = entry.Name,
                    // A Python profile with a JSON one of the same name is still listed, so the
                    // migration is visible, but marked so it is clear which one -c will read.
                    ["source"] = entry.Source == Source.Osducs ? "osducs"
                        : native.Contains(entry.Name) ? "python (overridden)" : "python",
                    ["server"] = settings?.Server ?? "(unreadable)",
                    ["partition"] = settings?.DataPartitionId ?? "",
                    ["selected"] = selected is not null
                        && Path.GetFullPath(selected) == Path.GetFullPath(entry.Path) ? "yes" : "",
                });
            }

            output.Write(rows.ToJsonString(), Spec);
            output.WriteNote(SelectionNote());
            return 0;
        }));

        return list;
    }

    private static readonly OutputSpec Spec = OutputSpec.Table(
        null, ("Profile", "profile"), ("Source", "source"), ("Server", "server"),
        ("Partition", "partition"), ("Selected", "selected"));

    // ---- config use ---------------------------------------------------------------------

    private static Command BuildUse()
    {
        var name = new Argument<string>("profile")
        {
            Description = "Profile to select, as named by `osducs config list`.",
        };
        name.CompletionSources.Add(_ => Profiles().Select(p => p.Name).Distinct().Select(n => new CompletionItem(n)));

        var use = new Command("use", "Select the profile osducs uses by default.")
        {
            name,
        };

        use.SetAction(parseResult => CliRunner.Run(parseResult, () =>
        {
            var requested = parseResult.GetValue(name)!;
            if (!CliConfig.Candidates(requested).Any(File.Exists))
            {
                throw new OsduException(
                    $"No profile named '{requested}'. Run `osducs config list` to see what there is.");
            }

            // Validated before it is recorded: selecting a profile that does not parse would
            // break every later command with an error pointing at the config rather than at
            // the moment the choice was made.
            CliConfig.Load(requested, out var username);
            var settings = CliConfig.ReadProfile(requested);

            // A name is recorded as a name, so the selection follows whichever file wins for
            // it; a path is recorded absolute, so it survives a change of directory.
            CliConfig.Select(CliConfig.LooksLikePath(requested) ? Path.GetFullPath(requested) : requested);

            var output = Writer(parseResult);
            output.WriteMessage($"Now using {requested}: {settings.Server}, partition {settings.DataPartitionId}");
            if (username is not null)
            {
                // Switching environment can switch identity, which is not obvious from the
                // name of a profile.
                output.WriteMessage($"Authenticating as {username}.");
            }
            if (CliConfig.PythonSelectedProfile() is not null)
                output.WriteNote("The Python CLI's selection is unchanged; osducs no longer follows it.");

            return 0;
        }));

        return use;
    }

    // ---- config show --------------------------------------------------------------------

    private static Command BuildShow()
    {
        var show = new Command("show", "Show the settings in effect and where they came from.");

        show.SetAction(parseResult => CliRunner.Run(parseResult, () =>
        {
            var requested = parseResult.GetValue(GlobalOptions.Config);
            var config = CliConfig.Load(requested, out var username);
            var output = Writer(parseResult);

            // One row per setting rather than one object, so table output reads as a list of
            // settings and JSON output stays a shape a script can iterate.
            var settings = new JsonArray();
            foreach (var (setting, value) in new[]
                     {
                         ("server", config.Server),
                         ("data-partition-id", config.DataPartitionId),
                         ("authority", config.Authority),
                         ("client-id", config.ClientId),
                         ("scopes", config.Scopes),
                         ("user", username ?? "(none — osducs will use the only signed-in account)"),
                     })
            {
                settings.Add(new JsonObject { ["setting"] = setting, ["value"] = value });
            }

            output.Write(settings.ToJsonString(), OutputSpec.Table(
                null, ("Setting", "setting"), ("Value", "value")));

            // Which files were consulted, in the order they are applied. Several sources feed
            // this and "which one won" is otherwise unanswerable without reading the code.
            foreach (var candidate in CliConfig.Candidates(requested))
            {
                var mark = File.Exists(candidate) ? "read" : "absent";
                output.WriteNote($"  [{mark}] {candidate}");
            }
            output.WriteNote("Later files win. Environment variables win over all of them.");
            if (string.IsNullOrWhiteSpace(requested))
                output.WriteNote(SelectionNote());
            return 0;
        }));

        return show;
    }

    // ---- shared -------------------------------------------------------------------------

    private static OutputWriter Writer(ParseResult parseResult) =>
        new(string.Equals(parseResult.GetValue(GlobalOptions.Output), "json",
                StringComparison.OrdinalIgnoreCase)
                ? OutputFormat.Json
                : OutputFormat.Table,
            Console.Out);

    internal enum Source { Osducs, Python }

    internal sealed record ProfileEntry(string Name, string Path, Source Source);

    /// <summary>Every profile, osducs's and the Python CLI's, by name with osducs's first.</summary>
    internal static IEnumerable<ProfileEntry> Profiles()
    {
        var entries = new List<ProfileEntry>();

        if (Directory.Exists(CliConfig.NativeDirectory))
        {
            // Every .json file here is meant as a profile, so an unreadable one is still
            // listed — hiding it would leave someone wondering where their profile went. The
            // directory also holds the token cache, which the extension already excludes.
            foreach (var file in Directory.EnumerateFiles(CliConfig.NativeDirectory, "*.json"))
            {
                if (!Path.GetFileName(file).Equals("state.json", StringComparison.OrdinalIgnoreCase))
                    entries.Add(new ProfileEntry(Path.GetFileNameWithoutExtension(file), file, Source.Osducs));
            }
        }

        if (Directory.Exists(CliConfig.ProfileDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(CliConfig.ProfileDirectory))
            {
                var name = Path.GetFileName(file);
                // `state` records the selection rather than being one, and the directory also
                // holds token caches. A file counts as a profile if it names a server.
                if (name == "state" || Read(file)?.Server is null)
                    continue;
                entries.Add(new ProfileEntry(name, file, Source.Python));
            }
        }

        return entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Source);
    }

    private static ProfileSettings? Read(string path)
    {
        try
        {
            return CliConfig.ReadFiles([path]);
        }
        catch (Exception exception) when (exception is IOException or FormatException
                                              or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file the selection resolves to — the one that wins for the selected name.</summary>
    private static string? SelectedFile() =>
        CliConfig.Selection().Origin == CliConfig.SelectionOrigin.None
            ? null
            // The two default files come first; everything after them is the selection.
            : CliConfig.Resolve(null).Skip(2).LastOrDefault(File.Exists);

    private static string SelectionNote() => CliConfig.Selection() switch
    {
        (CliConfig.SelectionOrigin.Osducs, var profile) when SelectedFile() is null =>
            $"The selected profile '{profile}' no longer exists; osducs falls back to its default config files.",
        (CliConfig.SelectionOrigin.Osducs, _) => "Selected with `osducs config use`.",
        (CliConfig.SelectionOrigin.Python, _) =>
            "Following the Python CLI's selection until `osducs config use` makes one of osducs's own.",
        _ => "No profile selected; osducs falls back to its default config files.",
    };
}
