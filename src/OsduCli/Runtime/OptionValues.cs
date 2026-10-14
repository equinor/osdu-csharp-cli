using System.CommandLine;
using System.CommandLine.Parsing;

namespace Equinor.OsduCli.Runtime;

/// <summary>Reading an option's value inside a parse-time validator.</summary>
public static class OptionValues
{
    /// <summary>
    /// The option's value, or the default when it has none to give: not given, or given without
    /// a usable value, which the parser reports by itself.
    /// </summary>
    /// <remarks>
    /// <c>GetValue</c> throws for the second inside a validator, and the option's own result
    /// carries no error to check first, so <c>record search --kind k --limit</c> crashed with a
    /// stack trace instead of saying the value was missing.
    /// </remarks>
    public static T? Usable<T>(SymbolResult result, Option<T> option)
    {
        try
        {
            return result.GetValue(option);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }
}
