using System.CommandLine;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Explains a command line that names a command osducs does not have, and suggests the one
/// that was probably meant.
/// </summary>
/// <remarks>
/// Commands are a resource and then a verb, so adding someone to a group is
/// <c>osducs group member add</c>. A tester typed <c>osducs member add group</c>, the same words
/// in another order, and got two unhelpful answers. Without <c>--help</c>, the parser reported
/// that a command was missing and showed the help for <c>member group</c>, the deepest command
/// it had matched, which only lists <c>list</c>. With <c>--help</c> it was worse: the unknown
/// word was dropped without a word, so <c>osducs member add -h</c> showed the help for
/// <c>member</c> as if that were the answer, and exited 0.
///
/// <para>So the typed words are walked down the tree to find the first one that is not a
/// command where it appears. That is what gets reported, along with any existing command made
/// of the same words in another order, or failing that one containing all of them, or failing
/// that one a typo away. The help shown afterwards is for the command the bad word followed,
/// not wherever the parser happened to stop.</para>
///
/// <para>The typo step restores something COMMAND-GRAMMAR.md listed as already in place.
/// System.CommandLine's earlier betas suggested corrections; 2.0 does not, so
/// <c>osducs record serch</c> had been answered with "Unrecognized command" and nothing
/// more.</para>
/// </remarks>
public static class CommandSuggestions
{
    /// <summary>A word that is not a command where it was typed.</summary>
    /// <param name="Word">The word, as typed.</param>
    /// <param name="Parent">The command it followed, which has no subcommand of that name.</param>
    /// <param name="ParentPath">The words naming <paramref name="Parent"/>, empty for the root.</param>
    /// <param name="Suggestions">Full command lines to try instead, best first.</param>
    public sealed record Unknown(
        string Word, Command Parent, IReadOnlyList<string> ParentPath, IReadOnlyList<string> Suggestions);

    private const int MaxSuggestions = 3;

    /// <summary>
    /// The unknown word in <paramref name="args"/>, or null when every word naming the command
    /// exists, or when the line is not one this can judge.
    /// </summary>
    /// <remarks>
    /// Only the words before the first option are considered, since after it a word can be an
    /// option's value. A command that takes positional arguments ends the walk: a word after it,
    /// as in <c>osducs status storage</c>, is an argument rather than a command, and the parser
    /// already reports one that is wrong.
    /// </remarks>
    public static Unknown? Find(RootCommand root, IReadOnlyList<string> args)
    {
        var words = CommandWords(args);
        Command node = root;
        var path = new List<string>();

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var child = node.Subcommands.FirstOrDefault(
                command => command.Name == word || command.Aliases.Contains(word));
            if (child is null)
            {
                if (node.Arguments.Count > 0)
                    return null;
                var suggestions = Suggest(root, words);
                if (suggestions.Count == 0)
                    suggestions = Corrections(node, path, word, words.Skip(index + 1).ToList());
                return new Unknown(word, node, path, suggestions);
            }

            node = child;
            path.Add(word);
        }

        return null;
    }

    /// <summary>
    /// Existing commands made of the same words in another order, or failing that, the shortest
    /// commands that contain all of them.
    /// </summary>
    /// <remarks>
    /// Compared without regard to case: the parser is case-sensitive, so <c>osducs Record get</c>
    /// is an unknown command, and <c>osducs record get</c> is the obvious suggestion.
    /// </remarks>
    public static IReadOnlyList<string> Suggest(Command root, IReadOnlyList<string> words)
    {
        var wanted = words.Select(word => word.ToLowerInvariant()).ToList();
        var nodes = Paths(root, []).ToList();

        var reordered = nodes.Where(path => SameWords(path, wanted)).ToList();
        var candidates = reordered.Count > 0
            ? reordered
            : nodes.Where(path => path.Count > wanted.Count && ContainsAll(path, wanted))
                .OrderBy(path => path.Count)
                .ThenBy(path => string.Join(' ', path), StringComparer.Ordinal)
                .ToList();

        return candidates.Take(MaxSuggestions)
            .Select(path => "osducs " + string.Join(' ', path))
            .ToList();
    }

    /// <summary>
    /// Commands under <paramref name="parent"/> whose name is a typo away from
    /// <paramref name="word"/>, with the words typed after it kept when they still lead
    /// somewhere: <c>reocrd get</c> becomes <c>osducs record get</c>.
    /// </summary>
    /// <remarks>
    /// A short word is allowed one edit and a longer one two, counting a swap of neighbouring
    /// letters as one. More would suggest commands that only look alike by accident.
    /// </remarks>
    private static IReadOnlyList<string> Corrections(
        Command parent, IReadOnlyList<string> parentPath, string word, IReadOnlyList<string> rest)
    {
        var allowed = word.Length <= 4 ? 1 : 2;
        return parent.Subcommands
            .Where(command => !command.Hidden)
            .Select(command => (Command: command,
                Distance: EditDistance(word.ToLowerInvariant(), command.Name.ToLowerInvariant())))
            .Where(candidate => candidate.Distance > 0 && candidate.Distance <= allowed)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Command.Name, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(candidate => "osducs " + string.Join(' ',
                [.. parentPath, candidate.Command.Name, .. Reachable(candidate.Command, rest)]))
            .ToList();
    }

    /// <summary>The leading words of <paramref name="rest"/> that are subcommands in turn.</summary>
    private static IEnumerable<string> Reachable(Command command, IReadOnlyList<string> rest)
    {
        foreach (var word in rest)
        {
            var child = command.Subcommands.FirstOrDefault(c => c.Name == word);
            if (child is null)
                yield break;
            yield return word;
            command = child;
        }
    }

    /// <summary>
    /// Edits between two words — insertions, deletions, substitutions and swaps of neighbouring
    /// letters, each counting one.
    /// </summary>
    internal static int EditDistance(string first, string second)
    {
        var distance = new int[first.Length + 1, second.Length + 1];
        for (var i = 0; i <= first.Length; i++) distance[i, 0] = i;
        for (var j = 0; j <= second.Length; j++) distance[0, j] = j;

        for (var i = 1; i <= first.Length; i++)
        {
            for (var j = 1; j <= second.Length; j++)
            {
                var cost = first[i - 1] == second[j - 1] ? 0 : 1;
                distance[i, j] = Math.Min(Math.Min(
                    distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && first[i - 1] == second[j - 2] && first[i - 2] == second[j - 1])
                    distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
            }
        }

        return distance[first.Length, second.Length];
    }

    /// <summary>
    /// Writes the explanation to <paramref name="error"/> and the help for the command the
    /// unknown word followed to <paramref name="output"/>. Returns the exit code, 1, since
    /// nothing was run, even when help was what was asked for.
    /// </summary>
    public static int Report(Unknown unknown, TextWriter error, TextWriter output)
    {
        var parent = unknown.ParentPath.Count == 0
            ? "osducs"
            : "osducs " + string.Join(' ', unknown.ParentPath);
        error.WriteLine($"'{unknown.Word}' is not a command under `{parent}`.");

        switch (unknown.Suggestions.Count)
        {
            case 0:
                break;
            case 1:
                error.WriteLine($"Did you mean `{unknown.Suggestions[0]}`?");
                break;
            default:
                error.WriteLine("Did you mean one of these?");
                foreach (var suggestion in unknown.Suggestions)
                    error.WriteLine($"  {suggestion}");
                break;
        }

        output.WriteLine();
        CliHelp.Write(unknown.Parent, output);
        return 1;
    }

    /// <summary>The words before the first option, which are the ones naming a command.</summary>
    internal static IReadOnlyList<string> CommandWords(IReadOnlyList<string> args) =>
        args.TakeWhile(arg => !IsOption(arg)).ToList();

    // `/?` and `/h` are help aliases; any other word starting with `/` is a path, not an option.
    private static bool IsOption(string arg) => arg.StartsWith('-') || arg is "/?" or "/h";

    /// <summary>Every visible command below <paramref name="command"/>, as its words.</summary>
    private static IEnumerable<IReadOnlyList<string>> Paths(Command command, IReadOnlyList<string> prefix)
    {
        foreach (var child in command.Subcommands.Where(child => !child.Hidden))
        {
            IReadOnlyList<string> path = [.. prefix, child.Name];
            yield return path;
            foreach (var descendant in Paths(child, path))
                yield return descendant;
        }
    }

    private static bool SameWords(IReadOnlyList<string> path, IReadOnlyList<string> wanted) =>
        path.Count == wanted.Count && ContainsAll(path, wanted);

    /// <summary>True when <paramref name="path"/> has every word in <paramref name="wanted"/>, repeats included.</summary>
    private static bool ContainsAll(IReadOnlyList<string> path, IReadOnlyList<string> wanted)
    {
        var available = path.GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var word in wanted)
        {
            if (!available.TryGetValue(word, out var count) || count == 0)
                return false;
            available[word] = count - 1;
        }

        return true;
    }
}
