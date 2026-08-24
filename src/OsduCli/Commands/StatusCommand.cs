using System.CommandLine;
using System.CommandLine.Completions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs status</c> — reports version and reachability for the OSDU services this CLI
/// talks to.
/// </summary>
/// <remarks>
/// Every OSDU service exposes an identical <c>GET /info</c>. The Python CLI turns that into
/// ten separate <c>&lt;service&gt; info</c> commands whose implementations all call the same
/// helper, so the user has to already know which service they care about and can never see
/// the estate at once. Here it is one command: no argument probes everything, an argument
/// narrows to one service.
///
/// Hand-written rather than generated for two reasons the manifest cannot express: it fans
/// out across services, and a failure against one service must not abort the others — an
/// unreachable service is precisely the answer the user asked for.
/// </remarks>
public static class StatusCommand
{
    /// <summary>
    /// The services this CLI can probe, in display order. One entry per service whose
    /// <c>/info</c> is claimed by a manifest's <c>handwritten:</c> block; adding a service
    /// manifest means adding a line here.
    /// </summary>
    private static readonly (string Name, Func<CliContext, CancellationToken, Task<string?>> Probe)[] Services =
    [
        ("crs-catalog", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.CrsCatalog.V3.Info.GetAsync(cancellationToken: cancellationToken))),
        ("dataset", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Dataset.Info.GetAsync(cancellationToken: cancellationToken))),
        ("entitlements", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Entitlements.Info.GetAsync(cancellationToken: cancellationToken))),
        ("file", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.File.V2.Info.GetAsync(cancellationToken: cancellationToken))),
        ("legal", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Legal.Info.GetAsync(cancellationToken: cancellationToken))),
        ("schema", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.SchemaService.Info.GetAsync(cancellationToken: cancellationToken))),
        ("search", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Search.Info.GetAsync(cancellationToken: cancellationToken))),
        ("storage", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Storage.Info.GetAsync(cancellationToken: cancellationToken))),
        ("unit", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.UnitV3.V3.Info.GetAsync(cancellationToken: cancellationToken))),
        ("workflow", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Workflow.V1.Info.GetAsync(cancellationToken: cancellationToken))),
    ];

    private static bool Known(string name) =>
        Services.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static Command Build()
    {
        var serviceArgument = new Argument<string?>("service")
        {
            Description = "Limit the report to one service. Omit to probe every service.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        serviceArgument.CompletionSources.Add(_ => Services.Select(s => new CompletionItem(s.Name)));

        // Validated at parse time, not in the action: a typo must be reported without first
        // loading config and authenticating, and must name the alternatives.
        serviceArgument.Validators.Add(result =>
        {
            var value = result.Tokens.Count > 0 ? result.Tokens[0].Value : null;
            if (value is null || Known(value))
                return;

            result.AddError(
                $"Unknown service '{value}'. Known services: " +
                string.Join(", ", Services.Select(s => s.Name)) + ".");
        });

        var command = new Command("status", "Show version and reachability for OSDU services.");
        command.Arguments.Add(serviceArgument);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var requested = parseResult.GetValue(serviceArgument);
            var selected = requested is null
                ? Services
                : Services.Where(s =>
                    string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase)).ToArray();

            var rows = new JsonArray();
            var failed = false;

            foreach (var (name, probe) in selected)
            {
                var row = await ProbeAsync(name, probe, context, cancellationToken);
                if (row["status"]?.GetValue<string>() != "ok") failed = true;
                rows.Add(row);
            }

            context.Output.Write(rows.ToJsonString(), OutputSpec.Table(
                null,
                ("Service", "service"),
                ("Status", "status"),
                ("Version", "version"),
                ("Build", "buildTime")));

            return failed ? 1 : 0;
        }, cancellationToken));

        return command;
    }

    /// <summary>
    /// Probes one service, turning any failure into a row rather than an exception. The
    /// catch is deliberately broad: the point of this command is to report whatever a
    /// service is doing, including refusing to talk to us.
    /// </summary>
    private static async Task<JsonObject> ProbeAsync(
        string name,
        Func<CliContext, CancellationToken, Task<string?>> probe,
        CliContext context,
        CancellationToken cancellationToken)
    {
        var row = new JsonObject { ["service"] = name };

        try
        {
            var json = await probe(context, cancellationToken);
            row["status"] = "ok";

            if (!string.IsNullOrWhiteSpace(json))
            {
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                    row[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            row["status"] = "unreachable";
            row["error"] = exception.Message;
        }

        return row;
    }
}
