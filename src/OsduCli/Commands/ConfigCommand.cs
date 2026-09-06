using System.CommandLine;
using System.CommandLine.Completions;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs config</c> — see which environment is selected, and change it.
/// </summary>
/// <remarks>
/// The Python CLI has `osdu config update`, and `osducs` read the selection it wrote without
/// being able to write it. That was deliberate — two tools with competing ideas of "current
/// environment" is worse than one that only reads — but it quietly assumed the Python CLI
/// would be installed to do the writing. On a machine with only `osducs`, there was no way to
/// switch environment short of editing `~/.osducli/state` by hand.
///
/// So this writes that same file rather than introducing a second one. Both tools continue to
/// agree, whichever is installed.
///
/// Hand-written: it reports on the CLI's own configuration rather than calling a service, and
/// runs without authenticating — you must be able to see and fix your settings when they are
/// wrong, which is exactly when authentication will not work.
/// </remarks>
public static class ConfigCommand
{
    public static Command Build()
    {
        var command = new Command("config", "Inspect and switch the environment osducs talks to.");
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildUse());
        command.Subcommands.Add(BuildShow());
        return command;
    }

    // ---- config list --------------------------------------------------------------------

    private static Command BuildList()
    {
        var list = new Command("list", "List the available profiles, marking the selected one.");

        list.SetAction(parseResult => CliRunner.Run(parseResult, () =>
        {
            var selected = CliConfig.SelectedProfile();
            var rows = new JsonArray();

            foreach (var (name, path) in Profiles())
            {
                // Reading each profile costs a file parse, and buys the only thing that makes
                // a list of thirteen names useful: which environment each one points at.
                var summary = Describe(path);
                rows.Add(new JsonObject
                {
                    ["profile"] = name,
                    ["server"] = summary.Server ?? "(unreadable)",
                    ["partition"] = summary.Partition ?? "",
                    ["selected"] = selected is not null
                        && Path.GetFullPath(selected) == Path.GetFullPath(path) ? "yes" : "",
                });
            }

            var output = Writer(parseResult);
            if (rows.Count == 0)
            {
                output.Write("[]", Spec);
                output.WriteNote($"No profiles found in {CliConfig.ProfileDirectory}.");
                return 0;
            }

            output.Write(rows.ToJsonString(), Spec);
            if (selected is null)
                output.WriteNote("No profile selected; osducs falls back to its default config files.");
            return 0;
        }));

        return list;
    }

    private static readonly OutputSpec Spec = OutputSpec.Table(
        null, ("Profile", "profile"), ("Server", "server"),
        ("Partition", "partition"), ("Selected", "selected"));

    // ---- config use ---------------------------------------------------------------------

    private static Command BuildUse()
    {
        var name = new Argument<string>("profile")
        {
            Description = "Profile to select, as named by `osducs config list`.",
        };
        name.CompletionSources.Add(_ => Profiles().Select(p => new CompletionItem(p.Name)));

        var use = new Command("use", "Select the profile osducs uses by default.")
        {
            name,
        };

        use.SetAction(parseResult => CliRunner.Run(parseResult, () =>
        {
            var requested = parseResult.GetValue(name)!;
            var path = Locate(requested)
                ?? throw new OsduException(
                    $"No profile named '{requested}'. Run `osducs config list` to see what there is.");

            // Validated before it is recorded: selecting a profile that does not parse would
            // break every later command with an error pointing at the config rather than at
            // the moment the choice was made.
            CliConfig.Load(path, out var username);
            var summary = Describe(path);

            CliConfig.SelectProfile(path);

            var output = Writer(parseResult);
            output.WriteMessage($"Now using {requested}: {summary.Server}, partition {summary.Partition}");
            if (username is not null)
            {
                // Switching environment can switch identity, which is not obvious from the
                // name of a profile.
                output.WriteMessage($"Authenticating as {username}.");
            }

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
                         ("username", username ?? "(none — osducs will use the only signed-in account)"),
                     })
            {
                settings.Add(new JsonObject { ["setting"] = setting, ["value"] = value });
            }

            output.Write(settings.ToJsonString(), OutputSpec.Table(
                null, ("Setting", "setting"), ("Value", "value")));

            // Which files were consulted, in the order they are applied. Four sources feed
            // this and "which one won" is otherwise unanswerable without reading the code.
            foreach (var candidate in CliConfig.Candidates(requested))
            {
                var mark = File.Exists(candidate) ? "read" : "absent";
                output.WriteNote($"  [{mark}] {candidate}");
            }
            output.WriteNote("Later files win. Environment variables win over all of them.");
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

    /// <summary>Profiles in the Python CLI's directory, plus native JSON configs.</summary>
    private static IEnumerable<(string Name, string Path)> Profiles()
    {
        var directory = CliConfig.ProfileDirectory;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                // `state` records the selection rather than being one, and the directory also
                // holds a token cache. A file counts as a profile if it names a server.
                if (name == "state" || Describe(file).Server is null)
                    continue;
                yield return (name, file);
            }
        }
    }

    private static string? Locate(string requested)
    {
        foreach (var candidate in CliConfig.Candidates(requested))
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// The server and partition a profile names, or nulls when it is not a profile at all.
    /// </summary>
    private static (string? Server, string? Partition) Describe(string path)
    {
        try
        {
            CliConfig.Load(path, out _);
        }
        catch (OsduException)
        {
            // Incomplete or not a profile. Fall through and read what is there, so a profile
            // missing one required field still lists rather than vanishing.
        }

        try
        {
            string? server = null, partition = null;
            foreach (var (key, value) in
                     OsduCliIniConfigurationProvider.Parse(File.ReadAllLines(path)))
            {
                if (key.Equals("server", StringComparison.OrdinalIgnoreCase)) server = value;
                if (key.Equals("data_partition_id", StringComparison.OrdinalIgnoreCase)) partition = value;
            }
            return (server, partition);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }
}
