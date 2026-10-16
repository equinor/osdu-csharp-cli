using System.Net;
using System.Text;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Register;
using Equinor.OsduCsharpClient.Register.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;
using SecretPutRequestBody = Equinor.OsduCsharpClient.Register.Subscription.Item.Secret.SecretRequestBuilder.SecretPutRequestBody;

namespace OsduCli.Tests;

/// <summary>
/// The subscription commands whose bodies carry a secret: sent and read as JSON, since the
/// client's models lost it, and kept out of <c>--debug</c>.
/// </summary>
public class SubscriptionSecretsTests
{
    private const string Hmac = """{"secretType":"HMAC","value":"s3cret"}""";
    private const string Gsa = """{"secretType":"GSA","value":{"audience":"https://push.example.com","key":"eyJrZXkiOiJ2YWx1ZSJ9"}}""";

    private static string SubscriptionWith(string secret) =>
        $$"""{"name":"orders","topic":"recordstopic","pushEndpoint":"https://push.example.com/hook","secret":{{secret}}}""";

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

    private static (RegisterClient Client, HttpClientRequestAdapter Adapter) Register(Capture capture)
    {
        TextBodyParseNodeFactory.Register();
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(capture))
        {
            BaseUrl = "https://osdu.example.com/api/register/v1",
        };
        return (new RegisterClient(adapter), adapter);
    }

    [Theory]
    [InlineData(Hmac)]
    [InlineData(Gsa)]
    public async Task ASubscriptionIsSentWithItsSecret(string secret)
    {
        // Through the client's model the secret became neither of its two types, and writing it
        // failed with "A JSON value is missing".
        var file = SubscriptionWith(secret);
        var created = SubscriptionWith(secret).Replace("{\"name\"", "{\"id\":\"abc\",\"notificationId\":\"de-1\",\"name\"");
        var capture = new Capture(HttpStatusCode.Created, created);
        var (client, adapter) = Register(capture);

        var answer = await SubscriptionSecrets.SendAsync(adapter,
            client.Subscription.ToPostRequestInformation(new Subscription()), SubscriptionSecrets.Subscription(file),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, capture.Request!.Method);
        Assert.Equal("https://osdu.example.com/api/register/v1/subscription", capture.Request.RequestUri!.ToString());
        Assert.Equal(file, capture.Body);
        // Shown as it came, secret and all, where the model could not write it back out.
        Assert.Equal(created, answer);
    }

    [Theory]
    [InlineData(Hmac)]
    [InlineData(Gsa)]
    public async Task ASecretIsReplacedAsWritten(string secret)
    {
        var capture = new Capture(HttpStatusCode.NoContent, "");
        var (client, adapter) = Register(capture);

        await SubscriptionSecrets.SendAsync(adapter,
            client.Subscription["abc"].Secret.ToPutRequestInformation(new SecretPutRequestBody { HmacSecret = new HmacSecret() }),
            SubscriptionSecrets.Secret(secret), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, capture.Request!.Method);
        Assert.Equal("https://osdu.example.com/api/register/v1/subscription/abc/secret", capture.Request.RequestUri!.ToString());
        Assert.Equal(secret, capture.Body);
    }

    [Fact]
    public async Task SubscriptionsHoldingSecretsAreFound()
    {
        var found = $"[{SubscriptionWith(Hmac)},{SubscriptionWith(Gsa)}]";
        var capture = new Capture(HttpStatusCode.OK, found);
        var (client, adapter) = Register(capture);

        var answer = await SubscriptionSecrets.SendAsync(adapter,
            client.Subscription.ToGetRequestInformation(configuration => configuration.QueryParameters.NotificationId = "de-1"),
            null, TestContext.Current.CancellationToken);

        Assert.Equal("?notificationId=de-1", capture.Request!.RequestUri!.Query);
        Assert.Null(capture.Body);
        Assert.Equal(found, answer);
    }

    [Fact]
    public async Task ARefusalIsReportedLikeAnyServiceError()
    {
        var (client, adapter) = Register(new Capture(HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad","message":"Invalid push endpoint"}"""));

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => SubscriptionSecrets.SendAsync(adapter,
            client.Subscription.ToPostRequestInformation(new Subscription()), SubscriptionWith(Hmac),
            TestContext.Current.CancellationToken));

        Assert.Equal(400, exception.ResponseStatusCode);
        Assert.Equal("Invalid push endpoint", CliRunner.Describe(exception));
    }

    [Theory]
    [InlineData("""{"topic":"recordstopic","pushEndpoint":"https://x","secret":{"secretType":"HMAC","value":"v"}}""", "name")]
    [InlineData("""{"name":"n","topic":"recordstopic","pushEndpoint":"https://x"}""", "secret.secretType")]
    [InlineData("""{"name":"n","topic":"recordstopic","pushEndpoint":"https://x","secret":{"value":"v"}}""", "secret.secretType")]
    public void AnIncompleteSubscriptionIsRefusedBeforeAnythingIsSent(string json, string missing)
    {
        var exception = Assert.Throws<OsduException>(() => SubscriptionSecrets.Subscription(json));

        Assert.Contains(missing, exception.Message);
    }

    [Theory]
    [InlineData("""{"value":"v"}""")]
    [InlineData("""["HMAC"]""")]
    public void ASecretWithoutItsTypeIsRefused(string json) =>
        Assert.Throws<OsduException>(() => SubscriptionSecrets.Secret(json));

    [Fact]
    public void DebugLeavesTheirBodiesOutButNotTheRest()
    {
        // The client logs bodies as they are, so a secret read from a file went to the log.
        var note = new StringWriter();

        using var hidden = CliContext.Logging(debug: true, SubscriptionSecrets.SecretBodies, note)!;
        using var shown = CliContext.Logging(debug: true, null, new StringWriter())!;

        Assert.False(hidden.CreateLogger(CliContext.BodyLog).IsEnabled(LogLevel.Debug));
        Assert.True(hidden.CreateLogger("Equinor.OsduCsharpClient").IsEnabled(LogLevel.Debug));
        Assert.True(shown.CreateLogger(CliContext.BodyLog).IsEnabled(LogLevel.Debug));
        Assert.Contains("leaves out request and response bodies", note.ToString());
        Assert.Null(CliContext.Logging(debug: false, SubscriptionSecrets.SecretBodies));
    }
}
