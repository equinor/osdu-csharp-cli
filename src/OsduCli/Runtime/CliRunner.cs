using System.CommandLine;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
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
        catch (Exception exception) when (parseResult.GetValue(GlobalOptions.Debug))
        {
            // --debug: the whole exception, for diagnosing an auth or transport failure
            // that the one-line summaries below deliberately hide.
            Console.Error.WriteLine(exception);
            return 1;
        }
        catch (ApiException exception)
        {
            // Non-2xx from an OSDU service. The status is what the user needs; the Kiota
            // stack trace above it is not.
            Console.Error.WriteLine(
                $"error: {exception.ResponseStatusCode} from the service. {exception.Message}");

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
