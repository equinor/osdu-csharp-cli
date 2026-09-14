using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Text;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Help output for every command in the tree.
/// </summary>
/// <remarks>
/// Two departures from the System.CommandLine default, both to keep what the Python CLI
/// gives users today:
///
/// <list type="number">
/// <item>Command options are separated from global ones, so <c>--kind</c> is not buried
/// among <c>--config</c> and <c>--debug</c>. The Python CLI calls the second group
/// "Common Options"; the name is kept so the two tools read the same.</item>
/// <item>Every alias is listed. The default formatter prints one form per option, which hid
/// <c>--id</c> even though it parses and even though the parser's own error messages name
/// it ("Option '--id' is required").</item>
/// <item>Positional arguments get their own section, and the usage line brackets the
/// optional ones — the default renders a required and an optional argument identically.</item>
/// </list>
///
/// Rendered by hand because <c>HelpBuilder</c>, <c>HelpContext</c> and
/// <c>TwoColumnHelpRow</c> are all internal in System.CommandLine 2.0.11 — only
/// <see cref="HelpOption"/> and its action are public. <c>HelpAction</c> is sealed, so the
/// action is replaced rather than subclassed; that loses its built-in clearing of parse
/// errors, which is why <see cref="WantsHelp"/> exists for Program.cs to check before
/// reporting them.
/// </remarks>
public static class CliHelp
{
    private const int Indent = 2;
    private const int Gap = 2;
    private const int MinDescriptionWidth = 20;

    /// <summary>
    /// Root nouns that render under a named heading instead of plain "Commands".
    /// </summary>
    /// <remarks>
    /// Grouping exists because a flat list stops being a list once it is long enough.
    /// Widening Wellbore DDMS to all nine of its record types put nine of twenty-two entries
    /// on the front page for a service that is not what most users came for, pushing
    /// `record`, `schema` and `search` down among them. Sections are declared by `section:`
    /// in a service manifest and flow through `GeneratedCommands.Sections`, so adding a
    /// tenth wellbore resource needs no change here.
    /// </remarks>
    private static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal);

    /// <summary>
    /// Heading for root entries no manifest claimed — the platform's own resources.
    /// </summary>
    /// <remarks>
    /// "Commands" until it was pointed out that nothing under it is one: every entry is a
    /// resource, and the command is a resource plus a verb. That heading was
    /// System.CommandLine's word for a subcommand rather than this CLI's word for what the
    /// reader is looking at, and R1 is explicit that these are nouns.
    ///
    /// "Core" rather than "OSDU" because the repo already uses it for exactly this set — the
    /// command surface minus Wellbore DDMS — and because it sets up the section below as
    /// adjacent rather than merely different.
    /// </remarks>
    internal const string DefaultSection = "Core resources";

    /// <summary>
    /// Heading for the entries on any page below the root — a noun's verbs and sub-nouns.
    /// </summary>
    /// <remarks>
    /// Its own constant because the root's heading is not a general one. Nested pages used to
    /// share <see cref="DefaultSection"/>, which was harmless while it read "Commands"; renaming
    /// it for the root then put <c>list</c>, <c>get</c> and <c>search</c> under "Core
    /// resources" on <c>osducs record --help</c>, and <c>osducs wellbore --help</c> announced
    /// its verbs as core resources of a service that is not core. On those pages the entries
    /// really are commands, so here the word fits.
    /// </remarks>
    internal const string NestedSection = "Commands";

    /// <summary>
    /// Heading for commands about the tool rather than the platform, rendered last.
    /// </summary>
    /// <remarks>
    /// Public because <see cref="Program"/> assigns it to the hand-written commands. A second
    /// copy of the string there is a bug waiting to happen: while these headings were being
    /// tried out, renaming the section here left those entries stranded in a section of their
    /// own, under the old name.
    ///
    /// The entries are deliberately not all of one grammatical kind — <c>account</c> takes a
    /// verb, <c>status</c> and <c>completion</c> do not. Grouping them by what they concern
    /// rather than by their shape is what lets every heading on the page answer the same
    /// question.
    /// </remarks>
    public const string ToolSection = "The CLI itself";

    public static void Categorise(IReadOnlyDictionary<string, string> sections)
    {
        foreach (var (name, section) in sections)
            Categories[name] = section;
    }

    public static void Categorise(string name, string section) => Categories[name] = section;

    public static void Install(RootCommand root)
    {
        foreach (var option in root.Options)
        {
            if (option is HelpOption help)
                help.Action = new Renderer();
        }
    }

    /// <summary>
    /// True when help was requested anywhere on the command line. Program.cs uses this to
    /// show help instead of parse errors, which the built-in help action did for us.
    /// </summary>
    public static bool WantsHelp(ParseResult parseResult) =>
        parseResult.Tokens.Any(token => token.Value is "--help" or "-h" or "-?" or "/?");

    public static void Write(Command command, TextWriter output)
    {
        var width = ConsoleWidth();

        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            output.WriteLine("Description:");
            foreach (var line in Wrap(command.Description, width - Indent))
                output.WriteLine(new string(' ', Indent) + line);
            output.WriteLine();
        }

        output.WriteLine("Usage:");
        output.WriteLine(new string(' ', Indent) + Usage(command));
        output.WriteLine();

        var options = VisibleOptions(command).ToList();
        var rows = new List<(string Left, string Right, string Section)>();

        foreach (var argument in command.Arguments.Where(a => !a.Hidden))
            rows.Add((ArgumentLabel(argument), argument.Description ?? string.Empty, "Arguments"));
        foreach (var option in options.Where(o => !IsCommon(o)))
            rows.Add((Label(option), option.Description ?? string.Empty, "Options"));
        foreach (var option in options.Where(IsCommon))
            rows.Add((Label(option), option.Description ?? string.Empty, "Common Options"));
        foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
            rows.Add((sub.Name, sub.Description ?? string.Empty, SectionFor(command, sub)));

        if (rows.Count == 0)
            return;

        // One column width across all sections so descriptions line up down the page.
        var leftWidth = rows.Max(row => row.Left.Length);

        // Named sections sort alphabetically between the default group and the tool
        // section, so the order is predictable without anyone maintaining a list.
        var named = rows.Select(row => row.Section)
            .Where(section => section is not ("Arguments" or "Options" or "Common Options"
                or DefaultSection or NestedSection or ToolSection))
            .Distinct()
            .OrderBy(section => section, StringComparer.Ordinal);

        var ordered = new[] { "Arguments", "Options", "Common Options", DefaultSection, NestedSection }
            .Concat(named)
            .Append(ToolSection);

        foreach (var section in ordered)
        {
            var inSection = rows.Where(row => row.Section == section).ToList();
            if (inSection.Count == 0)
                continue;

            output.WriteLine($"{section}:");
            foreach (var (left, right, _) in inSection)
                WriteRow(output, left, right, leftWidth, width);
            output.WriteLine();
        }
    }

    private sealed class Renderer : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            Write(parseResult.CommandResult.Command, Console.Out);
            return 0;
        }
    }

    private static void WriteRow(TextWriter output, string left, string right, int leftWidth, int width)
    {
        var pad = new string(' ', Indent);
        var descriptionWidth = Math.Max(MinDescriptionWidth, width - Indent - leftWidth - Gap);
        var lines = Wrap(right, descriptionWidth);

        if (lines.Count == 0)
        {
            output.WriteLine(pad + left);
            return;
        }

        output.WriteLine(pad + left.PadRight(leftWidth + Gap) + lines[0]);
        for (var i = 1; i < lines.Count; i++)
            output.WriteLine(pad + new string(' ', leftWidth + Gap) + lines[i]);
    }

    private static string Usage(Command command)
    {
        var names = new List<string> { command.Name };
        for (var parent = Parent(command); parent is not null; parent = Parent(parent))
            names.Insert(0, parent.Name);

        var usage = new StringBuilder(string.Join(' ', names));
        foreach (var argument in command.Arguments.Where(a => !a.Hidden))
        {
            var token = $"<{argument.Name}>";
            if (argument.Arity.MaximumNumberOfValues > 1)
                token += "...";
            usage.Append(IsRequired(argument) ? $" {token}" : $" [{token}]");
        }
        if (command.Subcommands.Any(c => !c.Hidden))
            usage.Append(" [command]");
        if (VisibleOptions(command).Any())
            usage.Append(" [options]");
        return usage.ToString();
    }

    private static bool IsRequired(Argument argument) =>
        argument.Arity.MinimumNumberOfValues > 0;

    private static string ArgumentLabel(Argument argument)
    {
        var label = $"<{argument.Name}>";
        if (argument.Arity.MaximumNumberOfValues > 1)
            label += "...";
        return IsRequired(argument) ? label + " (REQUIRED)" : label;
    }

    /// <summary>
    /// The heading <paramref name="sub"/> renders under on <paramref name="page"/>'s help.
    /// </summary>
    /// <remarks>
    /// Categories are looked up only on the root page. They are keyed by name, so consulting
    /// them anywhere else would file a nested command that happens to share a name with a
    /// root one under that root command's heading.
    /// </remarks>
    private static string SectionFor(Command page, Command sub) =>
        page is RootCommand
            ? Categories.GetValueOrDefault(sub.Name, DefaultSection)
            : NestedSection;

    private static Command? Parent(Command command) =>
        command.Parents.OfType<Command>().FirstOrDefault();

    /// <summary>Options on the command itself, plus recursive ones inherited from parents.</summary>
    private static IEnumerable<Option> VisibleOptions(Command command)
    {
        foreach (var option in command.Options)
        {
            if (!option.Hidden)
                yield return option;
        }

        for (var parent = Parent(command); parent is not null; parent = Parent(parent))
        {
            foreach (var option in parent.Options)
            {
                if (option.Recursive && !option.Hidden)
                    yield return option;
            }
        }
    }

    private static bool IsFlag(Option option) =>
        option is HelpOption or VersionOption;

    /// <summary>
    /// Whether an option belongs in "Common Options" rather than the command's own list.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="GlobalOptions.All"/> rather than enumerated here. The
    /// enumerated version fell behind the moment an option was added, and the symptom was
    /// subtle: help still rendered, the option still parsed, it was simply filed under the
    /// command's own flags.
    /// </remarks>
    private static bool IsCommon(Option option) =>
        option is HelpOption or VersionOption
        || GlobalOptions.All.Any(global => ReferenceEquals(global, option));

    private static string Label(Option option)
    {
        var label = new StringBuilder(Aliases(option));
        // HelpOption and VersionOption report a non-bool ValueType but take no value.
        if (option.ValueType != typeof(bool) && !IsFlag(option))
            label.Append($" <{option.Name.TrimStart('-')}>");
        if (option.Required)
            label.Append(" (REQUIRED)");
        return label.ToString();
    }

    /// <summary>Short forms first, then long, the way the Python CLI lists them.</summary>
    private static string Aliases(Option option)
    {
        var names = new List<string> { option.Name };
        names.AddRange(option.Aliases);
        return string.Join(", ", names
            .Distinct()
            .OrderBy(name => name.StartsWith("--", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Wraps to <paramref name="width"/>, honouring explicit line breaks in the text.
    /// </summary>
    /// <remarks>
    /// Splitting on spaces alone leaves a <c>\n</c> embedded inside a "word", so it is
    /// emitted mid-line and everything after it loses the caller's indent. Descriptions that
    /// want a deliberate break — the root command explaining the noun-verb grammar — need
    /// each line wrapped and indented separately.
    /// </remarks>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return lines;

        var paragraphs = text.Split('\n');
        if (paragraphs.Length > 1)
        {
            foreach (var paragraph in paragraphs)
                lines.AddRange(Wrap(paragraph, width));
            return lines;
        }

        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0)
            lines.Add(line.ToString());
        return lines;
    }

    private static int ConsoleWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 100 : Math.Clamp(Console.WindowWidth, 60, 120);
        }
        catch (IOException)
        {
            return 100;
        }
    }
}
