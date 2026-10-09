using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Moves a file's bytes to or from the signed storage URL the File service hands out, for
/// <c>file upload</c> and <c>file download</c>.
/// </summary>
/// <remarks>
/// <para>The URL carries its own authorisation, so the request goes through a client of its
/// own rather than the service client: that one would add the OSDU token, which has no
/// business reaching a storage account.</para>
///
/// <para>Neither the URL nor anything in its query is ever put in a message. Whoever holds it
/// can read or write the blob until it expires.</para>
/// </remarks>
internal static partial class SignedUrlTransfer
{
    /// <summary>
    /// The largest file one request may write: Azure's limit for a single Put Blob. Larger
    /// files would have to be sent in blocks, which this does not do yet.
    /// </summary>
    internal const long MaxUploadBytes = 5000L * 1024 * 1024;

    private const int BufferSize = 1024 * 1024;

    /// <summary>The client for storage URLs: no token, no timeout but the command's own.</summary>
    /// <remarks>
    /// A large file takes as long as it takes; the default 100 seconds stopped a slow
    /// download part way through. Ctrl+C still cancels it.
    /// </remarks>
    internal static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>What a download received.</summary>
    /// <param name="Bytes">How many bytes were written.</param>
    /// <param name="Hash">Their hash, in the algorithm asked for.</param>
    /// <param name="StorageMd5">The MD5 storage reported for the blob, when it keeps one.</param>
    internal sealed record Received(long Bytes, byte[] Hash, byte[]? StorageMd5);

    /// <summary>Copies the blob at <paramref name="url"/> into <paramref name="destination"/>, hashing as it goes.</summary>
    internal static async Task<Received> DownloadAsync(
        HttpClient http, string url, Stream destination, HashAlgorithmName algorithm,
        TextWriter? progress, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            http, new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead,
            "download", cancellationToken);

        var total = response.Content.Headers.ContentLength;
        using var hash = IncrementalHash.CreateHash(algorithm);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[BufferSize];
        var meter = new Meter("Downloaded", total, progress);
        long copied = 0;
        try
        {
            int read;
            while ((read = await ReadAsync(source, buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                meter.Report(copied);
            }
        }
        finally
        {
            meter.Finish();
        }

        return new Received(copied, hash.GetHashAndReset(), response.Content.Headers.ContentMD5);
    }

    /// <summary>
    /// Writes <paramref name="length"/> bytes from <paramref name="source"/> to the blob at
    /// <paramref name="url"/>, for storage to check against <paramref name="md5"/>.
    /// </summary>
    /// <remarks>
    /// Azure refuses a write to a signed URL without <c>x-ms-blob-type</c>, and with
    /// <c>Content-MD5</c> it refuses bytes that arrived different from those sent, rather
    /// than storing them.
    /// </remarks>
    internal static async Task UploadAsync(
        HttpClient http, string url, Stream source, long length, byte[] md5,
        TextWriter? progress, CancellationToken cancellationToken)
    {
        var content = new MeteredContent(source, length, new Meter("Uploaded", length, progress));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;
        content.Headers.ContentMD5 = md5;

        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");

        using var response = await SendAsync(http, request, HttpCompletionOption.ResponseContentRead, "upload",
            cancellationToken);
    }

    /// <summary>Sends <paramref name="request"/>, turning a failure into a message that names no URL.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, HttpCompletionOption completion, string what,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // The host is enough to tell a proxy or firewall problem; the rest of the URL is
            // the signature.
            throw new OsduException(
                $"The {what} could not reach storage at {request.RequestUri?.Host}: {exception.Message}");
        }
        finally
        {
            request.Dispose();
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var detail = ErrorCode(await response.Content.ReadAsStringAsync(cancellationToken));
            throw new OsduException(
                $"Storage refused the {what}: {(int)response.StatusCode} {response.ReasonPhrase}"
                + (detail is null ? "." : $" ({detail}).")
                + (response.StatusCode == HttpStatusCode.Forbidden
                    ? " The signed URL may have expired; run the command again for a new one."
                    : ""));
        }
    }

    /// <summary>Azure's error code from its XML error body, such as <c>Md5Mismatch</c>.</summary>
    internal static string? ErrorCode(string body) =>
        CodePattern().Match(body) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex("<Code>([^<]{1,100})</Code>")]
    private static partial Regex CodePattern();

    /// <summary>Reads until the buffer is full or the stream ends, so each write is a whole buffer.</summary>
    private static async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
                break;
            filled += read;
        }
        return filled;
    }

    /// <summary>A file's size the way a person reads it, in decimal units as the OS shows them.</summary>
    internal static string Size(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} bytes",
        < 1_000_000 => (bytes / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " kB",
        < 1_000_000_000 => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>Reports progress on one line, a few times a second rather than every buffer.</summary>
    private sealed class Meter(string verb, long? total, TextWriter? progress)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
        private DateTime _last = DateTime.MinValue;
        private long _done;
        private long _shown = -1;

        public void Report(long done)
        {
            _done = done;
            if (progress is null || DateTime.UtcNow - _last < Interval)
                return;
            _last = DateTime.UtcNow;
            Write();
        }

        public void Finish()
        {
            if (progress is null || _shown < 0)
                return;
            // The last figure, when the interval skipped it, and the line ended.
            if (_shown != _done)
                Write();
            progress.WriteLine();
        }

        private void Write()
        {
            _shown = _done;
            progress!.Write(total is { } all and > 0
                ? $"\r{verb} {Size(_done)} of {Size(all)}"
                : $"\r{verb} {Size(_done)}");
        }
    }

    /// <summary>A file's bytes as a request body, reporting how far the upload has got.</summary>
    private sealed class MeteredContent(Stream source, long length, Meter meter) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            long sent = 0;
            try
            {
                int read;
                while (sent < length && (read = await source.ReadAsync(
                           buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), cancellationToken)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sent += read;
                    meter.Report(sent);
                }
            }
            finally
            {
                meter.Finish();
            }

            // A file that shrank while it was read would otherwise leave the request short of
            // its declared length, which surfaces as an obscure transport error.
            if (sent != length)
                throw new IOException($"The file changed while it was being uploaded: {sent} of {length} bytes were read.");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
