using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Entitlements.Groups.All;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs group list --all</c> — every group in the data partition, not only the caller's,
/// through Entitlements' <c>GET /groups/all</c>.
/// </summary>
/// <remarks>
/// <para>An operator's question: what groups exist here. <c>group list</c> answers a different
/// one, the groups the caller belongs to, from another endpoint with other options, so
/// <c>--all</c> sends the command elsewhere rather than paging the same request. The generator
/// cannot express that (COMMAND-GRAMMAR §3.2), so this extends the generated command through
/// the Entitlements <c>Customize</c> hook.</para>
///
/// <para>The endpoint pages by an offset carried as a cursor, and how its list ends depends on
/// the provider: Azure, which ADME runs, answers the last page with the cursor <c>"0"</c>, which
/// followed would start the list again; the JDBC provider keeps counting and ends on a short or
/// empty page. Both end the paging here.</para>
/// </remarks>
public static class GroupListAll
{
    /// <summary>Groups asked for per page. The service's default is 100 and it sets no maximum.</summary>
    internal const int PageSize = 1000;

    /// <summary>The roles <c>GET /groups/all</c> admits, from the service's own authorization check.</summary>
    internal const string Roles = "users.datalake.ops or service.entitlements.admin";

    /// <summary>Group types the service filters by. <c>NONE</c> is no filter.</summary>
    private static readonly string[] Types = ["NONE", "DATA", "USER", "SERVICE"];

    private static readonly OutputSpec Columns = OutputSpec.Table(
        "groups", ("Email", "email"), ("Name", "name"), ("Description", "description"));

    /// <summary>Adds <c>--all</c> and <c>--type</c> to the generated <c>group list</c>.</summary>
    public static void Extend(Command list)
    {
        var all = new Option<bool>("--all")
        {
            Description = "Every group in the data partition, not only yours. Needs " + Roles + ".",
        };
        var type = new Option<string>("--type")
        {
            Description = "With --all, only groups of this type: data, user or service. Without it, every group.",
            DefaultValueFactory = _ => "NONE",
        };
        type.AcceptAnyCasingFromAmong(Types);
        list.Options.Add(all);
        list.Options.Add(type);
        // Plain `group list` needs no particular role; only --all does.
        CommandRoles.Set(list, $"with --all, {Roles}", operatorRole: true);

        var roleRequired = list.Options.Single(option => option.Name == "--role-required");
        list.Validators.Add(result =>
        {
            var everyGroup = OptionValues.Usable(result, all);
            if (!everyGroup && result.GetResult(type) is { Implicit: false })
                result.AddError("--type applies only with --all.");
            if (everyGroup && result.GetResult(roleRequired) is not null)
                result.AddError("--role-required cannot be combined with --all: it describes your own membership.");
        });

        // The generated action stays the command's own; --all goes elsewhere.
        var own = (AsynchronousCommandLineAction)list.Action!;
        list.SetAction((parseResult, cancellationToken) => parseResult.GetValue(all)
            ? CliRunner.RunAsync(parseResult, async (context, token) =>
                {
                    var json = await CollectAsync(context.Client.Entitlements.Groups.All, parseResult.GetValue(type)!,
                        Console.IsErrorRedirected ? null : Console.Error, token);
                    return context.Output.Write(json, Columns);
                }, cancellationToken, Roles)
            : own.InvokeAsync(parseResult, cancellationToken));
    }

    /// <summary>Every group of <paramref name="type"/>, page by page, as one <c>groups</c> list.</summary>
    internal static Task<string> CollectAsync(
        AllRequestBuilder groups, string type, TextWriter? progress, CancellationToken cancellationToken) =>
        CursorPaging.CollectAsync(
            new JsonObject(), limit: null, PageSize, "limit", "cursor", "groups", "totalCount",
            async (page, pageCancellation) => await OsduJson.ToJsonAsync(await groups.GetAsync(configuration =>
            {
                configuration.QueryParameters.Type = type;
                configuration.QueryParameters.Limit = page["limit"]!.GetValue<int>();
                configuration.QueryParameters.Cursor = page["cursor"]?.GetValue<string>();
            }, pageCancellation)),
            // Nothing to release: the cursor is an offset, not something the service holds open.
            (_, _) => Task.CompletedTask,
            progress, cancellationToken, endCursor: "0");
}
