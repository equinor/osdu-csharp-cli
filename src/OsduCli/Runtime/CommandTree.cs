using System.CommandLine;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// The global command tree, assembled from every service manifest.
/// </summary>
/// <remarks>
/// Commands are named for the resource, not the service that hosts it
/// (see COMMAND-GRAMMAR.md), and those two things do not line up one-to-one:
///
/// <list type="bullet">
/// <item>One service can own several nouns — Entitlements supplies both <c>osdu group</c>
/// and <c>osdu member</c>, which are the same relation viewed from either end.</item>
/// <item>Several services can share one noun — <c>osdu crs</c> is CRS Catalog and CRS
/// Conversion together, and <c>osdu record search</c> is the Search service sitting beside
/// Storage's <c>record list</c>.</item>
/// </list>
///
/// So the tree cannot be a property of any one manifest. Each generated service class
/// attaches its leaves to paths in this shared tree; group nodes are created on demand and
/// described once, globally. The generator checks for duplicate leaves and conflicting
/// descriptions before emitting, so a collision is a build failure rather than whichever
/// service happened to be registered last.
/// </remarks>
public sealed class CommandTree
{
    private readonly Dictionary<string, Command> _nodes = new(StringComparer.Ordinal);
    private readonly List<Command> _roots = [];

    /// <summary>Group descriptions by full path, e.g. <c>"record version"</c>.</summary>
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.Ordinal);

    /// <summary>Records the help text for a group node, before it is created.</summary>
    public void Describe(string path, string description) => _descriptions[path] = description;

    /// <summary>
    /// Returns the group command at <paramref name="path"/>, creating it and any missing
    /// ancestors. <paramref name="path"/> is space-separated and excludes <c>osdu</c>.
    /// </summary>
    public Command Node(string path)
    {
        if (_nodes.TryGetValue(path, out var existing))
            return existing;

        var segments = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var node = new Command(segments[^1], _descriptions.GetValueOrDefault(path, string.Empty));
        _nodes[path] = node;

        if (segments.Length == 1)
            _roots.Add(node);
        else
            Node(string.Join(' ', segments[..^1])).Subcommands.Add(node);

        return node;
    }

    /// <summary>Top-level commands, in name order, each with its subtree ordered.</summary>
    public IReadOnlyList<Command> Roots
    {
        get
        {
            foreach (var node in _nodes.Values)
                Sort(node);
            return _roots.OrderBy(root => root.Name, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Canonical verb order for help output. A group's commands arrive in whatever order
    /// the manifests were read — `record` is filled by two services — and "whichever file
    /// sorted first" is not an order a user can learn. Reading and finding come before
    /// changing, changing before destroying, and destructive verbs sit at the bottom where
    /// they are hard to hit by accident.
    /// </summary>
    private static readonly string[] VerbOrder =
    [
        "list", "get", "search", "query", "info",
        "add", "create", "update", "patch", "upload", "download",
        "trigger", "run", "convert", "validate",
        "delete", "purge", "revoke",
    ];

    private static void Sort(Command node)
    {
        var ordered = node.Subcommands
            .OrderBy(child => child.Subcommands.Count > 0 ? 1 : 0)   // groups last
            .ThenBy(child => Array.IndexOf(VerbOrder, child.Name) is var rank && rank >= 0
                ? rank
                : VerbOrder.Length)
            .ThenBy(child => child.Name, StringComparer.Ordinal)
            .ToList();

        if (ordered.SequenceEqual(node.Subcommands))
            return;

        node.Subcommands.Clear();
        foreach (var child in ordered)
            node.Subcommands.Add(child);
    }
}
