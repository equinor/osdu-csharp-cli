using System.Net;
using System.Text;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Legal;
using Equinor.OsduCsharpClient.Legal.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Text;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Error responses that are a page of text rather than JSON.
/// </summary>
/// <remarks>
/// A tester's environment answered a rejected token with an HTML page. Kiota had no parser for
/// <c>text/html</c>, so building the error model threw an InvalidOperationException that
/// nothing caught, and the command ended in a stack trace instead of
/// <c>error: 401 from the service.</c> These go through a real Kiota adapter and Legal's
/// generated error mapping, since that is where the failure was.
/// </remarks>
public class ErrorPageTests
{
    private const string GatewayPage = """
        <!DOCTYPE html>
        <html>
        <head>
          <title>401 - Unauthorized: Access is denied due to invalid credentials.</title>
          <style>body { font-family: sans-serif; }</style>
        </head>
        <body><h1>Server Error</h1><h2>401 - Unauthorized</h2></body>
        </html>
        """;

    private sealed class Respond(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            });
    }

    /// <summary>A Legal client whose every request gets the given response.</summary>
    private static LegalClient Legal(HttpStatusCode status, string mediaType, string body)
    {
        // As CliContext does, before any client exists.
        TextBodyParseNodeFactory.Register();
        var adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: new HttpClient(new Respond(status, mediaType, body)))
        {
            BaseUrl = "https://osdu.example.com/api/legal/v1",
        };
        return new LegalClient(adapter);
    }

    private static Task ListTags(LegalClient legal) =>
        legal.Legaltags.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnHtmlErrorPageBecomesAnErrorCarryingItsTitle()
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ListTags(Legal(HttpStatusCode.Unauthorized, "text/html", GatewayPage)));

        Assert.Equal(401, exception.ResponseStatusCode);
        Assert.Equal("401 - Unauthorized: Access is denied due to invalid credentials.",
            CliRunner.Describe(exception));
    }

    [Fact]
    public async Task APlainTextErrorBecomesAnErrorCarryingItsText()
    {
        // How a proxy with nothing behind it answers.
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ListTags(Legal(HttpStatusCode.ServiceUnavailable, "text/plain", "no healthy upstream\n")));

        Assert.Equal(503, exception.ResponseStatusCode);
        Assert.Equal("no healthy upstream", CliRunner.Describe(exception));
    }

    [Fact]
    public async Task JsonUnderATextContentTypeIsReadAsJson()
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ListTags(Legal(HttpStatusCode.Forbidden, "text/plain",
                """{"code":403,"reason":"Access denied","message":"The user is not authorized to perform this action"}""")));

        Assert.Equal("The user is not authorized to perform this action", CliRunner.Describe(exception));
    }

    [Fact]
    public async Task APageThatSaysNothingStillGivesTheStatus()
    {
        // A body with no content at all never reaches a parser; Kiota reports that itself.
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ListTags(Legal(HttpStatusCode.BadGateway, "text/html", "<html><body>\n</body></html>")));

        Assert.Equal(502, exception.ResponseStatusCode);
        Assert.True(string.IsNullOrEmpty(CliRunner.Describe(exception)));
    }

    [Fact]
    public async Task APageWhereASuccessfulResponseWasExpectedIsReported()
    {
        // A sign-in screen answered with 200 is not a list of legal tags, and an empty result
        // would look like one.
        var exception = await Assert.ThrowsAsync<OsduException>(() =>
            ListTags(Legal(HttpStatusCode.OK, "text/html", "<html><title>Sign in to your account</title></html>")));

        Assert.Equal(
            "The service answered with an HTML page where JSON was expected: Sign in to your account",
            exception.Message);
    }

    [Fact]
    public void APageWhereAListWasExpectedIsReported()
    {
        // Kiota's text parser threw for a list, which escaped as a stack trace.
        var node = new TextBodyParseNode("<html><title>Sign in to your account</title></html>", html: true);

        var exception = Assert.Throws<OsduException>(() =>
            node.GetCollectionOfObjectValues(AppError.CreateFromDiscriminatorValue));

        Assert.Equal(
            "The service answered with an HTML page where JSON was expected: Sign in to your account",
            exception.Message);
    }

    [Fact]
    public void AJsonListUnderATextContentTypeIsReadAsJson()
    {
        var node = new TextBodyParseNode("""[{"message":"a"},{"message":"b"}]""", html: false);

        var errors = node.GetCollectionOfObjectValues(AppError.CreateFromDiscriminatorValue).ToList();

        Assert.Equal(["a", "b"], errors.Select(error => error.Message));
    }

    [Fact]
    public void AClientRegisteringKiotasTextParserDoesNotDisplaceThisOne()
    {
        // Registered when the context is built, before any client; a client registers its
        // parsers when it is constructed, later. Kiota adds rather than replaces, which this holds it to.
        TextBodyParseNodeFactory.Register();

        ApiClientBuilder.RegisterDefaultDeserializer<TextParseNodeFactory>();

        Assert.IsType<TextBodyParseNodeFactory>(
            ParseNodeFactoryRegistry.DefaultInstance.ContentTypeAssociatedFactories["text/plain"]);
    }

    [Fact]
    public async Task PlainTextThatIsAnAnswerIsReadAsBefore()
    {
        var node = await new TextBodyParseNodeFactory("text/plain")
            .GetRootParseNodeAsync("text/plain", new MemoryStream(Encoding.UTF8.GetBytes("42")),
                TestContext.Current.CancellationToken);

        Assert.Equal("42", node.GetStringValue());
        Assert.Equal(42, node.GetIntValue());
    }

    [Theory]
    [InlineData(GatewayPage, "401 - Unauthorized: Access is denied due to invalid credentials.")]
    // No title: the first heading, with entities decoded.
    [InlineData("<html><body><h1>Bad &amp; Gateway</h1><p>Try later</p></body></html>", "Bad & Gateway")]
    // Escaped text is text: tags go before entities are decoded, not after.
    [InlineData("<title>Invalid &lt;token&gt; in <b>header</b></title>", "Invalid <token> in header")]
    // A title holding nothing is no title.
    [InlineData("<title> </title><h1>Service <b>down</b></h1>", "Service down")]
    // Neither: the visible text, without scripts, styles or comments.
    [InlineData("<html><script>var x = '<p>';</script><!-- hidden --><p>Service\n   unavailable</p></html>",
        "Service unavailable")]
    public void AnHtmlPageIsSummarisedByWhatItSays(string html, string expected)
    {
        Assert.Equal(expected, TextBodyParseNode.Summarise(html, html: true));
    }

    [Fact]
    public void PlainTextIsSummarisedOnOneLine()
    {
        Assert.Equal("upstream connect error or disconnect",
            TextBodyParseNode.Summarise("upstream connect error\n  or disconnect\n", html: false));
    }

    [Fact]
    public void ALongSummaryIsCut()
    {
        var summary = TextBodyParseNode.Summarise(new string('x', 500), html: false);

        Assert.Equal(201, summary.Length);
        Assert.EndsWith("…", summary);
    }
}
