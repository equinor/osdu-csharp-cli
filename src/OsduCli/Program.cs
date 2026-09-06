using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;

var root = new RootCommand(
    "osducs — command line for the OSDU platform.\n"
    + "Commands are <resource> <verb>, so verbs live under the thing they act on: "
    + "searching records is `osducs record search`. Run `osducs <resource> --help` to see them.");
GlobalOptions.AddTo(root);
CliHelp.Install(root);

// Generated commands carry their help section from `section:` in the manifests.
CliHelp.Categorise(GeneratedCommands.Sections);

foreach (var command in GeneratedCommands.All())
    root.Subcommands.Add(command);

// Hand-written commands are added and categorised together, because doing those separately
// is a bug waiting to happen — and did happen: `config` was added to the tree and left out of
// the section list, so it shipped in 0.6.0 listed among `record` and `schema` as though it
// were an OSDU resource. One loop means a command cannot be registered and then forgotten.
foreach (var command in new[]
         {
             StatusCommand.Build(),
             AccountCommand.Build(),
             ConfigCommand.Build(),
         })
{
    root.Subcommands.Add(command);
    CliHelp.Categorise(command.Name, CliHelp.ToolSection);
}

// Added after the rest of the tree: `osducs complete` parses against this same root, so
// everything it should be able to suggest has to be registered first. The hidden `complete`
// never reaches help, so categorising it is harmless and keeps this the same shape.
foreach (var command in CompletionCommand.Build(root))
{
    root.Subcommands.Add(command);
    CliHelp.Categorise(command.Name, CliHelp.ToolSection);
}

// `osducs` on its own is someone asking what this is, not a malformed command line.
// System.CommandLine treats it as a parse failure and prints "Required command was not
// provided." above the help, which reads as though something went wrong when nothing has.
// The help alone is the answer to the question actually being asked.
//
// The exit code stays non-zero. Nothing was run, and `osducs $cmd` with an empty variable
// reaches here as zero arguments — a script that silently succeeded there would be worse off
// than one that sees the help. `--help`, which is a request rather than an omission, still
// exits 0.
if (args.Length == 0)
{
    CliHelp.Write(root, Console.Out);
    return 1;
}

try
{
    var parseResult = root.Parse(args);

    // The custom help action replaces the built-in one, which cleared parse errors itself.
    // Without this, `osducs storage get --help` would report the missing --id instead of
    // showing help.
    if (parseResult.Errors.Count > 0 && CliHelp.WantsHelp(parseResult))
        return await parseResult.CommandResult.Command.Parse("--help").InvokeAsync();

    return await parseResult.InvokeAsync();
}
catch (OsduException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
