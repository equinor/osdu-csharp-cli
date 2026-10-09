using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs file download</c> — fetch a file's bytes, checked against the checksum its
/// metadata record holds.
/// </summary>
/// <remarks>
/// <para><c>file download-url</c> only hands out the address; fetching it took <c>curl</c> and
/// a signed URL pasted by hand. The Python CLI's <c>file download</c> did the fetch, and this
/// replaces it.</para>
///
/// <para>Hand-written because it is three requests: the record for the file's name and
/// checksum, the signed URL, and the bytes from storage, which take no OSDU token. The record
/// is read from Storage rather than the File service, whose metadata endpoint needs an editor
/// role a viewer, who may download, does not hold.</para>
/// </remarks>
public static class FileDownloadCommand
{
    public static Command Build()
    {
        var id = new Option<string>("--id", "-id")
        {
            Description = "File metadata record id.",
            Required = true,
        };
        var path = new Option<string?>("--path", "-p")
        {
            Description = "Where to save the file: a file name, or a directory to save it in under the name its "
                          + "record gives; a directory that does not exist yet is created. Defaults to that name in "
                          + "the current directory.",
        };
        var force = new Option<bool>("--force")
        {
            Description = "Replace a file that already exists at the path.",
        };

        var command = new Command("download",
            "Download a file's bytes, checked against the checksum its metadata record holds.")
        {
            id, path, force,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var fileId = parseResult.GetValue(id)!;
            var record = JsonNode.Parse(await OsduJson.ToJsonAsync(
                await context.Client.Storage.Records[fileId].GetAsync(cancellationToken: token)) ?? "null") as JsonObject ?? [];
            var source = Describe(record);
            var destination = Destination(parseResult.GetValue(path), source.Name, fileId);
            if (File.Exists(destination) && !parseResult.GetValue(force))
                throw new OsduException($"{destination} already exists. Pass --force to replace it, or --path to save elsewhere.");

            var signed = await context.Client.File.V2.Files[fileId].DownloadURL.GetAsync(cancellationToken: token);
            if (string.IsNullOrEmpty(signed?.SignedUrl))
                throw new OsduException($"The File service gave no download URL for {fileId}.");

            var checksum = await SaveAsync(SignedUrlTransfer.Http, signed.SignedUrl, destination,
                parseResult.GetValue(force), source, Console.IsErrorRedirected ? null : Console.Error, token);

            return context.Output.Write(new JsonArray(new JsonObject
            {
                ["id"] = fileId,
                ["path"] = destination,
                ["size"] = checksum.Bytes,
                ["checksum"] = checksum.Outcome,
            }).ToJsonString(), ResultSpec);
        }, cancellationToken,
            "service.file.viewers, users.datalake.viewers, users.datalake.editors, users.datalake.admins or users.datalake.ops"));

        return command;
    }

    private static readonly OutputSpec ResultSpec = OutputSpec.Table(
        null, ("Id", "id"), ("Path", "path"), ("Size", "size"), ("Checksum", "checksum"));

    // ---- what the record says ---------------------------------------------------------------

    /// <summary>The file as its record describes it.</summary>
    /// <param name="Name">The file's own name, or null when the record gives none.</param>
    /// <param name="Checksum">The recorded checksum, in hex, or null.</param>
    /// <param name="Algorithm">The recorded checksum's algorithm, as the record names it, or null.</param>
    internal sealed record Source(string? Name, string? Checksum, string? Algorithm);

    /// <summary>
    /// The name, checksum and algorithm from <c>data.DatasetProperties.FileSourceInfo</c>,
    /// falling back to <c>data.Name</c> and the last part of the file's location for the name.
    /// </summary>
    internal static Source Describe(JsonObject record)
    {
        var data = record["data"] as JsonObject;
        var info = data?["DatasetProperties"]?["FileSourceInfo"] as JsonObject;
        var location = Text(info?["FileSource"]);
        var name = Text(info?["Name"]) ?? Text(data?["Name"])
                   ?? location?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return new Source(name, Text(info?["Checksum"]), Text(info?["ChecksumAlgorithm"]));
    }

    /// <summary>
    /// Where to save the file: <paramref name="path"/> as given, or the record's name inside
    /// it when it is a directory, or that name in the current directory.
    /// </summary>
    internal static string Destination(string? path, string? recordName, string id)
    {
        var name = SafeFileName(recordName, id);
        if (string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(name);
        if (Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(Path.Combine(path, name));
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// <paramref name="name"/> made safe to save under, on any platform, without leaving the
    /// directory it is saved in.
    /// </summary>
    /// <remarks>
    /// <para>The name comes from a record anyone with write access could have set. Separators
    /// and characters Windows refuses become <c>_</c>, so <c>../../.bashrc</c> is saved as
    /// <c>.._.._.bashrc</c> where it was asked to go, and a name with nothing left falls back
    /// to the id's last part.</para>
    ///
    /// <para>Windows also refuses a name ending in a dot or a space, and a device name such as
    /// <c>CON</c> or <c>nul.txt</c> whatever follows it, so the first are trimmed and the
    /// second get a leading <c>_</c>. A record naming one failed to download there.</para>
    /// </remarks>
    internal static string SafeFileName(string? name, string id)
    {
        static string Clean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
            var cleaned = new string([.. text.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)])
                .TrimEnd('.', ' ');
            if (cleaned.TrimStart('.').Length == 0)
                return "";
            var stem = cleaned.Split('.')[0].TrimEnd(' ');
            return ReservedNames.Contains(stem) ? "_" + cleaned : cleaned;
        }

        var safe = Clean(name);
        if (safe.Length > 0)
            return safe;
        var fromId = Clean(id.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
        return fromId.Length > 0 ? fromId : "download";
    }

    /// <summary>The device names Windows reserves, in any case and with any extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL",
         .. Enumerable.Range(1, 9).Select(n => $"COM{n}"), "COM¹", "COM²", "COM³",
         .. Enumerable.Range(1, 9).Select(n => $"LPT{n}"), "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    // ---- saving and checking ----------------------------------------------------------------

    /// <summary>What was saved, and what checking it found.</summary>
    internal sealed record Saved(long Bytes, string Outcome);

    /// <summary>
    /// Downloads into a file beside <paramref name="destination"/> and moves it into place only
    /// once its checksum matches, so a failed or interrupted download never leaves a partial
    /// or wrong file under the name asked for.
    /// </summary>
    /// <remarks>
    /// The directory is created first: <c>--path logs/</c> names one that may not exist yet,
    /// and opening the file in it failed with an unexplained exception. The partial file's
    /// name is unique to the run, and in the same directory so the move into place is a
    /// rename: a fixed name made a second download to the same path fail to open it.
    /// </remarks>
    internal static async Task<Saved> SaveAsync(
        HttpClient http, string url, string destination, bool force, Source source,
        TextWriter? progress, CancellationToken cancellationToken)
    {
        var expected = Expected(source);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.osducs-download");
        var moved = false;
        try
        {
            SignedUrlTransfer.Received received;
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                received = await SignedUrlTransfer.DownloadAsync(
                    http, url, file, expected?.Algorithm ?? HashAlgorithmName.MD5, progress, cancellationToken);
            }

            var outcome = Check(received, expected, source);
            File.Move(partial, destination, overwrite: force);
            moved = true;
            return new Saved(received.Bytes, outcome);
        }
        finally
        {
            if (!moved)
                TryDelete(partial);
        }
    }

    /// <summary>A recorded checksum this can verify: its algorithm, and the hash it names.</summary>
    internal sealed record Checksum(HashAlgorithmName Algorithm, string Label, byte[] Hash);

    /// <summary>
    /// The record's checksum, when it is one of the algorithms the platform uses and is
    /// well-formed hex. An unnamed algorithm is taken from the length, as MD5 is the schema's
    /// documented default.
    /// </summary>
    internal static Checksum? Expected(Source source)
    {
        if (source.Checksum is not { } hex || !IsHex(hex))
            return null;
        var algorithm = (source.Algorithm?.Replace("-", "").ToUpperInvariant(), hex.Length) switch
        {
            ("MD5" or null, 32) => (HashAlgorithmName.MD5, "MD5"),
            ("SHA256" or null, 64) => (HashAlgorithmName.SHA256, "SHA-256"),
            _ => ((HashAlgorithmName, string)?)null,
        };
        return algorithm is { } known ? new Checksum(known.Item1, known.Item2, Convert.FromHexString(hex)) : null;
    }

    /// <summary>
    /// Whether the bytes are the file the record describes: against its checksum, or failing
    /// that the MD5 storage keeps for the blob. A mismatch is refused.
    /// </summary>
    internal static string Check(SignedUrlTransfer.Received received, Checksum? expected, Source source)
    {
        if (expected is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(received.Hash, expected.Hash))
            {
                throw new OsduException(
                    $"The downloaded bytes do not match the record's {expected.Label} checksum "
                    + $"(expected {Convert.ToHexStringLower(expected.Hash)}, got {Convert.ToHexStringLower(received.Hash)}), "
                    + "so nothing was saved. Run the command again; if it fails the same way, the record's checksum "
                    + "does not describe the file it points to.");
            }
            return $"{expected.Label} matches the record";
        }

        // Only reached hashing MD5: Expected returned null, so DownloadAsync was given the default.
        if (received.StorageMd5 is { Length: 16 } stored)
        {
            if (!CryptographicOperations.FixedTimeEquals(received.Hash, stored))
            {
                throw new OsduException(
                    "The downloaded bytes do not match the MD5 storage holds for the file, so nothing was saved. "
                    + "Run the command again.");
            }
            return "MD5 matches storage";
        }

        return source.Checksum is null
            ? "not checked: none recorded"
            : $"not checked: {source.Algorithm ?? "unknown algorithm"} not supported";
    }

    private static bool IsHex(string text) => text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind rather than hiding the failure that got here.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
