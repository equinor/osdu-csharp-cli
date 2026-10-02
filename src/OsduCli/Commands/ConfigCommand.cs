using System.CommandLine;
using System.CommandLine.Completions;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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
        var mode = new Option<string>("--authentication-mode")
        {
            Description = $"How the profile signs in: {AuthenticationModes.Interactive}, as you through a browser (the default), "
                + $"or {AuthenticationModes.ClientCredentials}, as the application with a client secret. The secret is "
                + "copied with --from or asked for without being shown, never given as an option.",
        };
        mode.AcceptAnyCasingFromAmong(AuthenticationModes.Supported);
        var force = new Option<bool>("--force") { Description = "Replace a profile of the same name." };

        var add = new Command("add", "Create a profile. Prompts for any setting not given when run in a terminal. --user sets the profile's default account.")
        {
            name, server, partition, authority, clientId, scopes, mode, from, force,
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
                    CliConfig.NormaliseUsername(parseResult.GetValue(GlobalOptions.User)),
                    parseResult.GetValue(mode)),
                parseResult.GetValue(from),
                parseResult.GetValue(force));

            // Prompting needs someone to answer. With input redirected, a missing setting is
            // an error naming the flag instead of a read that waits on nothing.
            var outcome = Console.IsInputRedirected
                ? Add(request, ask: null)
                : Add(request, Ask, AskSecret);

            var output = Writer(parseResult);
            output.WriteMessage(
                $"Created {outcome.Path}: {outcome.Written.Server}, partition {outcome.Written.DataPartitionId}");
            if (AuthenticationModes.Parse(outcome.Written.AuthenticationMode) == SignInMethod.ClientCredentials)
            {
                output.WriteNote(outcome.Written.ClientSecret is null
                    ? $"Signs in as the application {outcome.Written.ClientId}. No client secret is stored, "
                      + "so set OSDU_CLIENT_SECRET when using it."
                    : $"Signs in as the application {outcome.Written.ClientId}, with the client secret stored "
                      + "in the profile, which only you can read.");
            }
            if (outcome.DroppedScopes is { Count: > 0 } dropped)
            {
                output.WriteNote(
                    $"Scopes reduced to {outcome.Written.Scopes}: an application signs in with its resource's "
                    + $"/.default alone, and Entra ID refuses {string.Join(" and ", dropped)} alongside it.");
            }
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
            EnvironmentNote(output);
            return 0;
        }));

        return add;
    }

    internal sealed record AddRequest(string Name, ProfileSettings Given, string? From, bool Force);

    internal sealed record AddOutcome(
        string Path, ProfileSettings Written, string? NotCarriedFrom, IReadOnlyList<string> NotCarried,
        bool Selected, bool AlreadyInUse, IReadOnlyList<string> DroppedScopes);

    /// <summary>The Python profile keys osducs reads. Anything else in one is not copied.</summary>
    private static readonly HashSet<string> Carried = new(StringComparer.OrdinalIgnoreCase)
    {
        "server", "data_partition_id", "authority", "client_id", "scopes", "user",
        "authentication_mode", "client_secret",
    };

    /// <summary>
    /// Creates a JSON profile. Separated from the command so the rules can be tested without a
    /// console; <paramref name="ask"/> is null when nobody is there to answer a prompt, and
    /// <paramref name="askSecret"/> asks without showing what is typed.
    /// </summary>
    internal static AddOutcome Add(
        AddRequest request, Func<string, string?>? ask, Func<string, string?>? askSecret = null)
    {
        CheckName(request.Name);

        var target = CliConfig.NativeProfilePath(request.Name);
        if (File.Exists(target) && !request.Force)
            throw new OsduException($"{target} already exists. Pass --force to replace it.");

        // Decided before anything is written: after the write there is always a configuration.
        // The environment counts — a machine set up entirely through OSDU_* variables is
        // configured, and saying otherwise while selecting the new profile would be untrue.
        // A selection counts only through its files, which Resolve already lists: a selection
        // whose profile has been deleted is not an environment anyone is on.
        var nothingConfigured = !CliConfig.Resolve(null).Any(File.Exists)
            && !LoadsFromEnvironment();

        var (seed, files, notCarriedFrom, notCarried) = Copy(request.From);
        var given = request.Given;
        var settings = new ProfileSettings(
            Blank(given.Server) ?? seed.Server,
            Blank(given.DataPartitionId) ?? seed.DataPartitionId,
            Blank(given.Authority) ?? seed.Authority,
            Blank(given.ClientId) ?? seed.ClientId,
            Blank(given.Scopes) ?? seed.Scopes,
            given.User ?? seed.User,
            Blank(given.AuthenticationMode) ?? seed.AuthenticationMode ?? AuthenticationModes.Interactive);

        var method = AuthenticationModes.Parse(settings.AuthenticationMode);
        if (method == SignInMethod.Unsupported)
        {
            throw new OsduException(
                $"'{request.From}' uses authentication mode '{settings.AuthenticationMode}', which osducs "
                + $"does not support. Pass --authentication-mode {AuthenticationModes.Interactive} or "
                + $"{AuthenticationModes.ClientCredentials} to choose one it does.");
        }
        if (method == SignInMethod.ClientCredentials && given.User is not null)
        {
            throw new OsduException(
                $"--user does not apply to {AuthenticationModes.ClientCredentials}, which signs in as the "
                + "application rather than an account.");
        }
        // In its usual spelling, whatever the profile it was copied from said.
        settings = settings with
        {
            AuthenticationMode = method == SignInMethod.ClientCredentials
                ? AuthenticationModes.ClientCredentials
                : AuthenticationModes.Interactive,
        };

        // Named rather than dropped silently, with the file it was in when that is not the one
        // the rest of the list came from.
        void LeaveBehind(string pythonKey, string jsonKey, Func<ProfileSettings, object?> value)
        {
            var file = files.Last(path => value(CliConfig.ReadFiles([path])) is not null);
            var key = CliConfig.IsJson(file) ? jsonKey : pythonKey;
            notCarriedFrom ??= file;
            notCarried = [.. notCarried, CliConfig.SamePath(notCarriedFrom, file) ? key : $"{key} (in {file})"];
        }

        // A browser profile's default account means nothing once it signs in as an application.
        // Copied, it was refused, and nothing on the command line could clear it; only --user,
        // which says it means something, is refused.
        if (method == SignInMethod.ClientCredentials && settings.User is not null)
        {
            LeaveBehind("user", "User", source => source.User);
            settings = settings with { User = null };
        }

        settings = Complete(settings, ask);
        Validate(settings);

        // A secret is copied only into a profile that signs in with it, from a profile that
        // signed in with it, for the same client ID. A browser profile's secret is a leftover
        // nothing has used, so switching its copy to an application sign-in promoted a value
        // nobody had checked; and given another client ID, it is that application's secret
        // that is wanted.
        var copiedSecret = method == SignInMethod.ClientCredentials
                           && AuthenticationModes.Parse(seed.AuthenticationMode) == SignInMethod.ClientCredentials
                           && settings.ClientId == seed.ClientId
            ? seed.ClientSecret
            : null;
        if (seed.ClientSecret is not null && copiedSecret is null)
            LeaveBehind("client_secret", "ClientSecret", source => source.ClientSecret);

        IReadOnlyList<string> droppedScopes = [];
        if (method == SignInMethod.ClientCredentials)
        {
            (var scopes, droppedScopes) = ApplicationScopes(settings.Scopes!);

            // Last, so nothing typed after it can be refused and the secret typed again.
            settings = settings with
            {
                Scopes = scopes,
                ClientSecret = copiedSecret ?? AskForSecret(askSecret, settings.ClientId!),
            };
        }

        Directory.CreateDirectory(CliConfig.NativeDirectory);
        WriteOwnerOnly(target, ToJson(settings));

        if (nothingConfigured)
            CliConfig.Select(request.Name);

        var inUse = !nothingConfigured && InEffect(target);

        return new AddOutcome(
            target, settings, notCarriedFrom, notCarried, nothingConfigured, inUse, droppedScopes);
    }

    /// <summary>
    /// OpenID Connect scopes, which ask about the person signing in and so mean nothing to an
    /// application.
    /// </summary>
    private static readonly HashSet<string> PersonScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openid", "profile", "email", "offline_access",
    };

    /// <summary>
    /// The scope an application signs in with, and the person scopes removed to get it.
    /// </summary>
    /// <remarks>
    /// Entra ID takes exactly one scope for a client-credentials sign-in: the resource's own
    /// <c>/.default</c>. Browser profiles commonly add <c>openid</c>, which it refuses there,
    /// so turning one into an application profile with <c>--authentication-mode</c> made a
    /// profile that failed at its first request. Person scopes are removed and said so;
    /// anything else that is not a single <c>/.default</c> is refused, since which resource
    /// was meant is not something to guess.
    /// </remarks>
    private static (string Scopes, IReadOnlyList<string> Dropped) ApplicationScopes(string scopes)
    {
        var all = scopes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var resources = all.Where(scope => !PersonScopes.Contains(scope)).ToList();
        if (resources is not [var resource] || !resource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            throw new OsduException(
                $"{AuthenticationModes.ClientCredentials} signs in with one scope, a resource's /.default such as "
                + $"https://energy.azure.com/.default, not '{scopes}'. Pass --scopes with the one meant.");
        }

        return (resource, all.Where(PersonScopes.Contains).ToList());
    }

    /// <summary>Whether the environment alone holds a configuration that loads.</summary>
    private static bool LoadsFromEnvironment()
    {
        try
        {
            CliConfig.Load(null);
            return true;
        }
        catch (OsduException)
        {
            return false;
        }
    }

    /// <summary>Whether osducs now reads <paramref name="file"/> by default, ahead of anything it replaced.</summary>
    /// <remarks>
    /// Not merely whether it is one of the files read: the two default files always are, and a
    /// selection is layered over them, so a new <c>config.json</c> is only in effect when
    /// nothing is selected.
    /// </remarks>
    private static bool InEffect(string file)
    {
        return CliConfig.SelectedFile() is null
            ? CliConfig.SamePath(file, CliConfig.DefaultConfigPath)
            // Everything after the two defaults is the selection.
            : CliConfig.Resolve(null).Skip(2).Any(path => CliConfig.SamePath(path, file));
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

        // Windows keeps these as device names whatever the extension, so `con.json` is not a
        // file it will create. Refused on every platform: a profile directory is the kind of
        // thing that gets copied to another machine.
        var stem = name.Split('.')[0];
        if (WindowsDeviceNames.Contains(stem))
            throw new OsduException($"'{name}' cannot be a profile name: '{stem}' is a reserved device name on Windows.");
    }

    private static readonly HashSet<string> WindowsDeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What <c>--from</c> copies, the files it read, lowest precedence first, and the keys it
    /// leaves behind.
    /// </summary>
    private static (ProfileSettings Seed, IReadOnlyList<string> Files, string? NotCarriedFrom, IReadOnlyList<string> NotCarried)
        Copy(string? from)
    {
        if (from is null)
            return (ProfileSettings.Empty, [], null, []);

        var files = CliConfig.Candidates(from).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            throw new OsduException(
                $"No profile named '{from}' to copy from. Run `osducs config list` to see what there is.");
        }

        // Only a Python profile carries keys osducs has no use for; a JSON profile holds the
        // profile settings and nothing else.
        var python = files.FirstOrDefault(file => !CliConfig.IsJson(file));
        var notCarried = python is null
            ? []
            : OsduCliIniConfigurationProvider.Parse(File.ReadAllLines(python))
                .Select(pair => pair.Key)
                .Where(key => !Carried.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // The mode and the secret come from the file that wins for this name, as they do when
        // the profile is used: a JSON profile does not inherit them from the Python profile it
        // overrides. See CliConfig.LoadWithSignIn.
        var winner = CliConfig.ReadFiles([files[^1]]);
        var settings = CliConfig.ReadFiles(files) with
        {
            AuthenticationMode = winner.AuthenticationMode,
            ClientSecret = winner.ClientSecret,
        };

        return (settings, files, python, notCarried);
    }

    /// <summary>
    /// Asks for the client secret, or returns null when nobody can be asked or nothing is
    /// typed: a profile without one gets it from <c>OSDU_CLIENT_SECRET</c> when it is used.
    /// </summary>
    private static ClientSecret? AskForSecret(Func<string, string?>? askSecret, string clientId) =>
        Blank(askSecret?.Invoke(
                $"Client secret for {clientId} (not shown; leave blank to give it in OSDU_CLIENT_SECRET instead)"))
            is { } secret
            ? new ClientSecret(secret)
            : null;

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

    /// <remarks>
    /// The mode is always written, even when it is the default. A profile without one is read
    /// as interactive, but saying so in the file means nobody has to know that.
    /// </remarks>
    private static string ToJson(ProfileSettings settings)
    {
        var osdu = new JsonObject
        {
            ["Server"] = settings.Server,
            ["DataPartitionId"] = settings.DataPartitionId,
            ["Authority"] = settings.Authority,
            ["ClientId"] = settings.ClientId,
            ["Scopes"] = settings.Scopes,
            ["AuthenticationMode"] = settings.AuthenticationMode,
        };
        if (settings.User is not null)
            osdu["User"] = settings.User;
        if (settings.ClientSecret is not null)
            osdu["ClientSecret"] = settings.ClientSecret.Value;

        return new JsonObject { [OsduConfig.DefaultSectionName] = osdu }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>
    /// Writes a profile that only its owner can read, from the moment it exists.
    /// </summary>
    /// <remarks>
    /// Every profile, not only those holding a secret: a profile replaced with <c>--force</c>
    /// may gain one, and restricting it after writing would leave a moment in which the secret
    /// was readable. An existing file is restricted before anything is written to it.
    /// </remarks>
    internal static void WriteOwnerOnly(string path, string contents)
    {
        using var stream = OperatingSystem.IsWindows() ? CreateOwnerOnlyOnWindows(path) : CreateOwnerOnlyOnUnix(path);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyOnUnix(string path)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (File.Exists(path))
            File.SetUnixFileMode(path, ownerOnly);

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = ownerOnly,
        });
    }

    /// <remarks>
    /// An access list naming only the current user, with nothing inherited from the folder.
    /// The folder's own permissions were trusted at first, which holds for the default under
    /// the user's profile but not for an <c>OSDU_CONFIG_DIR</c> pointing somewhere shared.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyOnWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));

        var file = new FileInfo(path);
        if (file.Exists)
            file.SetAccessControl(security);

        return file.Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None,
            bufferSize: 4096, FileOptions.None, security);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Ask(string label)
    {
        Console.Out.Write($"{label}: ");
        return Console.ReadLine();
    }

    /// <summary>Like <see cref="Ask"/>, without showing what is typed.</summary>
    /// <remarks>
    /// Typed rather than given as an option, so the secret stays out of shell history and the
    /// process list. The Python CLI's <c>config update</c> showed it as it was typed.
    /// </remarks>
    private static string? AskSecret(string label)
    {
        Console.Out.Write($"{label}: ");
        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Out.WriteLine();
                    return secret.ToString();
                case ConsoleKey.Backspace:
                    if (secret.Length > 0)
                        secret.Length--;
                    break;
                // Ctrl+D, as at any other prompt, ends the input.
                case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    Console.Out.WriteLine();
                    return secret.Length > 0 ? secret.ToString() : null;
                default:
                    if (!char.IsControl(key.KeyChar))
                        secret.Append(key.KeyChar);
                    break;
            }
        }
    }

    // ---- config list --------------------------------------------------------------------

    private static Command BuildList()
    {
        var list = new Command("list", "List the available profiles, marking the selected one.");

        list.SetAction(parseResult => CliRunner.Run(parseResult, () => List(Writer(parseResult))));

        return list;
    }

    /// <summary>Writes the profile list. Separate from the command so its notes can be tested.</summary>
    internal static int List(OutputWriter output)
    {
        var entries = Profiles().ToList();
        if (entries.Count == 0)
        {
            output.Write("[]", Spec);
            output.WriteNote(
                $"No profiles found in {CliConfig.NativeDirectory} or {CliConfig.ProfileDirectory}. "
                + "Create one with `osducs config add <profile>`.");
            // Having nothing to list is exactly when a selection pointing at a profile that
            // has gone, or variables configuring osducs without any file, need saying.
            if (CliConfig.Selection().Origin != CliConfig.SelectionOrigin.None)
                output.WriteNote(SelectionNote());
            EnvironmentNote(output);
            return 0;
        }

        var selected = CliConfig.SelectedFile();
        var native = entries.Where(e => e.Source == Source.Osducs).Select(e => e.Name).ToHashSet(CliConfig.NameComparer);
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
                ["selected"] = selected is not null && CliConfig.SamePath(selected, entry.Path) ? "yes" : "",
            });
        }

        output.Write(rows.ToJsonString(), Spec);
        output.WriteNote(SelectionNote());
        EnvironmentNote(output);
        return 0;
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

            // Validated before it is recorded: selecting a profile that does not parse, or
            // that signs in in a way osducs cannot, would break every later command with an
            // error pointing at the config rather than at the moment the choice was made.
            var config = CliConfig.LoadWithSignIn(requested, out var signIn);
            if (signIn.Method == SignInMethod.Unsupported)
                throw new OsduException(AuthenticationModes.UnsupportedMessage(signIn.Mode));
            var settings = CliConfig.ReadProfile(requested);

            // A name is recorded as a name, so the selection follows whichever file wins for
            // it; a path is recorded absolute, so it survives a change of directory.
            CliConfig.Select(CliConfig.LooksLikePath(requested) ? Path.GetFullPath(requested) : requested);

            var output = Writer(parseResult);
            output.WriteMessage($"Now using {requested}: {settings.Server}, partition {settings.DataPartitionId}");
            // Switching environment can switch identity, which is not obvious from the name of
            // a profile.
            if (signIn.Method == SignInMethod.ClientCredentials)
                output.WriteMessage($"Authenticating as the application {config.ClientId}, with a client secret.");
            else if (signIn.User is not null)
                output.WriteMessage($"Authenticating as {signIn.User}.");
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
            var config = CliConfig.LoadWithSignIn(requested, out var signIn);
            var output = Writer(parseResult);

            // One row per setting rather than one object, so table output reads as a list of
            // settings and JSON output stays a shape a script can iterate.
            var settings = new JsonArray();
            foreach (var (setting, value) in Rows(config, signIn))
                settings.Add(new JsonObject { ["setting"] = setting, ["value"] = value });

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
            foreach (var file in ReadableByOthers(CliConfig.Candidates(requested)))
            {
                output.WriteNote(
                    $"{file} holds a client secret that other users on this machine can read. "
                    + $"Restrict it with `chmod 600 {file}`.");
            }
            if (string.IsNullOrWhiteSpace(requested))
                output.WriteNote(SelectionNote());
            EnvironmentNote(output);
            return 0;
        }));

        return show;
    }

    /// <summary>The settings <c>config show</c> lists. Separate from the command so they can be tested.</summary>
    /// <remarks>
    /// The secret is never shown, only whether there is one. Its last characters would be
    /// enough to tell two secrets apart, but they are also a part of it, in a terminal's
    /// scrollback and in whatever a user pastes when asking for help.
    /// </remarks>
    internal static IEnumerable<(string Setting, string? Value)> Rows(OsduConfig config, SignInSettings signIn)
    {
        yield return ("server", config.Server);
        yield return ("data-partition-id", config.DataPartitionId);
        yield return ("authority", config.Authority);
        yield return ("client-id", config.ClientId);
        yield return ("scopes", config.Scopes);
        yield return ("authentication-mode", signIn.Method == SignInMethod.Unsupported
            ? $"{signIn.Mode} (not supported by osducs)"
            : signIn.Mode);

        if (signIn.Method == SignInMethod.ClientCredentials)
        {
            yield return ("client-secret", signIn.Secret is null
                ? "(not set — set OSDU_CLIENT_SECRET, or ClientSecret in the profile)"
                : "(set, hidden)");
            yield return ("user", signIn.User is null
                ? "(none — signs in as the application)"
                : $"{signIn.User} (not used — signs in as the application)");
        }
        else
        {
            yield return ("user", signIn.User ?? "(none — osducs will use the only signed-in account)");
        }
    }

    /// <summary>
    /// The files among <paramref name="files"/> that hold a client secret and that users other
    /// than their owner can read or change.
    /// </summary>
    /// <remarks>
    /// osducs writes its own profiles owner-only, so this mostly finds Python CLI profiles:
    /// that tool restricts a profile only when it writes one itself, and leaves a profile made
    /// any other way as it is. Windows is skipped; there a file in the user's profile folder
    /// is the user's unless someone has gone out of their way to share it.
    /// </remarks>
    internal static IEnumerable<string> ReadableByOthers(IEnumerable<string> files)
    {
        if (OperatingSystem.IsWindows())
            yield break;

        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        foreach (var file in files.Where(File.Exists))
        {
            if (Read(file)?.ClientSecret is not null && (File.GetUnixFileMode(file) & others) != 0)
                yield return file;
        }
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
                if (string.Equals(name, "state", CliConfig.PathComparison) || Read(file)?.Server is null)
                    continue;
                entries.Add(new ProfileEntry(name, file, Source.Python));
            }
        }

        return entries
            // Same comparison as the file system, so `Dev.json` sits beside the `dev` it overrides.
            .OrderBy(entry => entry.Name, CliConfig.NameComparer)
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
    /// <summary>
    /// Names any OSDU_* variables in effect. They override every profile, so a list of files
    /// and a selected marker say nothing reliable about what osducs will use while one is set.
    /// </summary>
    private static void EnvironmentNote(OutputWriter output)
    {
        if (CliConfig.EnvironmentOverrides() is { Count: > 0 } set)
            output.WriteNote($"Set in the environment, overriding any profile: {string.Join(", ", set)}.");
    }

    private static string SelectionNote() => CliConfig.Selection() switch
    {
        (CliConfig.SelectionOrigin.Osducs, var profile) when CliConfig.SelectedFile() is null =>
            $"The selected profile '{profile}' no longer exists; osducs falls back to its default config files.",
        (CliConfig.SelectionOrigin.Python, var profile) when CliConfig.SelectedFile() is null =>
            $"The Python CLI's selected profile {profile} no longer exists; osducs falls back to its default config files.",
        (CliConfig.SelectionOrigin.Osducs, _) => "Selected with `osducs config use`.",
        (CliConfig.SelectionOrigin.Python, _) =>
            "Following the Python CLI's selection until `osducs config use` makes one of osducs's own.",
        _ => "No profile selected; osducs falls back to its default config files.",
    };
}
