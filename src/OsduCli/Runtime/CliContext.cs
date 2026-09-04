using System.CommandLine;
using System.Text.Json.Nodes;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Serialization.Json;
using Microsoft.Extensions.Logging;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.Facade.Auth;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Per-invocation state handed to every generated command action: an authenticated
/// <see cref="OsduClient"/> and a configured <see cref="OutputWriter"/>.
/// </summary>
public sealed class CliContext : IDisposable
{
    public OsduClient Client { get; }
    public OutputWriter Output { get; }

    /// <summary>The configuration this invocation resolved, for commands that report it.</summary>
    public OsduConfig Config { get; }

    /// <summary>The MSAL provider, for commands that report on sign-in state.</summary>
    public MsalInteractiveTokenProvider Msal { get; }

    /// <summary>
    /// The account this invocation will authenticate as, from <c>--user</c> or the profile's
    /// <c>username</c>, or null when neither said. Resolved once here so that commands
    /// reporting on it cannot disagree with the provider actually doing the work.
    /// </summary>
    public string? Username { get; }

    private CliContext(
        OsduClient client, OutputWriter output, OsduConfig config,
        MsalInteractiveTokenProvider msal, string? username)
    {
        Client = client;
        Output = output;
        Config = config;
        Msal = msal;
        Username = username;
    }

    /// <summary>
    /// Builds the context from the parsed command line. Kiota's serializer registry is
    /// process-global and is normally populated as a side effect of constructing a service
    /// client; we register up front so <see cref="OsduJson"/> works even for commands that
    /// deserialise a request body before touching a client.
    /// </summary>
    public static CliContext Create(ParseResult parseResult)
    {
        ApiClientBuilder.RegisterDefaultSerializer<JsonSerializationWriterFactory>();
        ApiClientBuilder.RegisterDefaultDeserializer<JsonParseNodeFactory>();

        var config = CliConfig.Load(parseResult.GetValue(GlobalOptions.Config), out var configuredUser);

        // An explicit --user beats the profile's default, which beats no opinion at all.
        // Normalised through the same helper the profile value goes through, so a blank or
        // padded flag cannot mean something different from a blank or padded config entry.
        var username = CliConfig.NormaliseUsername(parseResult.GetValue(GlobalOptions.User))
                       ?? configuredUser;

        var format = string.Equals(parseResult.GetValue(GlobalOptions.Output), "json",
            StringComparison.OrdinalIgnoreCase)
            ? OutputFormat.Json
            : OutputFormat.Table;

        // --debug turns on the client's own request/response logging. Exploratory testing
        // against a live service is mostly a question of "what did we actually send", and
        // without this the answer is a status code and nothing else.
        var loggerFactory = parseResult.GetValue(GlobalOptions.Debug)
            ? LoggerFactory.Create(builder => builder
                .AddFilter("Equinor.OsduCsharpClient", LogLevel.Debug)
                .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace))
            : null;

        // Client 2.0.0 made the core package authentication-agnostic: it no longer bundles
        // MSAL, and `OsduClient` no longer falls back to interactive sign-in. Choosing a
        // provider is now the consumer's job, and for a CLI driven by a person at a terminal
        // the answer is interactive — the same behaviour the old default gave, now stated
        // rather than inherited. The provider keeps its own OS-encrypted token cache under
        // ~/.osdu, so sign-in still survives between invocations.
        var msal = new MsalInteractiveTokenProvider(config, loggerFactory: loggerFactory)
        {
            Username = username,
        };

        return new CliContext(
            new OsduClient(config, new AccountScopedTokenProvider(msal, msal.GetCachedUsernamesAsync, username), loggerFactory),
            new OutputWriter(format, Console.Out),
            config,
            msal,
            username);
    }

    /// <summary>Reads and returns the contents of a JSON file passed via a command option.</summary>
    public static async Task<string> ReadBodyFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new OsduException($"File not found: {path}");
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    /// <summary>
    /// Wraps a bare JSON object in an array. Several OSDU create endpoints take a list of
    /// records, but users overwhelmingly hand the CLI a single record file — the Python CLI
    /// does the same wrap in <c>wellbore add</c>.
    /// </summary>
    public static string WrapAsArray(string json)
    {
        var trimmed = json.TrimStart();
        return trimmed.StartsWith('[') ? json : $"[{json}]";
    }

    public void Dispose() => Client.Dispose();

    /// <summary>
    /// Returns the named child object of <paramref name="parent"/>, creating it on first use.
    /// </summary>
    /// <remarks>
    /// Lets a body assembled from flags carry nested objects — Search's <c>sort</c> holds
    /// parallel <c>field</c> and <c>order</c> arrays. Created on demand so a request that
    /// sets none of a group's flags does not send an empty <c>"sort":{}</c>, which some OSDU
    /// services treat as a value rather than an omission.
    /// </remarks>
    public static JsonObject Child(JsonObject parent, string name) =>
        (JsonObject)(parent[name] ??= new JsonObject());
}
