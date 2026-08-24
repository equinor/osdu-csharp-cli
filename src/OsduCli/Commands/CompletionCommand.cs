using System.CommandLine;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osdu completion &lt;shell&gt;</c> prints a registration script; the hidden
/// <c>osdu complete</c> is what that script calls on every Tab.
/// </summary>
/// <remarks>
/// System.CommandLine can already answer "what could come next here" through
/// <see cref="ParseResult.GetCompletions"/>, but nothing connects that to a shell. The
/// documented route is the <c>dotnet-suggest</c> global tool, which means asking every user
/// to install a second tool and register this one with it — not a reasonable ask for an
/// enterprise rollout, and on Windows it adds another executable for WDAC to allow.
///
/// So the CLI answers for itself: the shell hands the words typed so far to
/// <c>osdu complete</c>, which parses them and prints one candidate per line. That contract
/// is simple enough to express in all four shells and depends on nothing but this binary.
/// Completion needs no configuration, no network and no token — Tab must never authenticate.
/// </remarks>
public static class CompletionCommand
{
    private static readonly string[] Shells = ["bash", "zsh", "fish", "powershell"];

    public static IEnumerable<Command> Build(RootCommand root)
    {
        yield return BuildCompletion();
        yield return BuildComplete(root);
    }

    private static Command BuildCompletion()
    {
        var shell = new Argument<string>("shell")
        {
            Description = "Shell to emit a registration script for.",
        };
        shell.AcceptOnlyFromAmong(Shells);

        var command = new Command("completion", "Print a shell completion script.")
        {
            shell,
        };

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(shell)!;
            Console.Out.Write(ScriptFor(name));
            return 0;
        });

        return command;
    }

    /// <summary>
    /// The hidden worker. Takes everything typed so far after <c>--</c> and prints the
    /// candidates for the final word, one per line.
    /// </summary>
    private static Command BuildComplete(RootCommand root)
    {
        var words = new Argument<string[]>("words")
        {
            Description = "The command line typed so far, excluding the program name.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("complete", "Emit completion candidates. Used by the shell.")
        {
            Hidden = true,
        };
        command.Arguments.Add(words);

        command.SetAction(parseResult =>
        {
            foreach (var candidate in Candidates(root, parseResult.GetValue(words) ?? []))
                Console.Out.WriteLine(candidate);

            return 0;
        });

        return command;
    }


    /// <summary>
    /// Candidates for the final word of <paramref name="typed"/>, which may be empty when
    /// the cursor sits after a space.
    /// </summary>
    /// <remarks>
    /// Built here rather than taken from <see cref="ParseResult.GetCompletions"/>, which in
    /// System.CommandLine 2.0.11 returns option names and nothing else: no subcommands at
    /// any depth, and no values for an option constrained by an enum or by
    /// <c>AcceptOnlyFromAmong</c>. Tab would have offered <c>--config</c> where the user
    /// needed <c>record</c> or <c>running</c>.
    /// </remarks>
    internal static IEnumerable<string> Candidates(RootCommand root, string[] typed)
    {
        var partial = typed.Length > 0 ? typed[^1] : string.Empty;
        var prefix = typed.Length > 0 ? typed[..^1] : [];

        var command = root.Parse(prefix).CommandResult.Command;

        // The context carries the partial word, so the symbol filters its own values.
        var context = root.Parse(typed).GetCompletionContext();

        // Directly after an option that takes a value, the value is the only sensible
        // suggestion — offering sibling options there would be noise.
        if (prefix.Length > 0 && FindOption(command, prefix[^1]) is { } valued)
            return valued.GetCompletions(context).Select(item => item.Label).Distinct();

        var candidates = new List<string>();

        candidates.AddRange(command.Subcommands
            .Where(subcommand => !subcommand.Hidden)
            .Select(subcommand => subcommand.Name));

        // Positional arguments with their own sources — `osdu status <service>`.
        foreach (var argument in command.Arguments.Where(argument => !argument.Hidden))
            candidates.AddRange(argument.GetCompletions(context).Select(item => item.Label));

        // Options are the fallback, not the headline. Offered once the user commits to one
        // by typing `-`, or when nothing better exists at this position. Otherwise
        // `osdu <Tab>` leads with eleven spellings of --help and buries the nouns, and
        // `osdu status <Tab>` hides the service names among them.
        if (partial.StartsWith('-') || candidates.Count == 0)
        {
            foreach (var option in VisibleOptions(command))
            {
                candidates.Add(option.Name);
                candidates.AddRange(option.Aliases);
            }
        }

        return candidates
            .Where(candidate => candidate.StartsWith(partial, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(candidate => candidate, StringComparer.Ordinal);
    }

    /// <summary>The command's own options plus the recursive ones it inherits.</summary>
    private static IEnumerable<Option> VisibleOptions(Command command)
    {
        foreach (var option in command.Options.Where(option => !option.Hidden))
            yield return option;

        foreach (var ancestor in command.Parents.OfType<Command>())
            foreach (var option in ancestor.Options.Where(o => o.Recursive && !o.Hidden))
                yield return option;
    }

    /// <summary>Resolves a token such as <c>--status</c> or <c>-s</c> to its option.</summary>
    private static Option? FindOption(Command command, string token) =>
        VisibleOptions(command).FirstOrDefault(option =>
            option.Name == token || option.Aliases.Contains(token));

    internal static string ScriptFor(string shell) => shell switch
    {
        "bash" => Bash,
        "zsh" => Zsh,
        "fish" => Fish,
        "powershell" => PowerShell,
        _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unsupported shell."),
    };

    // COMP_WORDS carries the program name in [0]; `osdu complete` wants only what follows.
    // `-o default` lets bash fall back to filenames when we return nothing, so completing a
    // path for --file still works.
    private const string Bash = """
        # osdu completion for bash
        #   eval "$(osdu completion bash)"
        # or, to load it once per session rather than on every shell start:
        #   osdu completion bash > /usr/local/etc/bash_completion.d/osdu

        _osdu_complete()
        {
            # Capture first, split second. Setting IFS before expanding
            # "${COMP_WORDS[@]:1}" makes bash collapse the slice into a single joined
            # argument, so the CLI sees `record ` instead of `record` plus an empty word
            # and returns nothing.
            local candidates
            candidates=$(osdu complete -- "${COMP_WORDS[@]:1}" 2>/dev/null) || return 0

            local IFS=$'\n'
            COMPREPLY=( $candidates )
            return 0
        }

        complete -o default -F _osdu_complete osdu

        """;

    // zsh's own completion system would mean shipping a #compdef file; bashcompinit is a
    // smaller ask and behaves identically for a candidate list this simple.
    private const string Zsh = """
        # osdu completion for zsh
        #   eval "$(osdu completion zsh)"
        # Add that to ~/.zshrc to make it permanent.

        autoload -U +X bashcompinit && bashcompinit

        _osdu_complete()
        {
            # Capture first, split second. Setting IFS before expanding
            # "${COMP_WORDS[@]:1}" makes bash collapse the slice into a single joined
            # argument, so the CLI sees `record ` instead of `record` plus an empty word
            # and returns nothing.
            local candidates
            candidates=$(osdu complete -- "${COMP_WORDS[@]:1}" 2>/dev/null) || return 0

            local IFS=$'\n'
            COMPREPLY=( $candidates )
            return 0
        }

        complete -o default -F _osdu_complete osdu

        """;

    // commandline -opc gives the tokens before the cursor; -ct gives the partial word the
    // cursor is on. Passing both reproduces the trailing-empty-word convention.
    private const string Fish = """
        # osdu completion for fish
        #   osdu completion fish > ~/.config/fish/completions/osdu.fish

        function __osdu_complete
            set -l tokens (commandline -opc)
            set -e tokens[1]
            osdu complete -- $tokens (commandline -ct) 2>/dev/null
        end

        complete -c osdu -f -a '(__osdu_complete)'

        """;

    // $wordToComplete is already the final element of $commandAst, so it is not appended
    // again; an empty one still arrives as a trailing empty element.
    private const string PowerShell = """
        # osdu completion for PowerShell
        #   osdu completion powershell | Out-String | Invoke-Expression
        # To make it permanent, append that line to $PROFILE.
        #
        # If running it is blocked, the execution policy is the cause, not this script:
        #   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned

        Register-ArgumentCompleter -Native -CommandName osdu -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $words = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { "$_" })
            if ($wordToComplete -eq '') { $words += '' }

            osdu complete -- @words 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new(
                    $_, $_, 'ParameterValue', $_)
            }
        }

        """;
}
