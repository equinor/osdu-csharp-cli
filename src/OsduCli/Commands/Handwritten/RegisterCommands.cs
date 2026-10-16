using Equinor.OsduCli.Runtime;

namespace Equinor.OsduCli.Commands.Generated;

/// <summary>The Register commands its manifest cannot express.</summary>
public static partial class RegisterCommands
{
    /// <summary>
    /// Replaces what <c>subscription add</c>, <c>update-secret</c> and <c>find</c> do: their
    /// bodies carry a secret the client's models lose. See <see cref="SubscriptionSecrets"/>.
    /// </summary>
    static partial void Customize(CommandTree tree) => SubscriptionSecrets.Extend(tree.Node("subscription"));
}
