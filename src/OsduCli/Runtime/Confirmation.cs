using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// What a command says and asks before it does something that cannot be undone, or that
/// reaches across a whole data partition: deleting an index, re-indexing every kind.
/// </summary>
/// <remarks>
/// <para>COMMAND-GRAMMAR's R6: such operations are exposed behind a question rather than left
/// out. A manifest's <c>confirm:</c> generates the call; <c>record add</c> asks its own
/// question through <see cref="Ask"/>.</para>
///
/// <para>What is about to happen is said whichever way it goes, and so is where: the data
/// partition and server, since the same command against the wrong profile is the accident
/// that matters most. <c>--yes</c> skips the question, not the statement, so a script's log
/// still shows what it did. With nobody to ask, the command refuses, since guessing yes is
/// the accident this exists to prevent.</para>
/// </remarks>
public static class Confirmation
{
    /// <summary>Whether to go ahead with <paramref name="action"/> in the context's data partition.</summary>
    public static bool Confirm(string action, bool yes, CliContext context) =>
        Confirm(action, yes, $"Data partition {context.Config.DataPartitionId} on {context.Config.Server}.",
            context.Output, Console.IsInputRedirected ? null : Ask);

    /// <summary>
    /// Whether to go ahead with <paramref name="action"/> in <paramref name="where"/>: at once
    /// with <paramref name="yes"/>, otherwise when the person asked agrees.
    /// </summary>
    internal static bool Confirm(string action, bool yes, string where, OutputWriter output, Func<string, string?>? ask)
    {
        output.WriteNote(action);
        output.WriteNote(where);
        if (yes)
            return true;

        if (ask is null)
            throw new OsduException("This needs confirming, and there is no terminal to ask at. Pass --yes to go ahead.");

        if (ask("Go ahead? [y/N]")?.Trim().ToLowerInvariant() is "y" or "yes")
            return true;

        output.WriteNote("Nothing was done.");
        return false;
    }

    /// <summary>Asks on stderr, so stdout stays the result even when it is piped.</summary>
    public static string? Ask(string question)
    {
        Console.Error.Write(question + " ");
        return Console.ReadLine();
    }

    /// <summary>
    /// Values for a message, all of them when there are a few and the first few and a count
    /// otherwise: a thousand record ids would bury the question.
    /// </summary>
    public static string List(IReadOnlyCollection<string>? values, int shown = 3) =>
        values is null || values.Count == 0
            ? "none"
            : values.Count <= shown
                ? string.Join(", ", values)
                : $"{string.Join(", ", values.Take(shown))} and {values.Count - shown} more";
}
