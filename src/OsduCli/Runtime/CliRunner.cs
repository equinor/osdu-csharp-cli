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
    public static async Task<int> RunAsync(
        ParseResult parseResult,
        Func<CliContext, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
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
