using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The Indexer commands its manifest cannot express.</summary>
public static partial class IndexerCommands
{
    /// <summary>
    /// Adds <c>index gaps</c>, which compares a kind's records in Storage with what Search has
    /// indexed, and <c>index status</c>, which counts per kind the records indexed with a
    /// problem. Both orchestrate several calls rather than wrapping one endpoint, so no manifest
    /// entry describes them.
    /// </summary>
    static partial void Customize(CommandTree tree)
    {
        var index = tree.Node("index");
        index.Subcommands.Add(IndexGapsCommand.Build());
        index.Subcommands.Add(IndexStatusCommand.Build());
    }
}
