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

    public static readonly Option<bool> Debug = new("--debug")
    {
        Description = "Show HTTP request/response detail and full exceptions.",
        Recursive = true,
    };

    public static void AddTo(RootCommand root)
    {
        root.Options.Add(Output);
        root.Options.Add(Config);
        root.Options.Add(Debug);
    }
}
