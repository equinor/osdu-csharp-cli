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
            rows.Add((sub.Name, sub.Description ?? string.Empty, "Commands"));

        if (rows.Count == 0)
            return;

        // One column width across all sections so descriptions line up down the page.
        var leftWidth = rows.Max(row => row.Left.Length);

        foreach (var section in new[] { "Arguments", "Options", "Common Options", "Commands" })
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

    private static bool IsCommon(Option option) =>
        option is HelpOption or VersionOption
        || ReferenceEquals(option, GlobalOptions.Output)
        || ReferenceEquals(option, GlobalOptions.Config)
        || ReferenceEquals(option, GlobalOptions.Debug);

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

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return lines;

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
