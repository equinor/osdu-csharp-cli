using System.CommandLine;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.WellboreDdms.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>&lt;noun&gt; data add</c> and <c>&lt;noun&gt; data session send</c>: writing a WellLog's,
/// trajectory's, PPFG dataset's or pressure test's bulk data from JSON or Parquet files.
/// </summary>
/// <remarks>
/// <para>Hand-written because the body is a file, sent as it is: the service takes either
/// format, told apart by the content type, and a file of curves can run to gigabytes, which a
/// round trip through a model would hold in memory. The format is read from the file itself,
/// so a Parquet file named <c>.json</c> still goes as Parquet.</para>
///
/// <para><c>data add</c> with one file sends it as the whole bulk, replacing the latest. Given
/// several, or <c>--update</c>, it opens an upload session, sends them in order, and commits:
/// one new version from all of them, as the service recommends past ten million values or
/// three thousand columns. On any failure, an interruption included, it abandons the session,
/// so a half-sent upload never becomes a version.</para>
/// </remarks>
public static class WellboreBulkWrite
{
    /// <summary>A record type that carries bulk data: its noun, path segment and kind.</summary>
    internal sealed record Entity(string Noun, string Path, string Kind);

    internal static readonly Entity[] Entities =
    [
        new("welllog", "welllogs", "WellLog"),
        new("trajectory", "wellboretrajectories", "WellboreTrajectory"),
        new("ppfg", "ppfgdataset", "PPFGDataset"),
        new("pressuretest", "wellpressuretestrawmeasurement", "WellPressureTestRawMeasurement"),
    ];

    private const string Roles = "users.datalake.editors or users.datalake.admins";
    internal const string Parquet = "application/x-parquet";
    internal const string Json = "application/json";

    /// <summary>Every failure, read for the reason the service gives.</summary>
    private static readonly Dictionary<string, ParsableFactory<IParsable>> Errors =
        new() { ["XXX"] = DdmsError.CreateFromDiscriminatorValue };

    /// <summary>
    /// A failure from Wellbore DDMS, with the reason it gives in <c>detail</c>.
    /// </summary>
    /// <remarks>
    /// <c>detail</c> is a sentence for most refusals, such as "Column(s) do(es) not match any
    /// CurveID of the WellLog record.", and a list of <c>{loc, msg}</c> entries for a request it
    /// cannot validate. The client maps only the second, so the first reached the user as a bare
    /// "400 from the service." and the reason was only in the response.
    /// </remarks>
    internal sealed class DdmsError : ApiException, IParsable
    {
        private UntypedNode? _detail;
        private string? _message;

        // `message` is where an error page from a gateway in front of the service ends up: the
        // text body reader puts the page's title there.
        public override string Message => Reason(_detail) ?? _message ?? string.Empty;

        public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() =>
            new Dictionary<string, Action<IParseNode>>
            {
                ["detail"] = node => _detail = node.GetObjectValue(UntypedNode.CreateFromDiscriminatorValue),
                ["message"] = node => _message = node.GetStringValue(),
            };

        public void Serialize(ISerializationWriter writer)
        {
        }

        public static DdmsError CreateFromDiscriminatorValue(IParseNode parseNode) => new();

        /// <summary>The sentence, or the first three validation messages with where they apply.</summary>
        internal static string? Reason(UntypedNode? detail) => detail switch
        {
            UntypedString text => text.GetValue(),
            UntypedArray entries => Join(entries.GetValue().Select(Entry).Where(text => !string.IsNullOrWhiteSpace(text)).Take(3)),
            _ => null,
        };

        private static string? Entry(UntypedNode entry)
        {
            if (entry is UntypedString text)
                return text.GetValue();
            if (entry is not UntypedObject fields)
                return null;
            var values = fields.GetValue();
            var message = values.TryGetValue("msg", out var msg) && msg is UntypedString said ? said.GetValue() : null;
            // FastAPI's location starts with where in the request it was found: "body".
            var where = values.TryGetValue("loc", out var loc) && loc is UntypedArray path
                ? string.Join('.', path.GetValue().OfType<UntypedString>().Select(part => part.GetValue()).Skip(1))
                : "";
            return where.Length > 0 && message is not null ? $"{where}: {message}" : message;
        }

        private static string? Join(IEnumerable<string?> messages) =>
            string.Join("; ", messages) is { Length: > 0 } joined ? joined : null;
    }

    /// <summary>Adds the two commands to each bulk-carrying type's <c>data</c> groups.</summary>
    public static void Extend(CommandTree tree)
    {
        foreach (var entity in Entities)
        {
            tree.Node($"{entity.Noun} data").Subcommands.Add(BuildAdd(entity));
            tree.Node($"{entity.Noun} data session").Subcommands.Add(BuildSend(entity));
        }
    }

    private static Command BuildAdd(Entity entity)
    {
        var id = new Option<string>("--id", "-id") { Description = $"{entity.Kind} record id.", Required = true };
        var files = new Option<string[]>("--file", "-f")
        {
            Description = "JSON or Parquet file of the bulk data. Repeat it to send several as the chunks of one upload "
                          + "session, which becomes one new version.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var update = new Option<bool>("--update")
        {
            Description = "Merge the files into the latest version's bulk data rather than replacing it.",
        };
        var command = new Command("add",
            $"Write a {entity.Kind}'s bulk data from JSON or Parquet files, as a new version.")
        {
            id, files, update,
        };

        command.SetAction((parseResult, cancellationToken) => CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var paths = parseResult.GetValue(files)!;
            var chunks = paths.Select(Chunk).ToList();
            var adapter = context.Client.GetRequestAdapter("wellbore_ddms");
            var recordId = parseResult.GetValue(id)!;
            // Progress only at a terminal; what became of a session is said either way.
            var progress = Console.IsErrorRedirected ? null : Console.Error;

            var json = chunks.Count == 1 && !parseResult.GetValue(update)
                ? await WriteAsync(adapter, entity, recordId, chunks[0], token)
                : await WriteSessionAsync(adapter, entity, recordId, chunks, parseResult.GetValue(update), progress,
                    Console.Error, token);
            return context.Output.Write(json, OutputSpec.Raw);
        }, cancellationToken, Roles));

        return command;
    }

    private static Command BuildSend(Entity entity)
    {
        var id = new Option<string>("--id", "-id") { Description = $"{entity.Kind} record id.", Required = true };
        var session = new Option<Guid>("--session", "-s")
        {
            Description = "Session id, as session open printed it.",
            Required = true,
        };
        var file = new Option<string>("--file", "-f") { Description = "JSON or Parquet file of the chunk.", Required = true };
        var command = new Command("send",
            "Send a JSON or Parquet file as a chunk of an open session. Commit the session with session update.")
        {
            id, session, file,
        };

        command.SetAction((parseResult, cancellationToken) => CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var json = await SendChunkAsync(context.Client.GetRequestAdapter("wellbore_ddms"), entity,
                parseResult.GetValue(id)!, parseResult.GetValue(session), Chunk(parseResult.GetValue(file)!), token);
            return context.Output.Write(json, OutputSpec.Table(null,
                ("Rows", "rowCount"), ("Columns", "columnCount"), ("IndexStart", "indexStart"), ("IndexEnd", "indexEnd")));
        }, cancellationToken, Roles));

        return command;
    }

    // ---- the file -----------------------------------------------------------------------

    /// <summary>A file to send, and the content type its first bytes say it is.</summary>
    internal sealed record Part(string Path, string ContentType, long Length);

    /// <summary>
    /// The file at <paramref name="path"/>, refused when it is missing or neither Parquet nor JSON.
    /// </summary>
    /// <remarks>
    /// Parquet files begin with <c>PAR1</c>; JSON, after any whitespace, with <c>{</c> or
    /// <c>[</c>. Anything else, a LAS or CSV file say, is refused here rather than by the
    /// service, which would answer with a validation error about the content type.
    /// </remarks>
    internal static Part Chunk(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new OsduException(Directory.Exists(path) ? $"{path} is a directory, not a file." : $"File not found: {path}");

        using var stream = file.OpenRead();
        Span<byte> head = stackalloc byte[4];
        var read = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        if (read == 4 && head.SequenceEqual("PAR1"u8))
            return new Part(path, Parquet, file.Length);

        stream.Position = 0;
        int first;
        while ((first = stream.ReadByte()) is ' ' or '\t' or '\r' or '\n' or 0xEF or 0xBB or 0xBF)
        {
        }
        if (first is '{' or '[')
            return new Part(path, Json, file.Length);

        throw new OsduException($"{path} is neither Parquet nor JSON. Bulk data is written from either, such as "
                                + "a data frame saved with orient=\"split\".");
    }

    // ---- the service ----------------------------------------------------------------------

    /// <summary>Writes one file as the record's whole bulk data, a new version.</summary>
    internal static Task<string?> WriteAsync(IRequestAdapter adapter, Entity entity, string recordId, Part part,
        CancellationToken cancellationToken) =>
        SendFileAsync(adapter, Request(adapter, Method.POST, "/data", entity, recordId), part, cancellationToken);

    /// <summary>Sends one file as a chunk of an open session.</summary>
    internal static Task<string?> SendChunkAsync(IRequestAdapter adapter, Entity entity, string recordId, Guid session,
        Part part, CancellationToken cancellationToken) =>
        SendFileAsync(adapter, Request(adapter, Method.POST, "/sessions/{session_id}/data", entity, recordId, session),
            part, cancellationToken);

    /// <summary>
    /// Opens a session, sends <paramref name="parts"/> in order, and commits it: one new version.
    /// Abandons it on any failure, so a partial upload is never committed.
    /// </summary>
    /// <remarks>
    /// <para>What became of the session is written to <paramref name="notes"/> whether or not
    /// progress is shown: a script's log needs the session id of an upload that went wrong more
    /// than a terminal does.</para>
    ///
    /// <para>A commit that gets no answer is not a failed upload. The version may exist, so the
    /// session is not abandoned; its state is asked for instead, and a committed session is the
    /// success it is.</para>
    /// </remarks>
    internal static async Task<string?> WriteSessionAsync(IRequestAdapter adapter, Entity entity, string recordId,
        IReadOnlyList<Part> parts, bool update, TextWriter? progress, TextWriter notes, CancellationToken cancellationToken)
    {
        var opened = await SendJsonAsync(adapter, Request(adapter, Method.POST, "/sessions", entity, recordId),
            new JsonObject { ["mode"] = update ? "update" : "overwrite" }, cancellationToken);
        if (JsonNode.Parse(opened ?? "null")?["id"]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var session))
            throw new OsduException("The service opened a session but did not say its id.");

        try
        {
            for (var index = 0; index < parts.Count; index++)
            {
                progress?.WriteLine($"Sending {index + 1} of {parts.Count}: {parts[index].Path} ({SignedUrlTransfer.Size(parts[index].Length)})");
                await SendChunkAsync(adapter, entity, recordId, session, parts[index], cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await AbandonAsync(adapter, entity, recordId, session, notes);
            throw;
        }

        try
        {
            return await SendJsonAsync(adapter, Request(adapter, Method.PATCH, "/sessions/{session_id}", entity, recordId, session),
                new JsonObject { ["state"] = "commit" }, cancellationToken);
        }
        catch (ApiException)
        {
            // Answered and refused, by the check the service runs on committing: nothing new.
            await AbandonAsync(adapter, entity, recordId, session, notes);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var (state, json) = await StateAsync(adapter, entity, recordId, session);
            if (state == "committed")
            {
                notes.WriteLine($"The commit of session {session} got no answer, but the session is committed: the new version was written.");
                return json;
            }
            notes.WriteLine(
                $"The commit of session {session} got no answer, and the session is {state ?? "in a state that could not be read"}. "
                + $"Check it with `osducs {entity.Noun} data session get --id {recordId} --session {session}` before writing again.");
            throw;
        }
    }

    /// <summary>Abandons a session, saying how it went; never throws, so the failure that led here is reported.</summary>
    private static async Task AbandonAsync(IRequestAdapter adapter, Entity entity, string recordId, Guid session, TextWriter notes)
    {
        // Not the command's token, which an interruption has cancelled.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await SendJsonAsync(adapter, Request(adapter, Method.PATCH, "/sessions/{session_id}", entity, recordId, session),
                new JsonObject { ["state"] = "abandon" }, timeout.Token);
            notes.WriteLine($"Abandoned session {session}: nothing was written.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            notes.WriteLine($"Session {session} could not be abandoned. Nothing from it is written unless it is committed, "
                            + "and the service drops it when its time to live runs out.");
        }
    }

    /// <summary>A session's state, and the session as JSON, or nulls when it cannot be read.</summary>
    private static async Task<(string? State, string? Json)> StateAsync(IRequestAdapter adapter, Entity entity,
        string recordId, Guid session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var json = await ReadAsync(adapter, Request(adapter, Method.GET, "/sessions/{session_id}", entity, recordId, session),
                timeout.Token);
            return (JsonNode.Parse(json ?? "null")?["state"]?.GetValue<string>(), json);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, null);
        }
    }

    private static RequestInformation Request(IRequestAdapter adapter, Method method, string tail, Entity entity,
        string recordId, Guid? session = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["baseurl"] = adapter.BaseUrl ?? "",
            ["entity"] = entity.Path,
            ["record_id"] = recordId,
        };
        if (session is { } value)
            parameters["session_id"] = value;
        var request = new RequestInformation(method, "{+baseurl}/ddms/v3/{entity}/{record_id}" + tail, parameters);
        request.Headers.TryAdd("Accept", Json);
        return request;
    }

    private static async Task<string?> SendFileAsync(IRequestAdapter adapter, RequestInformation request, Part part,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        request.SetStreamContent(file, part.ContentType);
        return await ReadAsync(adapter, request, cancellationToken);
    }

    private static Task<string?> SendJsonAsync(IRequestAdapter adapter, RequestInformation request, JsonObject body,
        CancellationToken cancellationToken)
    {
        request.SetStreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString())), Json);
        return ReadAsync(adapter, request, cancellationToken);
    }

    private static async Task<string?> ReadAsync(IRequestAdapter adapter, RequestInformation request,
        CancellationToken cancellationToken)
    {
        await using var response = await adapter.SendPrimitiveAsync<Stream>(request, Errors, cancellationToken);
        if (response is null)
            return null;
        using var reader = new StreamReader(response);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
