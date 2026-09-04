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

// Help sections. The generated ones come from `section:` in the manifests; the commands
// about the tool itself are grouped here because no manifest owns them.
CliHelp.Categorise(GeneratedCommands.Sections);
CliHelp.Categorise("status", "CLI");
CliHelp.Categorise("account", "CLI");
CliHelp.Categorise("completion", "CLI");

foreach (var command in GeneratedCommands.All())
    root.Subcommands.Add(command);

root.Subcommands.Add(StatusCommand.Build());
root.Subcommands.Add(AccountCommand.Build());

// Added after the rest of the tree: `osducs complete` parses against this same root, so
// everything it should be able to suggest has to be registered first.
foreach (var command in CompletionCommand.Build(root))
    root.Subcommands.Add(command);

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
