using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The Indexer commands its manifest cannot express.</summary>
public static partial class IndexerCommands
{
    /// <summary>
    /// Adds <c>index gaps</c>, which compares a kind's records in Storage with what Search has
    /// indexed. It orchestrates Storage, Search and Indexer calls rather than wrapping one
    /// endpoint, so no manifest entry describes it.
    /// </summary>
    static partial void Customize(CommandTree tree) =>
        tree.Node("index").Subcommands.Add(IndexGapsCommand.Build());
}
