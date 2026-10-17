using System.Net;
using System.Text;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Facade.Auth;
using Equinor.OsduCsharpClient.WellboreDdms;
using Equinor.OsduCsharpClient.WellboreDdms.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Refusals whose status the service's spec does not describe, read for the reason anyway.
/// </summary>
/// <remarks>
/// Wellbore DDMS describes only 422, so reading a WellLog with no bulk, asking for a curve it
/// does not have or getting a record that is not there all printed "404 from the service." and
/// nothing more, though each response said why. These go through a real Kiota adapter and the
/// generated client, since the client's own error mapping is what left them out.
/// </remarks>
public class ServiceErrorTests
{
    private const string Id = "dev:work-product-component--WellLog:da8f1283d7204e8290d323ea955cd8fc";

    private sealed class Respond(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            });
    }

    private static IRequestAdapter Adapter(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        TextBodyParseNodeFactory.Register();
        return new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: new HttpClient(new Respond(status, mediaType, body)))
        {
            BaseUrl = "https://osdu.example.com/api/os-wellbore-ddms",
        };
    }

    private static Task ReadBulk(IRequestAdapter adapter) =>
        new WellboreDdmsClient(adapter).Ddms.V3.Welllogs[Id].Data.GetAsync(
            configuration => configuration.QueryParameters.Curves = "DEPT", TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("""{"detail":"bulk for record dev:x not found"}""", "bulk for record dev:x not found")]
    // A bulk read passes on another answer as the text of its own.
    [InlineData("""{"detail":"{\"detail\":\"Bulk error: curves=['DEPT']\"}"}""", "Bulk error: curves=['DEPT']")]
    // Storage's answer, passed on by Wellbore DDMS for a record that is not there.
    [InlineData("""{"origin":"osdu-data-ecosystem-storage","errors":[{"code":404,"reason":"Record not found","message":"The record 'dev:x' was not found"}]}""",
        "The record 'dev:x' was not found")]
    [InlineData("""{"code":404,"reason":"Not Found","message":"The record 'dev:x' was not found"}""", "The record 'dev:x' was not found")]
    // All Entitlements says when it leaves `message` empty.
    [InlineData("""{"code":404,"reason":"Not Found","message":""}""", "Not Found")]
    public async Task ARefusalTheSpecLeavesOutSaysWhy(string body, string reason)
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ReadBulk(new ReasonReadingAdapter(Adapter(HttpStatusCode.NotFound, body))));

        Assert.Equal(404, exception.ResponseStatusCode);
        Assert.Equal(reason, CliRunner.Describe(exception));
    }

    [Fact]
    public async Task WithoutItTheReasonWasLost()
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ReadBulk(Adapter(HttpStatusCode.NotFound, """{"detail":"bulk for record dev:x not found"}""")));

        Assert.Null(CliRunner.Describe(exception));
    }

    [Fact]
    public async Task AStatusTheSpecDescribesIsReadAsItWas()
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => ReadBulk(new ReasonReadingAdapter(
            Adapter(HttpStatusCode.UnprocessableEntity, """{"detail":[{"loc":["query","limit"],"msg":"bad"}]}"""))));

        Assert.IsType<HTTPValidationError>(exception);
        Assert.Equal(422, exception.ResponseStatusCode);
        // Not "Exception of type '…HTTPValidationError' was thrown.", which it printed before.
        Assert.Equal("limit: bad", CliRunner.Describe(exception));
    }

    [Fact]
    public async Task AGatewaysErrorPageGivesItsTitle()
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() => ReadBulk(new ReasonReadingAdapter(
            Adapter(HttpStatusCode.BadGateway, "<html><head><title>502 Bad Gateway</title></head></html>", "text/html"))));

        Assert.Equal(502, exception.ResponseStatusCode);
        Assert.Equal("502 Bad Gateway", CliRunner.Describe(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    // One the spec describes: Kiota's JSON parser threw on it too.
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ARefusalCutShortKeepsItsStatus(HttpStatusCode status)
    {
        // The parser threw before an error could be built, so the status was lost, and the
        // CLI said "invalid JSON in request body" for a GET.
        const string cut = """{"detail":"bulk for rec""";

        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ReadBulk(new ReasonReadingAdapter(Adapter(status, cut))));

        Assert.Equal((int)status, exception.ResponseStatusCode);
        Assert.Equal(cut, CliRunner.Describe(exception));
    }

    [Fact]
    public async Task AnAnswerCutShortSaysSo()
    {
        var exception = await Assert.ThrowsAsync<OsduException>(() =>
            ReadBulk(new ReasonReadingAdapter(Adapter(HttpStatusCode.OK, """{"columns":["MD","GR"],"data":[[1"""))));

        Assert.Contains("where JSON was expected", exception.Message);
    }

    [Fact]
    public async Task AnEmptyRefusalIsStillJustItsStatus()
    {
        var exception = await Assert.ThrowsAnyAsync<ApiException>(() =>
            ReadBulk(new ReasonReadingAdapter(Adapter(HttpStatusCode.NotFound, ""))));

        Assert.Equal(404, exception.ResponseStatusCode);
        Assert.Null(CliRunner.Describe(exception));
    }

    [Fact]
    public void ACallersOwnFallbackIsKept()
    {
        var own = new Dictionary<string, ParsableFactory<IParsable>> { ["XXX"] = HTTPValidationError.CreateFromDiscriminatorValue };

        Assert.Same(own, ReasonReadingAdapter.WithFallback(own));
    }

    [Fact]
    public void ACallersMappingIsLeftAsItWas()
    {
        var own = new Dictionary<string, ParsableFactory<IParsable>> { ["422"] = HTTPValidationError.CreateFromDiscriminatorValue };

        var mapping = ReasonReadingAdapter.WithFallback(own);

        Assert.Equal(["422", "XXX"], mapping.Keys.Order());
        Assert.Single(own);
    }

    private sealed class Token : ITokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
    }

    [Fact]
    public void EveryCommandSendsThroughIt()
    {
        using var clients = new ServiceClients(new OsduClient(
            new OsduConfig { Server = "https://osdu.example.com", DataPartitionId = "p" }, new Token()));

        var adapter = Assert.IsType<ReasonReadingAdapter>(clients.GetRequestAdapter("wellbore_ddms"));
        Assert.Same(adapter, clients.GetRequestAdapter("wellbore_ddms"));
        Assert.Same(clients.WellboreDdms, clients.WellboreDdms);
        Assert.StartsWith("https://osdu.example.com/", adapter.BaseUrl);
    }
}
