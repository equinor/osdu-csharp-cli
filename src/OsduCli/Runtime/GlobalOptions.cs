using System.CommandLine;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Options attached to the root command and inherited by every subcommand.
/// </summary>
/// <remarks>
/// Held statically so generated command actions can read them without the generator having
/// to thread option instances through every call site — the same role the Python CLI's
/// <c>State</c> object plays.
/// </remarks>
public static class GlobalOptions
{
    public static readonly Option<string> Output = new("--output", "-o")
    {
        Description = "Output format: table (default) or json.",
        DefaultValueFactory = _ => "table",
        Recursive = true,
    };

    public static readonly Option<string?> Config = new("--config", "-c")
    {
        Description = "Config file path, or a profile name from ~/.osducli/.",
        Recursive = true,
    };

    /// <summary>
    /// Which account to authenticate as, when the token cache holds more than one.
    /// </summary>
    /// <remarks>
    /// A person with a normal account and a separate privileged one has exactly that, and
    /// until now the first cached account won silently. Global rather than per-command
    /// because identity applies to every call, and it has to be settable on the command a
    /// user is already typing rather than by signing out and back in.
    /// </remarks>
    public static readonly Option<string?> User = new("--user", "-u")
    {
        Description = "Account to authenticate as, e.g. name@equinor.com. "
                    + "See `osducs account list`.",
        Recursive = true,
    };

    public static readonly Option<bool> Debug = new("--debug")
    {
        Description = "Show HTTP request/response detail and full exceptions.",
        Recursive = true,
    };

    /// <summary>
    /// Every option declared here, so nothing has to restate the list.
    /// </summary>
    /// <remarks>
    /// <c>CliHelp</c> asks this which options are global, rather than keeping its own copy.
    /// It kept one until <c>--user</c> was added to this class and not to that list, and the
    /// new option was then rendered among each command's own flags — <c>--id</c>,
    /// <c>--attributes</c>, <c>--user</c> — which is the exact burial the separate section
    /// exists to prevent. Shipped in 0.5.0.
    /// </remarks>
    public static IReadOnlyList<Option> All { get; } = [Output, Config, User, Debug];

    public static void AddTo(RootCommand root)
    {
        foreach (var option in All)
            root.Options.Add(option);
    }
}
