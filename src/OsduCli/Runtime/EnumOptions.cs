using System.CommandLine;
using System.CommandLine.Parsing;

namespace Equinor.OsduCli.Runtime;

/// <summary>Enum options that accept their value in any casing.</summary>
/// <remarks>
/// The allowed values come from the OpenAPI specs, and the specs disagree with each other:
/// Entitlements and Search spell theirs <c>MEMBER</c> and <c>ASC</c>, while Storage, Workflow
/// and Wellbore DDMS spell theirs <c>version</c> and <c>running</c>. Nothing on the command
/// line says which convention the option being typed follows, so rejecting <c>--role member</c>
/// asked the caller to know something only the spec knows.
/// <para>
/// Casing is accepted freely; the value passed on is always the spec's own spelling, because
/// that is what the service parses. A value wrong in more than its casing is still rejected
/// at parse time, before any config load, token or round trip.
/// </para>
/// <para>
/// This replaces <c>AcceptOnlyFromAmong</c> rather than wrapping it: that validator tests the
/// token as typed, so normalising in a parser first does not reach it.
/// </para>
/// </remarks>
internal static class EnumOptions
{
    /// <summary>Accepts one value in any casing, normalised to the spec's spelling.</summary>
    public static void AcceptAnyCasingFromAmong(this Option<string> option, params string[] allowed)
    {
        option.CustomParser = result => Canonical(result, result.Tokens[0].Value, allowed);
        option.CompletionSources.Add(allowed);
    }

    /// <summary>Accepts repeated values in any casing, each normalised to the spec's spelling.</summary>
    public static void AcceptAnyCasingFromAmong(this Option<string[]> option, params string[] allowed)
    {
        option.CustomParser = result =>
        {
            var values = new string[result.Tokens.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = Canonical(result, result.Tokens[index].Value, allowed) ?? string.Empty;
            }

            return values;
        };
        option.CompletionSources.Add(allowed);
    }

    /// <summary>The spec's spelling of <paramref name="token"/>, or null once an error is recorded.</summary>
    private static string? Canonical(ArgumentResult result, string token, string[] allowed)
    {
        foreach (var value in allowed)
        {
            if (string.Equals(value, token, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        // Naming the casing rule here saves the reader a round of retrying `MEMBER` after
        // `member` was refused for some other reason.
        result.AddError(
            $"Argument '{token}' not recognized. Must be one of: "
            + string.Join(", ", allowed.Select(value => $"'{value}'"))
            + " (any casing).");
        return null;
    }
}
