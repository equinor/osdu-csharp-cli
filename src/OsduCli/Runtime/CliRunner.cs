using System.CommandLine;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Runs one generated command action: builds the <see cref="CliContext"/>, invokes the
/// body, and turns the failures a CLI user can actually cause into a one-line message and
/// a non-zero exit code.
/// </summary>
/// <remarks>
/// Centralised here rather than emitted into every command for two reasons: it keeps the
/// generated code to the part that differs per endpoint, and it means error handling can be
/// improved without regenerating. System.CommandLine invokes actions itself and reports any
/// exception that escapes, so this has to sit inside the action, not around the parse.
/// </remarks>
public static class CliRunner
{
    /// <summary>
    /// The most useful text an OSDU error carries, which is not always its <c>Message</c>.
    /// </summary>
    /// <remarks>
    /// Kiota generates a typed exception per error schema and overrides <c>Message</c> to
    /// whatever property the spec called <c>message</c>. Several OSDU services nest the real
    /// content one level down — Schema Service answers a bad request with
    /// <c>{"error":{"code":400,"message":"Schema Id is already present","errors":[…]}}</c> —
    /// and since the schema declares <c>message</c> at the top level, nothing matches and
    /// <c>Message</c> is empty. The CLI printed "error: 400 from the service." and stopped,
    /// which tells a user that they failed but not what to change.
    ///
    /// Unmapped body fields land in <c>AdditionalData</c>, so that is where to look. Every
    /// one of the 18 generated error models implements <see cref="IAdditionalDataHolder"/>,
    /// so this is a cast rather than a per-service switch that would rot the first time a
    /// spec changed — and rather than reflection, which trimming can quietly defeat and which
    /// would fail silently if a model ever declared the property differently.
    /// </remarks>
    private static string? Describe(ApiException exception)
    {
        if (!string.IsNullOrWhiteSpace(exception.Message))
            return exception.Message;

        if (exception is not IAdditionalDataHolder holder)
            return null;

        // Deepest-first: the nested object holds the message, the wrapper holds a status code
        // the caller has already been shown.
        foreach (var value in holder.AdditionalData.Values)
        {
            var text = Flatten(value);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    /// <summary>Pulls readable text out of whatever shape the error body arrived in.</summary>
    /// <remarks>
    /// A summary alone is often not actionable: Schema Service answers a malformed request
    /// with <c>"message": "Validation Error"</c> and puts what is actually wrong in a nested
    /// <c>errors</c> array — "schema must not be null", "Schema is not a valid JSON Object".
    /// Telling the user only that validation failed makes them go and read the raw response.
    ///
    /// Capped at three details. Past that it stops being a message and becomes a dump, and
    /// <c>--debug</c> already prints the whole body for anyone who wants it.
    /// </remarks>
    internal static string? Flatten(object? value) => value switch
    {
        null => null,
        string text => text,
        UntypedString untyped => untyped.GetValue(),
        UntypedObject obj => Summarise(obj),
        UntypedArray array => array.GetValue().Select(Flatten)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
        _ => null,
    };

    private static string? Summarise(UntypedObject error)
    {
        var fields = error.GetValue();

        var summary = new[] { "message", "reason" }
            .Select(key => fields.TryGetValue(key, out var v) ? Flatten(v) : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        var details = fields.TryGetValue("errors", out var nested) && nested is UntypedArray items
            ? items.GetValue()
                .OfType<UntypedObject>()
                .Select(item => item.GetValue().TryGetValue("message", out var m) ? Flatten(m) : null)
                .Where(text => !string.IsNullOrWhiteSpace(text) && text != summary)
                .Distinct()
                .Take(3)
                .ToList()
            : [];

        if (summary is null)
            return details.Count > 0 ? string.Join("; ", details) : NestedFallback(fields);

        return details.Count > 0 ? $"{summary}: {string.Join("; ", details)}" : summary;
    }

    /// <summary>Any readable text at all, when nothing is where it was expected.</summary>
    private static string? NestedFallback(IDictionary<string, UntypedNode> fields) =>
        fields.Values.Select(Flatten).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    /// <summary>
    /// Runs a command that needs no service call, turning the same failures into a one-line
    /// message rather than a stack trace.
    /// </summary>
    /// <remarks>
    /// <see cref="RunAsync"/> builds a <see cref="CliContext"/>, which loads configuration and
    /// constructs a token provider. The commands that report on configuration itself must work
    /// when that configuration is broken — which is exactly when someone runs them — so they
    /// cannot go through it.
    ///
    /// Still needs to be a wrapper rather than a try/catch in Program.cs: System.CommandLine
    /// invokes actions itself and reports whatever escapes, so an exception thrown in an action
    /// never reaches the code around the parse. `osducs config use nosuchprofile` printed a
    /// full stack trace until this existed.
    /// </remarks>
    public static int Run(ParseResult parseResult, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (Exception exception) when (parseResult.GetValue(GlobalOptions.Debug))
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        catch (OsduException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            // Writing the state file into a directory the user cannot write.
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    /// <param name="requiredRoles">
    /// The roles the endpoint documents, used to explain a 403. Derived from the spec by the
    /// generator, so a command whose spec says nothing simply passes null.
    /// </param>
    /// <param name="forbiddenHint">
    /// What to do instead when access is refused. Editorial rather than derivable — the spec
    /// knows `record list` needs an admin role, but not that `record search` answers the same
    /// question for everyone else.
    /// </param>
    public static async Task<int> RunAsync(
        ParseResult parseResult,
        Func<CliContext, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken,
        string? requiredRoles = null,
        string? forbiddenHint = null)
    {
        try
        {
            using var context = CliContext.Create(parseResult);
            return await body(context, cancellationToken);
        }
        // Before the --debug catch below, deliberately. That one matches any exception, so
        // with --debug set it took ApiException first and the 403 guidance never printed —
        // and TROUBLESHOOTING tells people to add --debug when a call is refused, so the
        // person actively diagnosing a 403 was the one who could not see why. Here --debug
        // adds the full exception rather than replacing the explanation.
        catch (ApiException exception)
        {
            // Non-2xx from an OSDU service. The status is what the user needs; the Kiota
            // stack trace above it is not.
            Console.Error.WriteLine(
                $"error: {exception.ResponseStatusCode} from the service. "
                + (Describe(exception) ?? string.Empty));

            // "The user is not authorized to perform this action" does not say which
            // authorisation, and several OSDU endpoints need an admin role a normal user will
            // never hold. Without this the only way to find out is to read the spec.
            if (exception.ResponseStatusCode == 403)
            {
                if (requiredRoles is not null)
                    Console.Error.WriteLine($"       this endpoint requires {requiredRoles}");
                if (forbiddenHint is not null)
                    Console.Error.WriteLine($"       {forbiddenHint}");
            }

            if (parseResult.GetValue(GlobalOptions.Debug))
                Console.Error.WriteLine(exception);

            return 1;
        }
        catch (Exception exception) when (parseResult.GetValue(GlobalOptions.Debug))
        {
            // --debug: the whole exception, for diagnosing an auth or transport failure
            // that the one-line summaries below deliberately hide.
            Console.Error.WriteLine(exception);
            return 1;
        }
        catch (OsduException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (JsonException exception)
        {
            // A malformed --file payload. The user's typo, not a bug worth a stack trace.
            Console.Error.WriteLine($"error: invalid JSON in request body. {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }
}
