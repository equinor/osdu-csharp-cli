using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Storage.Models;
using Equinor.OsduCsharpClient.Storage.Records;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs record add</c> — create or update records of any kind from a JSON file, through
/// Storage's <c>PUT /records</c>.
/// </summary>
/// <remarks>
/// <para>The typed nouns' <c>add</c> commands write their own kinds. Anything else, such as
/// reference data, had no path but <c>curl</c> and a token copied by hand.</para>
///
/// <para>Hand-written because it does more than the one request. Writing to an id that
/// exists adds a new version over whatever is there, and on a shared record — reference
/// data that several teams edit, where the last version wins — that can quietly drop
/// someone else's work. So it fetches the records the file names first, says which exist
/// and what would change in each, and asks before updating them. <c>--dry-run</c> stops
/// there.</para>
///
/// <para>The file is sent as it is, not read into the client's model and written back out:
/// a round trip would reformat timestamps and could reshape what a record holds, and these
/// are records other people rely on.</para>
/// </remarks>
public static class RecordAddCommand
{
    /// <summary>Storage's limit on the records one request may carry.</summary>
    internal const int MaxRecords = 500;

    /// <summary>How many records are fetched at once when checking which exist.</summary>
    private const int Concurrency = 8;

    public static Command Build()
    {
        var file = new Option<string>("--file", "-f")
        {
            Description = $"JSON file holding one record, or an array of up to {MaxRecords}.",
            Required = true,
        };
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Check the records and show which would be created and which updated, and what "
                          + "would change, without writing anything.",
        };
        var yes = new Option<bool>("--yes")
        {
            Description = "Update records that already exist without asking.",
        };
        var skipUnchanged = new Option<bool>("--skip-unchanged")
        {
            Description = "Leave a record alone when it is identical to its latest version, rather than "
                          + "adding an identical new one.",
        };

        var command = new Command("add",
            "Create or update records of any kind from a JSON file. Asks before updating a record that already exists.")
        {
            file, dryRun, yes, skipUnchanged,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var json = await CliContext.ReadBodyFileAsync(parseResult.GetValue(file)!, token);
            var records = Read(json, context.Config.DataPartitionId);
            var existing = await FetchExistingAsync(
                Ids(records), (id, cancel) => CurrentAsync(context.Client, id, cancel), token);
            var plan = Plan(records, existing);

            if (parseResult.GetValue(dryRun))
            {
                context.Output.Write(PlanRows(plan).ToJsonString(), PlanSpec);
                foreach (var update in plan.Where(entry => entry.IsUpdate))
                    context.Output.WriteNote(Describe(update));
                context.Output.WriteNote("Dry run: nothing was written.");
                return 0;
            }

            if (!Confirm(plan, parseResult.GetValue(yes), Console.IsInputRedirected ? null : Ask, context.Output))
                return 1;

            var response = await PutAsync(context.Client.Storage.Records, context.Client.GetRequestAdapter("storage"),
                json, parseResult.GetValue(skipUnchanged), token);

            return context.Output.Format == OutputFormat.Json
                ? context.Output.Write(await OsduJson.ToJsonAsync(response), OutputSpec.Raw)
                : context.Output.Write(Results(response, existing).ToJsonString(), ResultSpec);
        }, cancellationToken, "users.datalake.editors or users.datalake.admins"));

        return command;
    }

    // ---- reading the file -----------------------------------------------------------------

    /// <summary>
    /// The records in <paramref name="json"/>, refused with every problem at once when
    /// Storage would refuse them.
    /// </summary>
    /// <remarks>
    /// Checked here so a mistake costs nothing, and so the report names the record and the
    /// field rather than leaving the service's 400 to be decoded. Every problem is listed, so
    /// a file is fixed in one pass rather than one error per attempt: one line per record with
    /// all of its problems, which keeps even a file wrong throughout to a line a record. A cap
    /// on the lines listed made that promise false for any file with more than ten.
    /// </remarks>
    internal static IReadOnlyList<JsonObject> Read(string json, string partition)
    {
        IReadOnlyList<JsonNode?> items = JsonNode.Parse(json) switch
        {
            JsonObject record => [record],
            JsonArray array => [.. array],
            _ => throw new OsduException("The file must hold a record, or an array of records."),
        };
        if (items.Count == 0)
            throw new OsduException("The file holds an empty array: there are no records to add.");
        if (items.Count > MaxRecords)
        {
            throw new OsduException(
                $"The file holds {items.Count} records, and Storage takes at most {MaxRecords} in one request. "
                + $"Split it into files of {MaxRecords} or fewer.");
        }

        var problems = new List<string>();
        var records = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject record)
            {
                problems.Add($"record {index + 1} is not a JSON object");
                continue;
            }
            records.Add(record);

            var name = $"record {index + 1}";
            var wrong = new List<string>();
            if (Text(record, "id") is { } id)
            {
                name = $"record {index + 1} ({id})";
                // Storage refuses an id from another partition, as "does not belong to account".
                if (!id.StartsWith(partition + ":", StringComparison.Ordinal))
                    wrong.Add($"the id must start with '{partition}:', the data partition it is written to");
                if (!seen.Add(id))
                    wrong.Add("the id appears more than once in the file");
            }
            else if (Property(record, "id") is not null)
            {
                // A number or a blank string read as no id at all: the dry run said Storage
                // would assign one and skipped the check for an existing record, while the file,
                // sent as it is, still carried the id for Storage to refuse.
                wrong.Add("the id must be a non-empty string, or left out for Storage to assign one");
            }

            if (Text(record, "kind") is null)
                wrong.Add("kind is missing");
            // Empty as well as missing: written over an existing record, `{}` replaces all of
            // its data.
            if (Property(record, "data") is not JsonObject { Count: > 0 })
                wrong.Add("data is missing, empty, or not an object");
            foreach (var path in new[] { "acl.owners", "acl.viewers", "legal.legaltags", "legal.otherRelevantDataCountries" })
            {
                var segments = path.Split('.');
                if (Property(Property(record, segments[0]), segments[1]) is not JsonArray { Count: > 0 })
                    wrong.Add($"{path} is missing or empty");
            }

            if (wrong.Count > 0)
                problems.Add($"{name}: {string.Join("; ", wrong)}");
        }

        if (problems.Count > 0)
        {
            throw new OsduException(
                $"The file cannot be written; nothing was sent.{Environment.NewLine}"
                + string.Join(Environment.NewLine, problems.Select(problem => "  " + problem)));
        }

        return records;
    }

    /// <summary>The ids the file names, in order. A record without one is always new.</summary>
    internal static IReadOnlyList<string> Ids(IReadOnlyList<JsonObject> records) =>
        records.Select(record => Text(record, "id")).OfType<string>().ToList();

    // ---- what exists already --------------------------------------------------------------

    /// <summary>
    /// The latest version of each record in <paramref name="ids"/> that exists, by id.
    /// </summary>
    /// <remarks>
    /// One request per id. Storage's multi-record fetch needs a <c>service.storage</c> role
    /// most users lack, its batch fetch takes twenty at a time, and its header fetch is not
    /// deployed everywhere. The first is fetched alone: an interactive sign-in happens on the
    /// first request, and eight at once could each open a browser.
    /// </remarks>
    internal static async Task<Dictionary<string, JsonObject>> FetchExistingAsync(
        IReadOnlyList<string> ids, Func<string, CancellationToken, Task<JsonObject?>> current,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];

        var found = new JsonObject?[ids.Count];
        found[0] = await current(ids[0], cancellationToken);

        using var gate = new SemaphoreSlim(Concurrency);
        await Task.WhenAll(ids.Skip(1).Select(async (id, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                found[index + 1] = await current(id, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));

        return ids.Zip(found)
            .Where(pair => pair.Second is not null)
            .ToDictionary(pair => pair.First, pair => pair.Second!);
    }

    /// <summary>The latest version of a record, or null when there is none.</summary>
    private static async Task<JsonObject?> CurrentAsync(OsduClient client, string id, CancellationToken cancellationToken)
    {
        try
        {
            var record = await client.Storage.Records[id].GetAsync(cancellationToken: cancellationToken);
            return JsonNode.Parse(await OsduJson.ToJsonAsync(record) ?? "null") as JsonObject;
        }
        catch (ApiException exception) when (exception.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    // ---- the plan ---------------------------------------------------------------------------

    /// <summary>One record in the file, and what writing it would do.</summary>
    /// <param name="Id">The record's id, or null when Storage will assign one.</param>
    /// <param name="Kind">The record's kind.</param>
    /// <param name="Current">The latest version stored under the id, or null when it would be created.</param>
    /// <param name="Changes">What would change, field by field, when it is an update.</param>
    internal sealed record PlannedRecord(string? Id, string Kind, JsonObject? Current, IReadOnlyList<string> Changes)
    {
        public bool IsUpdate => Current is not null;
    }

    internal static IReadOnlyList<PlannedRecord> Plan(
        IReadOnlyList<JsonObject> records, IReadOnlyDictionary<string, JsonObject> existing) =>
        records.Select(record =>
        {
            var id = Text(record, "id");
            var current = id is not null && existing.TryGetValue(id, out var stored) ? stored : null;
            return new PlannedRecord(id, Text(record, "kind") ?? "", current,
                current is null ? [] : Changes(current, record));
        }).ToList();

    /// <summary>
    /// What writing <paramref name="proposed"/> over <paramref name="current"/> would change:
    /// each of <c>data</c>'s fields, and the record's other parts whole.
    /// </summary>
    /// <remarks>
    /// Per field rather than a full diff, which for a large record would bury the one line
    /// that matters: <c>removes data.Configurations</c> says another team's entries are about
    /// to go. A field that is absent, null or empty counts as absent, since Storage returns
    /// some parts filled in that a file may leave out.
    /// </remarks>
    internal static IReadOnlyList<string> Changes(JsonObject current, JsonObject proposed)
    {
        var changes = new List<string>();
        foreach (var part in new[] { "kind", "acl", "legal", "tags", "ancestry", "meta" })
            Compare(part, Property(current, part), Property(proposed, part));

        var before = Property(current, "data") as JsonObject ?? [];
        var after = Property(proposed, "data") as JsonObject ?? [];
        foreach (var field in after.Select(pair => pair.Key).Concat(before.Select(pair => pair.Key)).Distinct())
            Compare("data." + field, Property(before, field), Property(after, field));

        return changes;

        void Compare(string field, JsonNode? was, JsonNode? will)
        {
            if (IsEmpty(was) && IsEmpty(will))
                return;
            if (IsEmpty(was))
                changes.Add($"adds {field}");
            else if (IsEmpty(will))
                changes.Add($"removes {field}");
            else if (!JsonNode.DeepEquals(was, will))
                changes.Add($"changes {field}");
        }
    }

    private static bool IsEmpty(JsonNode? node) =>
        node is null or JsonObject { Count: 0 } or JsonArray { Count: 0 };

    /// <summary>An update in one line: the version it replaces, whose and when, and what changes.</summary>
    internal static string Describe(PlannedRecord update)
    {
        var current = update.Current!;
        var version = Property(current, "version")?.ToString() ?? "?";
        var who = Text(current, "modifyUser") ?? Text(current, "createUser") ?? "unknown";
        var when = Text(current, "modifyTime") ?? Text(current, "createTime");
        var changes = update.Changes.Count == 0 ? "no changes" : string.Join(", ", update.Changes);
        return $"{update.Id}: replaces version {version}, last changed by {who}"
               + (when is null ? "" : $" on {(when.Length >= 10 ? when[..10] : when)}")
               + $"; {changes}";
    }

    private static JsonArray PlanRows(IReadOnlyList<PlannedRecord> plan) =>
        [.. plan.Select(entry => (JsonNode)new JsonObject
        {
            ["id"] = entry.Id ?? "(assigned by Storage)",
            ["kind"] = entry.Kind,
            ["action"] = entry.IsUpdate ? "update" : "create",
            ["changes"] = entry.IsUpdate
                ? entry.Changes.Count == 0 ? "no changes" : string.Join(", ", entry.Changes)
                : "",
        })];

    private static readonly OutputSpec PlanSpec = OutputSpec.Table(
        null, ("Id", "id"), ("Kind", "kind"), ("Action", "action"), ("Changes", "changes"));

    // ---- asking -----------------------------------------------------------------------------

    /// <summary>
    /// Whether to go ahead: at once when nothing would be updated or <c>--yes</c> says so,
    /// otherwise when the person at the terminal agrees. With nobody to ask, refused, since
    /// guessing yes is the accident this exists to prevent.
    /// </summary>
    /// <remarks>
    /// What each update replaces and changes is said whichever way it goes. <c>--yes</c> skips
    /// the question, not the information: it returned before saying anything, so a pipeline
    /// writing over a shared record left no trace of whose version it replaced.
    /// </remarks>
    internal static bool Confirm(
        IReadOnlyList<PlannedRecord> plan, bool yes, Func<string, string?>? ask, OutputWriter output)
    {
        var updates = plan.Where(entry => entry.IsUpdate).ToList();
        if (updates.Count == 0)
            return true;

        foreach (var update in updates)
            output.WriteNote(Describe(update));
        if (yes)
            return true;

        var count = updates.Count == 1 ? "1 record already exists" : $"{updates.Count} records already exist";
        if (ask is null)
        {
            var them = updates.Count == 1 ? "it" : "them";
            throw new OsduException(
                $"{count}, and writing adds a new version over {(updates.Count == 1 ? "it" : "each")}. "
                + $"Pass --yes to update {them}, or --dry-run to see what would change.");
        }

        var answer = ask($"{count}. Update {(updates.Count == 1 ? "it" : "them")}? [y/N]");
        if (answer?.Trim().ToLowerInvariant() is "y" or "yes")
            return true;

        output.WriteNote("Nothing was written.");
        return false;
    }

    /// <summary>Asks on stderr, so stdout stays the result even when it is piped.</summary>
    private static string? Ask(string question)
    {
        Console.Error.Write(question + " ");
        return Console.ReadLine();
    }

    // ---- writing ----------------------------------------------------------------------------

    /// <summary>
    /// Sends the file's records as they are: the client's request for <c>PUT /records</c>,
    /// with the file in place of a body serialised from its model.
    /// </summary>
    internal static async Task<CreateUpdateRecordsResponse?> PutAsync(
        RecordsRequestBuilder records, IRequestAdapter adapter, string json, bool skipUnchanged,
        CancellationToken cancellationToken)
    {
        var request = records.ToPutRequestInformation([], configuration =>
        {
            if (skipUnchanged)
                configuration.QueryParameters.Skipdupes = true;
        });
        request.SetStreamContent(new MemoryStream(Encoding.UTF8.GetBytes(CliContext.WrapAsArray(json))), "application/json");

        // The error model the client maps Storage's failures to, so they are reported like
        // every other command's.
        return await adapter.SendAsync(request, CreateUpdateRecordsResponse.CreateFromDiscriminatorValue,
            new Dictionary<string, ParsableFactory<IParsable>> { ["XXX"] = AppError.CreateFromDiscriminatorValue },
            cancellationToken);
    }

    /// <summary>One row per record written or skipped, with its new version.</summary>
    internal static JsonArray Results(CreateUpdateRecordsResponse? response, IReadOnlyDictionary<string, JsonObject> existing)
    {
        var skipped = new HashSet<string>(response?.SkippedRecordIds ?? [], StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var id in response?.RecordIds ?? [])
        {
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["version"] = VersionOf(id, response?.RecordIdVersions),
                ["result"] = skipped.Contains(id) ? "unchanged" : existing.ContainsKey(id) ? "updated" : "created",
            });
        }
        foreach (var id in skipped.Where(id => !(response?.RecordIds ?? []).Contains(id)))
        {
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["version"] = existing.TryGetValue(id, out var current) ? Property(current, "version")?.ToString() : null,
                ["result"] = "unchanged",
            });
        }
        return rows;
    }

    /// <summary>
    /// The version Storage gave <paramref name="id"/>, from its <c>id:version</c> list. Matched
    /// on the id rather than split at the last colon, since an id may itself end in one.
    /// </summary>
    internal static string? VersionOf(string id, IEnumerable<string>? idVersions) =>
        idVersions?
            .Where(entry => entry.Length > id.Length + 1 && entry.StartsWith(id + ":", StringComparison.Ordinal))
            .Select(entry => entry[(id.Length + 1)..])
            .FirstOrDefault(version => version.All(char.IsAsciiDigit));

    private static readonly OutputSpec ResultSpec = OutputSpec.Table(
        null, ("Id", "id"), ("Version", "version"), ("Result", "result"));

    // ---- JSON helpers -----------------------------------------------------------------------

    private static JsonNode? Property(JsonNode? node, string name) =>
        node is JsonObject json && json.TryGetPropertyValue(name, out var value) ? value : null;

    private static string? Text(JsonNode? node, string name) =>
        Property(node, name) is JsonValue value && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
