using System.Text;
using System.Text.Json;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// A response the spec gives no content, which Kiota hands back as a <see cref="Stream"/>:
/// Indexer's reindex and provision operations.
/// </summary>
public class StreamResponseTests
{
    private static Stream Body(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task JsonIsPassedThrough() =>
        Assert.Equal("""{"status":"queued"}""", await OsduJson.ToJsonAsync(Body("""{"status":"queued"}""")));

    [Fact]
    public async Task TextBecomesAJsonString() =>
        Assert.Equal("\"Reindex started\"", await OsduJson.ToJsonAsync(Body("Reindex started")));

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task AnEmptyBodyIsNoOutput(string text) =>
        Assert.Null(await OsduJson.ToJsonAsync(Body(text)));

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Sign in to your account</title></head></html>")]
    [InlineData("\n  <html><title>Sign in to your account</title></html>")]
    public async Task AnHtmlPageIsReportedNotPrinted(string page)
    {
        // A sign-in screen answered with 200 is not the reindex having started.
        var exception = await Assert.ThrowsAsync<OsduException>(() => OsduJson.ToJsonAsync(Body(page)));

        Assert.Equal(
            "The service answered with an HTML page where a result was expected: Sign in to your account",
            exception.Message);
    }

    [Fact]
    public async Task TextThatOnlyMentionsMarkupIsStillText() =>
        Assert.Equal("Use <html> output",
            JsonSerializer.Deserialize<string>((await OsduJson.ToJsonAsync(Body("Use <html> output")))!));

    [Fact]
    public async Task ReadingStopsWhenTheCommandIsCancelled()
    {
        // A body that never finishes, as when a response stalls after its headers.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OsduJson.ToJsonAsync(new StalledStream(), cancellation.Token));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
