using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.FileNamespace;
using Equinor.OsduCsharpClient.FileNamespace.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>file upload</c> and <c>file download</c>: a file's bytes through the File service's
/// signed URLs, checked against the checksum its record holds.
/// </summary>
public sealed class FileTransferTests : IDisposable
{
    private const string SignedUrl = "https://account.blob.core.windows.net/staging/a/b?sv=2025-05-05&sig=SECRET";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("osducs-file-");

    public void Dispose() => _directory.Delete(recursive: true);

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("~Version information\nVERS. 2.0\n");
    private static string Md5Hex(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    /// <summary>Storage, as far as a signed URL sees it: answers every request the same way.</summary>
    private sealed class Storage(HttpStatusCode status, byte[]? body = null, byte[]? contentMd5 = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public byte[]? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Sent = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = new ByteArrayContent(body ?? []);
            if (contentMd5 is not null)
                content.Headers.ContentMD5 = contentMd5;
            return new HttpResponseMessage(status) { Content = content };
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (account.blob.core.windows.net:443)");
    }

    // ---- moving bytes -----------------------------------------------------------------------

    [Fact]
    public async Task AnUploadIsABlockBlobWrittenWithItsChecksumAndNoToken()
    {
        var storage = new Storage(HttpStatusCode.Created);
        var md5 = MD5.HashData(Bytes);

        await SignedUrlTransfer.UploadAsync(new HttpClient(storage), SignedUrl, new MemoryStream(Bytes), Bytes.Length, md5,
            null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, storage.Request!.Method);
        Assert.Equal(["BlockBlob"], storage.Request.Headers.GetValues("x-ms-blob-type"));
        Assert.Equal(md5, storage.Request.Content!.Headers.ContentMD5);
        Assert.Equal("application/octet-stream", storage.Request.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Bytes, storage.Sent);
        // The URL carries its own authorisation; the OSDU token has no business there.
        Assert.Null(storage.Request.Headers.Authorization);
    }

    [Fact]
    public async Task ARefusalSaysWhyWithoutTheSignature()
    {
        var storage = new Storage(HttpStatusCode.BadRequest,
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Error><Code>InvalidBlobType</Code><Message>no</Message></Error>"));

        var exception = await Assert.ThrowsAsync<OsduException>(() => SignedUrlTransfer.UploadAsync(
            new HttpClient(storage), SignedUrl, new MemoryStream(Bytes), Bytes.Length, MD5.HashData(Bytes), null,
            TestContext.Current.CancellationToken));

        Assert.Equal("Storage refused the upload: 400 Bad Request (InvalidBlobType).", exception.Message);
    }

    [Fact]
    public async Task AForbiddenAnswerSuggestsAnExpiredUrl()
    {
        var exception = await Assert.ThrowsAsync<OsduException>(() => SignedUrlTransfer.DownloadAsync(
            new HttpClient(new Storage(HttpStatusCode.Forbidden)), SignedUrl, new MemoryStream(), HashAlgorithmName.MD5, null,
            TestContext.Current.CancellationToken));

        Assert.Contains("may have expired", exception.Message);
    }

    [Fact]
    public async Task AnUnreachableStorageIsNamedByItsHostAlone()
    {
        var exception = await Assert.ThrowsAsync<OsduException>(() => SignedUrlTransfer.DownloadAsync(
            new HttpClient(new Unreachable()), SignedUrl, new MemoryStream(), HashAlgorithmName.MD5, null,
            TestContext.Current.CancellationToken));

        Assert.Contains("account.blob.core.windows.net", exception.Message);
        Assert.DoesNotContain("SECRET", exception.Message);
    }

    [Fact]
    public async Task ADownloadHashesWhatItWrites()
    {
        var stored = MD5.HashData(Bytes);
        var destination = new MemoryStream();

        var received = await SignedUrlTransfer.DownloadAsync(
            new HttpClient(new Storage(HttpStatusCode.OK, Bytes, stored)), SignedUrl, destination, HashAlgorithmName.SHA256,
            null, TestContext.Current.CancellationToken);

        Assert.Equal(Bytes, destination.ToArray());
        Assert.Equal(Bytes.Length, received.Bytes);
        Assert.Equal(SHA256.HashData(Bytes), received.Hash);
        Assert.Equal(stored, received.StorageMd5);
    }

    [Fact]
    public async Task ProgressEndsItsLine()
    {
        var progress = new StringWriter();

        await SignedUrlTransfer.DownloadAsync(new HttpClient(new Storage(HttpStatusCode.OK, Bytes)), SignedUrl,
            new MemoryStream(), HashAlgorithmName.MD5, progress, TestContext.Current.CancellationToken);

        Assert.Equal($"\rDownloaded {Bytes.Length} bytes of {Bytes.Length} bytes" + Environment.NewLine, progress.ToString());
    }

    [Theory]
    [InlineData(999, "999 bytes")]
    [InlineData(832_696, "832.7 kB")]
    [InlineData(4_903_168, "4.9 MB")]
    [InlineData(SignedUrlTransfer.MaxUploadBytes, "5.24 GB")]
    public void SizesReadAsTheOperatingSystemShowsThem(long bytes, string expected) =>
        Assert.Equal(expected, SignedUrlTransfer.Size(bytes));

    // ---- download: what the record says ------------------------------------------------------

    private static JsonObject FileRecord(string? name = "log.las", string? checksum = null, string? algorithm = null,
        string? dataName = "Friendly name", string source = "/osdu-user/1/abc") =>
        new()
        {
            ["id"] = "dev:dataset--File.Generic:1",
            ["data"] = new JsonObject
            {
                ["Name"] = dataName,
                ["DatasetProperties"] = new JsonObject
                {
                    ["FileSourceInfo"] = new JsonObject
                    {
                        ["FileSource"] = source,
                        ["Name"] = name,
                        ["Checksum"] = checksum,
                        ["ChecksumAlgorithm"] = algorithm,
                    },
                },
            },
        };

    [Fact]
    public void TheFileIsNamedAsItsRecordNamesIt()
    {
        Assert.Equal("log.las", FileDownloadCommand.Describe(FileRecord()).Name);
        Assert.Equal("Friendly name", FileDownloadCommand.Describe(FileRecord(name: null)).Name);
        Assert.Equal("abc", FileDownloadCommand.Describe(FileRecord(name: null, dataName: null)).Name);
    }

    [Theory]
    // A name from a record anyone with write access could have set: it cannot leave the directory.
    [InlineData("../../.bashrc", ".._.._.bashrc")]
    [InlineData(@"..\..\evil.dll", ".._.._evil.dll")]
    [InlineData("C:report?.pdf", "C_report_.pdf")]
    [InlineData("no_6608!10-17_s~jan.las", "no_6608!10-17_s~jan.las")]
    // Windows refuses a trailing dot or space, and a device name whatever its case or extension.
    [InlineData("log.las. .", "log.las")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1.tar.gz", "_Com1.tar.gz")]
    [InlineData("LPT¹", "_LPT¹")]
    [InlineData("CONSOLE.log", "CONSOLE.log")]
    [InlineData("COM10", "COM10")]
    [InlineData("..", "1")]
    [InlineData("...", "1")]
    [InlineData("ø", "ø")]
    [InlineData(null, "1")]
    public void ANameIsSafeToSaveUnder(string? name, string expected) =>
        Assert.Equal(expected, FileDownloadCommand.SafeFileName(name, "dev:dataset--File.Generic:1"));

    [Theory]
    // Bytes, not characters, are what Linux and macOS count: ø is two of them, and 👍🏽 eight.
    [InlineData("a", ".las")]
    [InlineData("ø", ".las")]
    [InlineData("👍🏽", ".las")]
    [InlineData("a", "")]
    public void ALongNameIsCutKeepingItsExtension(string repeated, string extension)
    {
        var name = string.Concat(Enumerable.Repeat(repeated, 300)) + extension;

        var safe = FileDownloadCommand.SafeFileName(name, "dev:x:1");

        Assert.InRange(Encoding.UTF8.GetByteCount(safe), FileDownloadCommand.MaxNameBytes - 8, FileDownloadCommand.MaxNameBytes);
        Assert.EndsWith(repeated + extension, safe);
    }

    [Fact]
    public void TheDestinationIsThePathOrTheRecordsNameInIt()
    {
        var id = "dev:dataset--File.Generic:1";
        Assert.Equal(Path.GetFullPath("log.las"), FileDownloadCommand.Destination(null, "log.las", id));
        Assert.Equal(Path.Combine(_directory.FullName, "log.las"), FileDownloadCommand.Destination(_directory.FullName, "log.las", id));
        var missing = Path.Combine(_directory.FullName, "new") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine(missing, "log.las"), FileDownloadCommand.Destination(missing, "log.las", id));
        var named = Path.Combine(_directory.FullName, "mine.las");
        Assert.Equal(named, FileDownloadCommand.Destination(named, "log.las", id));
    }

    // ---- download: saving and checking -------------------------------------------------------

    private Task<FileDownloadCommand.Saved> Save(Storage storage, JsonObject record, string? destination = null, bool force = false) =>
        FileDownloadCommand.SaveAsync(new HttpClient(storage), SignedUrl,
            destination ?? Path.Combine(_directory.FullName, "log.las"), force,
            FileDownloadCommand.Describe(record), null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AFileMatchingItsRecordIsSaved()
    {
        var saved = await Save(new Storage(HttpStatusCode.OK, Bytes), FileRecord(checksum: Md5Hex(Bytes), algorithm: "MD5"));

        Assert.Equal("MD5 matches the record", saved.Outcome);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(_directory.FullName, "log.las"), TestContext.Current.CancellationToken));
        Assert.Equal(["log.las"], _directory.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public async Task AFileNotMatchingItsRecordIsNotSavedAtAll()
    {
        var record = FileRecord(checksum: Md5Hex(Encoding.UTF8.GetBytes("something else")), algorithm: "MD5");

        var exception = await Assert.ThrowsAsync<OsduException>(() => Save(new Storage(HttpStatusCode.OK, Bytes), record));

        Assert.StartsWith("The downloaded bytes do not match the record's MD5 checksum", exception.Message);
        // Neither under its name nor half-written beside it.
        Assert.Empty(_directory.GetFiles());
    }

    [Fact]
    public async Task AFailedDownloadLeavesNothingBehind()
    {
        await Assert.ThrowsAsync<OsduException>(() => Save(new Storage(HttpStatusCode.Forbidden), FileRecord()));

        Assert.Empty(_directory.GetFiles());
    }

    [Fact]
    public async Task ADirectoryThatDoesNotExistYetIsCreated()
    {
        var destination = FileDownloadCommand.Destination(
            Path.Combine(_directory.FullName, "logs", "2026") + Path.DirectorySeparatorChar, "log.las", "dev:x:1");

        await Save(new Storage(HttpStatusCode.OK, Bytes), FileRecord(), destination);

        Assert.Equal(Bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    /// <summary>Holds every response until <paramref name="expected"/> requests are in flight at once.</summary>
    private sealed class Together(int expected) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == expected)
                _all.SetResult();
            await _all.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        }
    }

    [Fact]
    public async Task TwoDownloadsToOnePathDoNotShareAPartialFile()
    {
        // Each opens its partial file before asking for the bytes, so both are open together:
        // under one fixed name, the second could not open it.
        var destination = Path.Combine(_directory.FullName, "log.las");
        var http = new HttpClient(new Together(2));
        var source = FileDownloadCommand.Describe(FileRecord());

        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => FileDownloadCommand.SaveAsync(
            http, SignedUrl, destination, force: true, source, null, TestContext.Current.CancellationToken)));

        Assert.Equal(["log.las"], _directory.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public async Task ANameNearTheLengthLimitCanBeDownloaded()
    {
        // Windows' limit on the whole path depends on a system setting; the component limit
        // tested here is what a partial name built on the destination's broke.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the full path, not the name, is what Windows limits by default");
        var destination = Path.Combine(_directory.FullName, new string('a', 246) + ".las");

        await Save(new Storage(HttpStatusCode.OK, Bytes), FileRecord(), destination);

        Assert.Equal(Bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExistingFileIsReplacedOnlyWhenForced()
    {
        var destination = Path.Combine(_directory.FullName, "log.las");
        await File.WriteAllTextAsync(destination, "mine", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => Save(new Storage(HttpStatusCode.OK, Bytes), FileRecord(), destination));
        Assert.Equal("mine", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));

        await Save(new Storage(HttpStatusCode.OK, Bytes), FileRecord(), destination, force: true);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(_directory.GetFiles());
    }

    [Theory]
    [InlineData("SHA-256")]
    [InlineData("SHA256")]
    [InlineData(null)]
    public async Task ASha256ChecksumIsCheckedToo(string? algorithm)
    {
        var record = FileRecord(checksum: Convert.ToHexString(SHA256.HashData(Bytes)), algorithm: algorithm);

        var saved = await Save(new Storage(HttpStatusCode.OK, Bytes), record);

        Assert.Equal("SHA-256 matches the record", saved.Outcome);
    }

    [Fact]
    public async Task WithoutARecordedChecksumStoragesMd5IsUsed()
    {
        var saved = await Save(new Storage(HttpStatusCode.OK, Bytes, MD5.HashData(Bytes)), FileRecord());

        Assert.Equal("MD5 matches storage", saved.Outcome);
    }

    [Fact]
    public async Task ABlobNotMatchingStoragesMd5IsNotSaved()
    {
        var wrong = MD5.HashData(Encoding.UTF8.GetBytes("something else"));

        await Assert.ThrowsAsync<OsduException>(() => Save(new Storage(HttpStatusCode.OK, Bytes, wrong), FileRecord()));

        Assert.Empty(_directory.GetFiles());
    }

    [Theory]
    [InlineData(null, null, "not checked: none recorded")]
    [InlineData("1a2b3c4d", "CRC32C", "not checked: CRC32C not supported")]
    // Broken records, not unsupported algorithms.
    [InlineData("not hex at all", "MD5", "not checked: the record's MD5 checksum is malformed")]
    [InlineData("1a2b3c4d", "SHA-256", "not checked: the record's SHA-256 checksum is malformed")]
    [InlineData("1a2b3c4d", null, "not checked: the record's checksum is neither MD5 nor SHA-256")]
    public async Task WhatCannotBeCheckedIsSaidSo(string? checksum, string? algorithm, string expected)
    {
        var saved = await Save(new Storage(HttpStatusCode.OK, Bytes), FileRecord(checksum: checksum, algorithm: algorithm));

        Assert.Equal(expected, saved.Outcome);
    }

    // ---- upload: the record ------------------------------------------------------------------

    private static readonly FileUploadCommand.Settings Everything = new(
        null, null, ["dev-private"], ["no"],
        ["data.default.owners@dev.dataservices.energy"], ["data.default.viewers@dev.dataservices.energy"]);

    [Fact]
    public void TheOptionsMakeARecord()
    {
        var record = FileUploadCommand.Record(null, Everything with { Countries = ["no", "NO"] }, "log.las");

        Assert.Equal(FileUploadCommand.DefaultKind, (string?)record["kind"]);
        Assert.Equal("""["data.default.owners@dev.dataservices.energy"]""", record["acl"]!["owners"]!.ToJsonString());
        Assert.Equal("""["dev-private"]""", record["legal"]!["legaltags"]!.ToJsonString());
        Assert.Equal("""["NO"]""", record["legal"]!["otherRelevantDataCountries"]!.ToJsonString());
        Assert.Equal("log.las", (string?)record["data"]!["Name"]);
    }

    [Fact]
    public void TheOptionsWinOverTheTemplateWhichKeepsTheRest()
    {
        var template = FileUploadCommand.Template("""
            {"kind":"osdu:wks:dataset--File.Generic:1.1.0",
             "acl":{"owners":["data.other.owners@dev.dataservices.energy"],"viewers":["data.other.viewers@dev.dataservices.energy"]},
             "legal":{"legaltags":["dev-other"],"otherRelevantDataCountries":["GB"]},
             "data":{"Name":"From template","Source":"Geolog","Description":"old"}}
            """);

        var record = FileUploadCommand.Record(template,
            new FileUploadCommand.Settings(null, "new", [], [], ["data.default.owners@dev.dataservices.energy"], []), "log.las");

        Assert.Equal("osdu:wks:dataset--File.Generic:1.1.0", (string?)record["kind"]);
        Assert.Equal("""["data.default.owners@dev.dataservices.energy"]""", record["acl"]!["owners"]!.ToJsonString());
        Assert.Equal("""["data.other.viewers@dev.dataservices.energy"]""", record["acl"]!["viewers"]!.ToJsonString());
        Assert.Equal("""["GB"]""", record["legal"]!["otherRelevantDataCountries"]!.ToJsonString());
        Assert.Equal("From template", (string?)record["data"]!["Name"]);
        Assert.Equal("Geolog", (string?)record["data"]!["Source"]);
        Assert.Equal("new", (string?)record["data"]!["Description"]);
    }

    [Theory]
    [InlineData("""{"id":"dev:dataset--File.Generic:1","kind":"k"}""", "has an id")]
    [InlineData("""[{"kind":"k"}]""", "one record")]
    public void ATemplateMustBeANewRecord(string json, string expected)
    {
        var exception = Assert.Throws<OsduException>(() => FileUploadCommand.Template(json));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void EveryMissingSettingIsListedAtOnce()
    {
        var exception = Assert.Throws<OsduException>(() =>
            FileUploadCommand.Record(null, new FileUploadCommand.Settings(null, null, [], [], [], []), "log.las"));

        Assert.Equal(
            string.Join(Environment.NewLine,
                "The file's record cannot be created; nothing was uploaded.",
                "  legal.legaltags is missing: pass --legal-tag, or set it in --metadata",
                "  legal.otherRelevantDataCountries is missing: pass --country, or set it in --metadata",
                "  acl.owners is missing: pass --owner, or set it in --metadata",
                "  acl.viewers is missing: pass --viewer, or set it in --metadata"),
            exception.Message);
    }

    [Fact]
    public void GroupsAndCountriesAreCheckedBeforeAnythingIsSent()
    {
        var exception = Assert.Throws<OsduException>(() => FileUploadCommand.Record(null,
            Everything with { Countries = ["Norway"], Owners = ["data.default.owners"] }, "log.las"));

        Assert.Contains("'NORWAY' is not a two-letter country code", exception.Message);
        Assert.Contains("'data.default.owners' is not a data group", exception.Message);
    }

    [Fact]
    public void TheBytesAreDescribedByThisUploadAlone()
    {
        // As copied from an existing record: another upload's checksum, and a signed URL that
        // some tools leave in PreloadFilePath.
        var record = FileUploadCommand.Record(FileUploadCommand.Template("""
            {"data":{"DatasetProperties":{"FileSourceInfo":{"FileSource":"/old","Checksum":"00",
             "PreloadFilePath":"https://account.blob.core.windows.net/x?sig=SECRET"}}}}
            """), Everything, "log.las");

        FileUploadCommand.Complete(record, "/osdu-user/2/def", "log.las", Bytes.Length, MD5.HashData(Bytes));

        Assert.Equal(
            $$"""{"FileSource":"/osdu-user/2/def","Name":"log.las","FileSize":"{{Bytes.Length}}","Checksum":"{{Md5Hex(Bytes)}}","ChecksumAlgorithm":"MD5"}""",
            record["data"]!["DatasetProperties"]!["FileSourceInfo"]!.ToJsonString());
        Assert.Equal(Bytes.Length.ToString(), (string?)record["data"]!["TotalSize"]);
    }

    [Fact]
    public void ATemplatePartOfTheWrongShapeIsReportedWithTheRest()
    {
        var template = FileUploadCommand.Template("""
            {"acl":[],"legal":"dev-private","data":{"Name":5,"DatasetProperties":[]}}
            """);

        var exception = Assert.Throws<OsduException>(() => FileUploadCommand.Record(template, Everything, "log.las"));

        Assert.Equal(
            string.Join(Environment.NewLine,
                "The file's record cannot be created; nothing was uploaded.",
                "  acl must be an object, not array",
                "  legal must be an object, not string",
                "  data.DatasetProperties must be an object, not array",
                "  data.Name must be a string"),
            exception.Message);
    }

    [Fact]
    public void AFileTooLargeForOneRequestIsRefused()
    {
        FileUploadCommand.CheckSize("big.segy", SignedUrlTransfer.MaxUploadBytes);

        var exception = Assert.Throws<OsduException>(() =>
            FileUploadCommand.CheckSize("big.segy", SignedUrlTransfer.MaxUploadBytes + 1));
        Assert.Equal("big.segy is 5.24 GB, and one upload takes at most 5.24 GB.", exception.Message);
    }

    [Fact]
    public void AMissingFileOrADirectoryIsRefused()
    {
        Assert.Contains("File not found", Assert.Throws<OsduException>(() =>
            FileUploadCommand.Local(Path.Combine(_directory.FullName, "missing.las"))).Message);
        Assert.Contains("is a directory", Assert.Throws<OsduException>(() =>
            FileUploadCommand.Local(_directory.FullName)).Message);
    }

    // ---- upload: the service -----------------------------------------------------------------

    [Fact]
    public async Task AFileThatChangesDuringTheUploadIsSaidToHaveChanged()
    {
        // Shorter than measured. HttpClient wraps the failure reading the body as its own,
        // which read as storage being unreachable.
        var exception = await Assert.ThrowsAsync<OsduException>(() => SignedUrlTransfer.UploadAsync(
            new HttpClient(new Storage(HttpStatusCode.Created)), SignedUrl, new MemoryStream(Bytes[..10]), Bytes.Length,
            MD5.HashData(Bytes), null, TestContext.Current.CancellationToken));

        Assert.Equal($"The file changed while it was being uploaded: 10 of {Bytes.Length} bytes were read.", exception.Message);
    }

    [Fact]
    public async Task AChecksumMismatchSuggestsAChangedFile()
    {
        var storage = new Storage(HttpStatusCode.BadRequest, Encoding.UTF8.GetBytes("<Error><Code>Md5Mismatch</Code></Error>"));

        var exception = await Assert.ThrowsAsync<OsduException>(() => SignedUrlTransfer.UploadAsync(
            new HttpClient(storage), SignedUrl, new MemoryStream(Bytes), Bytes.Length, MD5.HashData(Bytes), null,
            TestContext.Current.CancellationToken));

        Assert.Contains("it may have changed while it was being uploaded", exception.Message);
    }

    public static TheoryData<Exception, string> PostFailures => new()
    {
        { new ApiException("refused") { ResponseStatusCode = 400 }, "refused its metadata record, so nothing refers to them" },
        // The record may have been created before the answer was lost.
        { new ApiException("failed") { ResponseStatusCode = 500 }, "could not be confirmed" },
        { new HttpRequestException("Connection reset"), "could not be confirmed" },
        { new OperationCanceledException(), "could not be confirmed" },
    };

    [Theory]
    [MemberData(nameof(PostFailures))]
    public async Task AFailureAfterTheUploadSaysWhatBecameOfTheBytes(Exception failure, string expected)
    {
        var error = new StringWriter();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => FileUploadCommand.CreateAsync(
            () => throw failure, "/osdu-user/2/def", new OutputWriter(OutputFormat.Table, new StringWriter(), error)));

        Assert.Same(failure, thrown);
        Assert.Contains("uploaded to /osdu-user/2/def", error.ToString());
        Assert.Contains(expected, error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAnswerWithoutAnIdIsNotASuccess(bool empty)
    {
        var error = new StringWriter();

        var exception = await Assert.ThrowsAsync<OsduException>(() => FileUploadCommand.CreateAsync(
            () => Task.FromResult(empty ? new FileMetadataResponse { Id = " " } : null), "/osdu-user/2/def",
            new OutputWriter(OutputFormat.Table, new StringWriter(), error)));

        Assert.Equal("The File service accepted the metadata record but did not return its id.", exception.Message);
        Assert.Contains("could not be confirmed", error.ToString());
    }

    [Fact]
    public async Task TheNewRecordsIdIsReturned()
    {
        var id = await FileUploadCommand.CreateAsync(
            () => Task.FromResult<FileMetadataResponse?>(new FileMetadataResponse { Id = "dev:dataset--File.Generic:2" }),
            "/osdu-user/2/def", new OutputWriter(OutputFormat.Table, new StringWriter(), new StringWriter()));

        Assert.Equal("dev:dataset--File.Generic:2", id);
    }

    [Fact]
    public void TheUploadLocationIsReadFromTheResponse()
    {
        var response = new LocationResponse
        {
            Location = new LocationResponse_Location
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["SignedURL"] = new UntypedString(SignedUrl),
                    ["FileSource"] = "/osdu-user/2/def",
                },
            },
        };

        Assert.Equal((SignedUrl, "/osdu-user/2/def"), FileUploadCommand.Location(response));
        Assert.Throws<OsduException>(() => FileUploadCommand.Location(new LocationResponse()));
    }

    private sealed class Capture(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static (FileClient Client, HttpClientRequestAdapter Adapter) FileService(Capture capture)
    {
        TextBodyParseNodeFactory.Register();
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(capture))
        {
            BaseUrl = "https://osdu.example.com/api/file",
        };
        return (new FileClient(adapter), adapter);
    }

    [Fact]
    public async Task TheRecordIsSentAsWritten()
    {
        const string json = """{"kind":"k","data":{"Ratio":1.50}}""";
        var capture = new Capture(HttpStatusCode.Created, """{"id":"dev:dataset--File.Generic:2"}""");
        var (client, adapter) = FileService(capture);

        var created = await FileUploadCommand.PostAsync(client.V2.Files.Metadata, adapter, json, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, capture.Request!.Method);
        Assert.Equal("https://osdu.example.com/api/file/v2/files/metadata", capture.Request.RequestUri!.ToString());
        Assert.Equal(json, capture.Body);
        Assert.Equal("dev:dataset--File.Generic:2", created!.Id);
    }

    [Fact]
    public async Task ARefusedRecordIsReportedLikeAnyServiceError()
    {
        var (client, adapter) = FileService(new Capture(HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad","message":"Invalid legal tags"}"""));

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => FileUploadCommand.PostAsync(
            client.V2.Files.Metadata, adapter, "{}", TestContext.Current.CancellationToken));

        Assert.Equal(400, exception.ResponseStatusCode);
        Assert.Equal("Invalid legal tags", CliRunner.Describe(exception));
    }

    // ---- the command tree --------------------------------------------------------------------

    [Theory]
    [InlineData("upload")]
    [InlineData("download")]
    public void TheCommandsAreUnderFile(string verb)
    {
        var root = new RootCommand("osducs");
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);

        Assert.Contains(root.Subcommands.Single(c => c.Name == "file").Subcommands, c => c.Name == verb);
    }
}
