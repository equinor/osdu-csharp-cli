using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Runtime;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Register.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using SecretPutRequestBody = Equinor.OsduCsharpClient.Register.Subscription.Item.Secret.SecretRequestBuilder.SecretPutRequestBody;

namespace Equinor.OsduCli.Commands;

/// <summary>
/// <c>subscription add</c>, <c>update-secret</c> and <c>find</c>: the subscription commands whose
/// bodies carry a secret, sent and read as JSON rather than through the client's models.
/// </summary>
/// <remarks>
/// <para>The client's secret is a union of <c>HmacSecret</c> and <c>GsaSecret</c>, told apart
/// by <c>secretType</c>, but its spec maps no <c>HMAC</c> or <c>GSA</c> to either. A secret
/// read from a file therefore became neither: writing the subscription failed with "A JSON
/// value is missing", and a response holding one could not be shown. So these send the file
/// as it is, as <c>record add</c> does, and show the response as it came.</para>
///
/// <para>Their bodies are also kept out of <c>--debug</c>, which logs them unredacted.</para>
///
/// <para>The generated commands keep their options, help and place in the command
/// reference; only what they do is replaced, through the Register <c>Customize</c> hook.</para>
/// </remarks>
public static class SubscriptionSecrets
{
    private const string Roles = "users.datalake.ops, users.datalake.admins or users.datalake.editors";

    /// <summary>Why these commands keep their bodies out of <c>--debug</c>.</summary>
    internal const string SecretBodies = "they carry a secret";

    private static readonly Dictionary<string, ParsableFactory<IParsable>> Errors =
        new() { ["XXX"] = AppError.CreateFromDiscriminatorValue };

    private static readonly OutputSpec Created = OutputSpec.Table(
        null, ("Id", "id"), ("Name", "name"), ("Topic", "topic"), ("NotificationId", "notificationId"));

    private static readonly OutputSpec Found = OutputSpec.Table(
        null, ("Id", "id"), ("Name", "name"), ("Topic", "topic"), ("Endpoint", "pushEndpoint"));

    /// <summary>Replaces what the three generated commands do.</summary>
    public static void Extend(Command subscription)
    {
        var add = Find(subscription, "add");
        var file = Option<string>(add, "--file");
        add.SetAction((parseResult, cancellationToken) => CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var json = Subscription(await CliContext.ReadBodyFileAsync(parseResult.GetValue(file)!, token));
            var request = context.Client.Register.Subscription.ToPostRequestInformation(new Subscription());
            return context.Output.Write(await SendAsync(context.Client.GetRequestAdapter("register"), request, json, token), Created);
        }, cancellationToken, Roles, hideBodiesBecause: SecretBodies));

        var update = Find(subscription, "update-secret");
        var id = Option<string>(update, "--id");
        var secretFile = Option<string>(update, "--file");
        update.SetAction((parseResult, cancellationToken) => CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var json = Secret(await CliContext.ReadBodyFileAsync(parseResult.GetValue(secretFile)!, token));
            // Any member will do: the content is replaced with the file before it is sent.
            var request = context.Client.Register.Subscription[parseResult.GetValue(id)!].Secret
                .ToPutRequestInformation(new SecretPutRequestBody { HmacSecret = new HmacSecret() });
            await SendAsync(context.Client.GetRequestAdapter("register"), request, json, token);
            return context.Output.WriteMessage("Secret updated");
        }, cancellationToken, Roles, hideBodiesBecause: SecretBodies));

        var find = Find(subscription, "find");
        var notificationId = Option<string>(find, "--notification-id");
        find.SetAction((parseResult, cancellationToken) => CliRunner.RunAsync(parseResult, async (context, token) =>
        {
            var request = context.Client.Register.Subscription.ToGetRequestInformation(
                configuration => configuration.QueryParameters.NotificationId = parseResult.GetValue(notificationId));
            return context.Output.Write(await SendAsync(context.Client.GetRequestAdapter("register"), request, null, token), Found);
        }, cancellationToken, Roles, hideBodiesBecause: SecretBodies));
    }

    /// <summary>
    /// Sends <paramref name="request"/>, with <paramref name="json"/> as its body when given, and
    /// returns the response as it came.
    /// </summary>
    internal static async Task<string?> SendAsync(IRequestAdapter adapter, RequestInformation request, string? json,
        CancellationToken cancellationToken)
    {
        if (json is not null)
            request.SetStreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)), "application/json");
        await using var response = await adapter.SendPrimitiveAsync<Stream>(request, Errors, cancellationToken);
        if (response is null)
            return null;
        using var reader = new StreamReader(response);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>The subscription file, refused when it is not one record with a secret.</summary>
    internal static string Subscription(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject subscription)
            throw new OsduException("The file must hold one subscription, as a JSON object.");
        var missing = new[] { "name", "topic", "pushEndpoint" }
            .Where(field => subscription[field] is not JsonValue value || !value.TryGetValue<string>(out var text)
                                                                     || string.IsNullOrWhiteSpace(text))
            .ToList();
        if (subscription["secret"] is not JsonObject secret || !HasType(secret))
            missing.Add("secret.secretType");
        if (missing.Count > 0)
            throw new OsduException($"The subscription file is missing {string.Join(", ", missing)}; nothing was sent.");
        return json;
    }

    /// <summary>The secret file, refused when it does not say what kind of secret it is.</summary>
    internal static string Secret(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject secret || !HasType(secret))
            throw new OsduException("The file must hold one secret with its secretType, such as {\"secretType\": \"HMAC\", \"value\": \"...\"}; nothing was sent.");
        return json;
    }

    private static bool HasType(JsonObject secret) =>
        secret["secretType"] is JsonValue type && type.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);

    private static Command Find(Command parent, string name) => parent.Subcommands.Single(command => command.Name == name);

    private static Option<T> Option<T>(Command command, string name) =>
        (Option<T>)command.Options.Single(option => option.Name == name);
}
