using System.CommandLine;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;

var root = new RootCommand("osdu — command line for the OSDU platform.");
GlobalOptions.AddTo(root);
CliHelp.Install(root);

foreach (var command in GeneratedCommands.All())
    root.Subcommands.Add(command);

root.Subcommands.Add(StatusCommand.Build());

// Added after the rest of the tree: `osdu complete` parses against this same root, so
// everything it should be able to suggest has to be registered first.
foreach (var command in CompletionCommand.Build(root))
    root.Subcommands.Add(command);

try
{
    var parseResult = root.Parse(args);

    // The custom help action replaces the built-in one, which cleared parse errors itself.
    // Without this, `osdu storage get --help` would report the missing --id instead of
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
