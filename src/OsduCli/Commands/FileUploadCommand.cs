using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.FileNamespace.Models;
using Equinor.OsduCsharpClient.FileNamespace.V2.Files.Metadata;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>osducs file upload</c> — upload a file and create the metadata record that describes it.
/// </summary>
/// <remarks>
/// <para>Three steps that had to be done by hand: <c>file upload-url</c>, a <c>PUT</c> of the
/// bytes with the header Azure insists on, and <c>file add</c> with a record pointing at the
/// location the first step returned. The Python CLI had no upload at all.</para>
///
/// <para>Everything that can be checked locally is checked before a byte is sent, so a
/// mistake in the record costs nothing. The checksum is computed first, recorded, and sent
/// with the bytes for storage to refuse them if they arrive different.</para>
///
/// <para>Access and legal settings have no default: group domains differ between environments,
/// and a record readable by the wrong groups is the one mistake that matters. They come from
/// the options, a <c>--metadata</c> template, or both, the options winning.</para>
/// </remarks>
public static partial class FileUploadCommand
{
    /// <summary>The kind a record is created as unless the template says otherwise.</summary>
    internal const string DefaultKind = "osdu:wks:dataset--File.Generic:1.0.0";

    public static Command Build()
    {
        var file = new Option<string>("--file", "-f")
        {
            Description = $"The file to upload, of at most {SignedUrlTransfer.Size(SignedUrlTransfer.MaxUploadBytes)}.",
            Required = true,
        };
        var name = new Option<string?>("--name")
        {
            Description = "A name for the file's record (data.Name). Defaults to the file's own name.",
        };
        var description = new Option<string?>("--description")
        {
            Description = "A description for the file's record (data.Description).",
        };
        var legalTags = List("--legal-tag", "Legal tag for the record. Repeat the flag or comma-separate.");
        var countries = List("--country",
            "Two-letter country code for legal.otherRelevantDataCountries, e.g. NO. Repeat the flag or comma-separate.");
        var owners = List("--owner", "Data group that may change the record, e.g. data.default.owners@<domain>. "
                                     + "Repeat the flag or comma-separate.");
        var viewers = List("--viewer", "Data group that may read the record and the file, e.g. "
                                       + "data.default.viewers@<domain>. Repeat the flag or comma-separate.");
        var metadata = new Option<string?>("--metadata", "-m")
        {
            Description = "JSON file with the record to create, without an id. The file's location, size and "
                          + "checksum are filled in, and the options above replace what it says.",
        };
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Check the file and show the record that would be created, without uploading anything.",
        };

        var command = new Command("upload",
            "Upload a file and create the metadata record that describes it. Prints the new record's id.")
        {
            file, name, description, legalTags, countries, owners, viewers, metadata, dryRun,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var local = Local(parseResult.GetValue(file)!);
            var template = parseResult.GetValue(metadata) is { } templatePath
                ? Template(await CliContext.ReadBodyFileAsync(templatePath, token))
                : null;
            var record = Record(template, new Settings(
                parseResult.GetValue(name), parseResult.GetValue(description),
                parseResult.GetValue(legalTags) ?? [], parseResult.GetValue(countries) ?? [],
                parseResult.GetValue(owners) ?? [], parseResult.GetValue(viewers) ?? []), local.Name);

            var progress = Console.IsErrorRedirected ? null : Console.Error;
            var md5 = await HashAsync(local.FullName, token);

            if (parseResult.GetValue(dryRun))
            {
                Complete(record, "(assigned when uploaded)", local.Name, local.Length, md5);
                context.Output.Write(record.ToJsonString(), OutputSpec.Raw);
                context.Output.WriteNote("Dry run: nothing was uploaded.");
                return 0;
            }

            var location = await context.Client.File.V2.Files.UploadURL.GetAsync(cancellationToken: token);
            var (signedUrl, fileSource) = Location(location);

            await using (var bytes = new FileStream(local.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await SignedUrlTransfer.UploadAsync(
                    SignedUrlTransfer.Http, signedUrl, bytes, local.Length, md5, progress, token);
            }

            Complete(record, fileSource, local.Name, local.Length, md5);
            FileMetadataResponse? created;
            try
            {
                created = await PostAsync(context.Client.File.V2.Files.Metadata,
                    context.Client.GetRequestAdapter("file"), record.ToJsonString(), token);
            }
            catch (ApiException)
            {
                // Said before the error, so it is not mistaken for a failed upload worth
                // retrying as it is.
                context.Output.WriteNote(
                    "The file's bytes were uploaded, but its metadata record was not created, so nothing refers to them.");
                throw;
            }

            return context.Output.Write(new JsonArray(new JsonObject
            {
                ["id"] = created?.Id,
                ["name"] = (string?)record["data"]?["Name"],
                ["size"] = local.Length,
                ["md5"] = Convert.ToHexStringLower(md5),
            }).ToJsonString(), ResultSpec);
        }, cancellationToken, "service.file.editors, users.datalake.editors, users.datalake.admins or users.datalake.ops"));

        return command;
    }

    private static readonly OutputSpec ResultSpec = OutputSpec.Table(
        null, ("Id", "id"), ("Name", "name"), ("Size", "size"), ("MD5", "md5"));

    /// <summary>A repeatable option that also splits each value on commas, as the generated list options do.</summary>
    private static Option<string[]> List(string flag, string description) => new(flag)
    {
        Description = description,
        AllowMultipleArgumentsPerToken = true,
        CustomParser = result => result.Tokens
            .SelectMany(token => token.Value.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray(),
    };

    // ---- the local file ---------------------------------------------------------------------

    /// <summary>The file to upload, refused when it is missing or larger than one request may carry.</summary>
    internal static FileInfo Local(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new OsduException(Directory.Exists(path) ? $"{path} is a directory, not a file." : $"File not found: {path}");
        CheckSize(path, file.Length);
        return file;
    }

    internal static void CheckSize(string path, long length)
    {
        if (length > SignedUrlTransfer.MaxUploadBytes)
        {
            throw new OsduException(
                $"{path} is {SignedUrlTransfer.Size(length)}, and one upload takes at most "
                + $"{SignedUrlTransfer.Size(SignedUrlTransfer.MaxUploadBytes)}.");
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await MD5.HashDataAsync(stream, cancellationToken);
    }

    // ---- the record -------------------------------------------------------------------------

    /// <summary>What the options say the record should hold. Empty lists leave the template's.</summary>
    internal sealed record Settings(
        string? Name, string? Description,
        IReadOnlyList<string> LegalTags, IReadOnlyList<string> Countries,
        IReadOnlyList<string> Owners, IReadOnlyList<string> Viewers);

    /// <summary>The <c>--metadata</c> template, refused when it is not a record this can create.</summary>
    internal static JsonObject Template(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject template)
            throw new OsduException("The --metadata file must hold one record, as a JSON object.");
        // The File service would write over a record that exists under it, and the point of
        // this command is a new one: there is no check here of what that would replace.
        if (template["id"] is not null)
            throw new OsduException("The --metadata record has an id. file upload creates a new record; remove the id.");
        return template;
    }

    /// <summary>
    /// The record to create: the template, or an empty one, with the options applied over it.
    /// Every problem is reported at once, before anything is uploaded.
    /// </summary>
    internal static JsonObject Record(JsonObject? template, Settings settings, string fileName)
    {
        var record = (JsonObject?)template?.DeepClone() ?? [];
        record["kind"] ??= DefaultKind;

        var acl = CliContext.Child(record, "acl");
        var legal = CliContext.Child(record, "legal");
        Replace(legal, "legaltags", settings.LegalTags);
        Replace(legal, "otherRelevantDataCountries", settings.Countries.Select(country => country.ToUpperInvariant()).ToList());
        Replace(acl, "owners", settings.Owners);
        Replace(acl, "viewers", settings.Viewers);

        var data = CliContext.Child(record, "data");
        if (settings.Name is { } name)
            data["Name"] = name;
        data["Name"] ??= fileName;
        if (settings.Description is { } description)
            data["Description"] = description;

        var problems = new List<string>();
        if (record["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var kindText) || string.IsNullOrWhiteSpace(kindText))
            problems.Add("kind must be a string");
        Require(legal, "legal", "legaltags", "--legal-tag", problems);
        Require(legal, "legal", "otherRelevantDataCountries", "--country", problems,
            country => CountryPattern().IsMatch(country) ? null : $"'{country}' is not a two-letter country code");
        Require(acl, "acl", "owners", "--owner", problems, Group);
        Require(acl, "acl", "viewers", "--viewer", problems, Group);

        if (problems.Count > 0)
        {
            throw new OsduException(
                $"The file's record cannot be created; nothing was uploaded.{Environment.NewLine}"
                + string.Join(Environment.NewLine, problems.Select(problem => "  " + problem)));
        }
        return record;

        static string? Group(string group) => GroupPattern().IsMatch(group)
            ? null
            : $"'{group}' is not a data group, which looks like data.<name>@<domain>";
    }

    private static void Replace(JsonObject parent, string field, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
            parent[field] = new JsonArray([.. values.Distinct(StringComparer.Ordinal).Select(value => (JsonNode)value)]);
    }

    private static void Require(JsonObject parent, string parentName, string field, string flag, List<string> problems,
        Func<string, string?>? check = null)
    {
        var path = $"{parentName}.{field}";
        if (parent[field] is not JsonArray { Count: > 0 } values)
        {
            problems.Add($"{path} is missing: pass {flag}, or set it in --metadata");
            return;
        }
        foreach (var value in values)
        {
            var text = value is JsonValue json && json.TryGetValue<string>(out var s) ? s : null;
            if (text is null)
                problems.Add($"{path} must hold strings");
            else if (check?.Invoke(text) is { } problem)
                problems.Add($"{path}: {problem}");
        }
    }

    /// <summary>The ACL group pattern from the File service's own schema.</summary>
    [GeneratedRegex(@"^data\.[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")]
    private static partial Regex GroupPattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    /// <summary>
    /// Fills in where the bytes are and what they are: <c>FileSourceInfo</c>, with the file's
    /// location, own name, size and checksum, and <c>TotalSize</c>.
    /// </summary>
    /// <remarks>
    /// <c>FileSourceInfo</c> is written whole. A template copied from an existing record
    /// describes that record's upload: its checksum, its size, and on records written by some
    /// tools, a signed URL in <c>PreloadFilePath</c>. The file's own name goes in it, as
    /// <c>file download</c> saves under that name.
    /// </remarks>
    internal static void Complete(JsonObject record, string fileSource, string fileName, long size, byte[] md5)
    {
        var data = CliContext.Child(record, "data");
        var bytes = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CliContext.Child(data, "DatasetProperties")["FileSourceInfo"] = new JsonObject
        {
            ["FileSource"] = fileSource,
            ["Name"] = fileName,
            ["FileSize"] = bytes,
            ["Checksum"] = Convert.ToHexStringLower(md5),
            ["ChecksumAlgorithm"] = "MD5",
        };
        data["TotalSize"] = bytes;
    }

    // ---- the service ------------------------------------------------------------------------

    /// <summary>The signed URL to write to, and the location the record refers to the file by.</summary>
    internal static (string SignedUrl, string FileSource) Location(LocationResponse? response)
    {
        var location = response?.Location?.AdditionalData;
        var signedUrl = Text(location, "SignedURL");
        var fileSource = Text(location, "FileSource");
        if (signedUrl is null || fileSource is null)
            throw new OsduException("The File service's upload URL response had no SignedURL or FileSource.");
        return (signedUrl, fileSource);

        static string? Text(IDictionary<string, object>? values, string key) =>
            values is not null && values.TryGetValue(key, out var value)
                ? value switch
                {
                    string text => text,
                    UntypedString untyped => untyped.GetValue(),
                    _ => null,
                } is { Length: > 0 } found ? found : null
                : null;
    }

    /// <summary>
    /// Creates the record as written here, rather than through the client's model, whose round
    /// trip could reshape what a template holds.
    /// </summary>
    internal static async Task<FileMetadataResponse?> PostAsync(
        MetadataRequestBuilder metadata, IRequestAdapter adapter, string json, CancellationToken cancellationToken)
    {
        var request = metadata.ToPostRequestInformation(new FileMetadata());
        request.SetStreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)), "application/json");
        return await adapter.SendAsync(request, FileMetadataResponse.CreateFromDiscriminatorValue,
            new Dictionary<string, ParsableFactory<IParsable>> { ["XXX"] = AppError.CreateFromDiscriminatorValue },
            cancellationToken);
    }
}
