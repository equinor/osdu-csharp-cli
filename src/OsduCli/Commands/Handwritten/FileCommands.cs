using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The File commands its manifest cannot express.</summary>
public static partial class FileCommands
{
    /// <summary>
    /// Adds <c>file upload</c> and <c>file download</c>, which move a file's bytes through the
    /// signed URLs <c>file upload-url</c> and <c>file download-url</c> hand out.
    /// </summary>
    static partial void Customize(CommandTree tree)
    {
        tree.Node("file").Subcommands.Add(FileUploadCommand.Build());
        tree.Node("file").Subcommands.Add(FileDownloadCommand.Build());
    }
}
