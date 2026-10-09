using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The Storage commands its manifest cannot express.</summary>
public static partial class StorageCommands
{
    /// <summary>Adds <c>record add</c>, which the manifest lists under <c>handwritten</c>.</summary>
    static partial void Customize(CommandTree tree) =>
        tree.Node("record").Subcommands.Add(RecordAddCommand.Build());
}
