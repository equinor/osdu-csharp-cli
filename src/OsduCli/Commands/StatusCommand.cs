using System.CommandLine;
using System.CommandLine.Completions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Microsoft.Kiota.Abstractions;

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
    /// The services this CLI can probe, in display order. Every service with a manifest —
    /// i.e. every service the CLI can issue a command against — must appear here, so that
    /// "is my estate healthy" has the same scope as "what can I run".
    /// </summary>
    /// <remarks>
    /// <c>StatusCoverageTests</c> enforces that, comparing this table against
    /// <c>cli-manifest/</c>. The table drifted behind the manifests once already: commands
    /// were added for CRS Conversion and Wellbore DDMS without a matching probe, so
    /// <c>status</c> reported a clean estate while two services it could talk to went
    /// unchecked.
    ///
    /// Most services are probed through their generated <c>Info</c> builder. Two are not,
    /// and the exceptions are the interesting part:
    /// <list type="bullet">
    /// <item>Wellbore DDMS predates the shared convention and serves <c>/about</c>.</item>
    /// <item>CRS Conversion's OpenAPI document declares only its three <c>convert</c>
    /// operations, so no <c>Info</c> builder is generated for it even though the running
    /// service answers <c>/v4/info</c> like every other. Until the spec is fixed upstream
    /// this one goes through the request adapter by hand.</item>
    /// </list>
    /// </remarks>
    private static readonly (string Name, Func<CliContext, CancellationToken, Task<string?>> Probe)[] Services =
    [
        ("crs-catalog", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.CrsCatalog.V3.Info.GetAsync(cancellationToken: cancellationToken))),
        // Hand-rolled: see the remarks above. Kiota generates nothing for a path the spec
        // does not declare, so this issues the request the missing builder would have.
        // Note the version skew: the service's operations are v4, but it serves info under
        // v2 and v3 only — v4/info is a 404. Verified against dev, 2026-08-26.
        ("crs-conversion", async (context, cancellationToken) =>
            await RawGetAsync(context, "crs_conversion", "v3/info", cancellationToken)),
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
        // Wellbore DDMS serves /about, not /info; the payload carries the same version fields.
        ("wellbore-ddms", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.WellboreDdms.About.GetAsync(cancellationToken: cancellationToken))),
        ("workflow", async (context, cancellationToken) =>
            await OsduJson.ToJsonAsync(
                await context.Client.Workflow.V1.Info.GetAsync(cancellationToken: cancellationToken))),
    ];

    /// <summary>Row keys owned by this command; a service payload cannot overwrite them.</summary>
    private static readonly HashSet<string> Reserved =
        new(StringComparer.Ordinal) { "service", "status", "error" };

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

            // Which environment this is has to be visible. `status` is the command whose
            // whole job is "what am I connected to", and with a default profile in play the
            // answer is no longer on the command line.
            context.Output.WriteMessage(
                $"{context.Config.Server}  partition {context.Config.DataPartitionId}");

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
    /// Merges a service's info payload into its row, leaving the columns this command owns
    /// untouched.
    /// </summary>
    /// <remarks>
    /// The payload is whatever the service chose to return, so it can collide with the row's
    /// own keys. Wellbore DDMS's <c>/about</c> carries a <c>service</c> field holding a
    /// display name ("Wellbore DDMS OSDU"), which replaced the row's name outright: the table
    /// then listed an estate entry that <c>status &lt;service&gt;</c> would reject as unknown.
    /// A payload claiming <c>status: "ok"</c> would have been worse — a service reporting its
    /// own health over ours.
    /// </remarks>
    internal static void MergePayload(JsonObject row, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (Reserved.Contains(property.Name)) continue;
            row[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    /// <summary>
    /// Issues a GET against a service the generated client has no builder for, reusing that
    /// service's own request adapter so the base URL, auth and partition header are resolved
    /// exactly as they are for every generated call.
    /// </summary>
    /// <remarks>
    /// This exists only for CRS Conversion, whose spec omits its <c>info</c> endpoint. It is
    /// deliberately not a general escape hatch: a second caller means a second spec defect,
    /// and the fix for those is upstream, not here.
    /// </remarks>
    private static async Task<string?> RawGetAsync(
        CliContext context,
        string serviceAttribute,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var adapter = context.Client.GetRequestAdapter(serviceAttribute);

        var request = new RequestInformation(
            Method.GET,
            "{+baseurl}/" + relativePath,
            new Dictionary<string, object> { ["baseurl"] = adapter.BaseUrl ?? string.Empty });
        request.Headers.Add("Accept", "application/json");

        using var stream = await adapter.SendPrimitiveAsync<Stream>(
            request, cancellationToken: cancellationToken);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
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
            MergePayload(row, json);
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
