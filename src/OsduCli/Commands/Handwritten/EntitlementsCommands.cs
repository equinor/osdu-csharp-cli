using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The Entitlements commands its manifest cannot express.</summary>
public static partial class EntitlementsCommands
{
    /// <summary>
    /// Adds <c>--all</c> to <c>group list</c>: every group in the data partition, from another
    /// endpoint, which the manifest lists under <c>handwritten</c>.
    /// </summary>
    static partial void Customize(CommandTree tree) =>
        GroupListAll.Extend(tree.Node("group").Subcommands.Single(command => command.Name == "list"));
}
