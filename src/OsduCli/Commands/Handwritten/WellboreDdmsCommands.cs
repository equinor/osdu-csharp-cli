using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The Wellbore DDMS commands its manifest cannot express.</summary>
public static partial class WellboreDdmsCommands
{
    /// <summary>
    /// Adds <c>data add</c> and <c>data session send</c> to the four types with bulk data: their
    /// bodies are JSON or Parquet files, sent as they are. See <see cref="WellboreBulkWrite"/>.
    /// </summary>
    static partial void Customize(CommandTree tree) => WellboreBulkWrite.Extend(tree);
}
