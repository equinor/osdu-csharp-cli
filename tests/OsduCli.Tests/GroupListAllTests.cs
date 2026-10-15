using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Commands.Generated;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Entitlements;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// <c>group list --all</c>: every group in the data partition, from <c>GET /groups/all</c>,
/// page by page.
/// </summary>
public class GroupListAllTests
{
    private static RootCommand Root()
    {
        var root = new RootCommand("osducs");
        GlobalOptions.AddTo(root);
        foreach (var command in GeneratedCommands.All())
            root.Subcommands.Add(command);
        return root;
    }

    [Theory]
    [InlineData("group list")]
    [InlineData("group list --all")]
    [InlineData("group list --all --type data")]
    [InlineData("group list --all --type SERVICE")]
    [InlineData("group list --role-required")]
    public void WhatCanBeCombinedParses(string line) =>
        Assert.Empty(Root().Parse(line.Split(' ')).Errors);

    [Theory]
    // A type only filters every group; your own are not typed.
    [InlineData("group list --type data", "--type applies only with --all.")]
    [InlineData("group list --all --role-required", "--role-required cannot be combined with --all")]
    [InlineData("group list --all --type admins", "Argument 'admins' not recognized")]
    public void WhatCannotIsRefusedWhileParsing(string line, string expected) =>
        Assert.Contains(Root().Parse(line.Split(' ')).Errors,
            error => error.Message.StartsWith(expected, StringComparison.Ordinal));

    /// <summary>Entitlements on Azure: an offset for a cursor, and "0" for the end of the list.</summary>
    private sealed class Partition(int groups) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var offset = int.Parse(query["cursor"] ?? "0");
            var limit = int.Parse(query["limit"]!);
            var page = new JsonArray([.. Enumerable.Range(offset, Math.Max(0, Math.Min(limit, groups - offset)))
                .Select(i => (JsonNode)new JsonObject { ["email"] = $"data.g{i}@dev.example.com", ["name"] = $"data.g{i}" })]);
            var body = new JsonObject
            {
                ["groups"] = page,
                ["cursor"] = groups > offset + limit ? (offset + limit).ToString() : "0",
                ["totalCount"] = groups,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Theory]
    [InlineData(2000, 2)]   // a whole number of pages, which "0" ends
    [InlineData(2500, 3)]
    [InlineData(0, 1)]
    public async Task EveryGroupIsFetchedOnce(int groups, int pages)
    {
        var partition = new Partition(groups);
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(partition))
        {
            BaseUrl = "https://osdu.example.com/api/entitlements/v2",
        };

        var json = JsonNode.Parse(await GroupListAll.CollectAsync(
            new EntitlementsClient(adapter).Groups.All, "DATA", null, TestContext.Current.CancellationToken))!;

        var emails = json["groups"]!.AsArray().Select(group => group!["email"]!.GetValue<string>()).ToList();
        Assert.Equal(groups, emails.Count);
        Assert.Equal(groups, emails.Distinct().Count());
        Assert.Equal(pages, partition.Requests.Count);
        Assert.All(partition.Requests, uri =>
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal("DATA", query["type"]);
            Assert.Equal(GroupListAll.PageSize.ToString(), query["limit"]);
        });
        Assert.Null(HttpUtility.ParseQueryString(partition.Requests[0].Query)["cursor"]);
    }
}
