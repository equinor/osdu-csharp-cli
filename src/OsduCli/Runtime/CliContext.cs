using System.CommandLine;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Serialization.Json;
using Microsoft.Extensions.Logging;
using Equinor.OsduCsharpClient.Facade;

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

    private CliContext(OsduClient client, OutputWriter output, OsduConfig config)
    {
        Client = client;
        Output = output;
        Config = config;
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

        var config = CliConfig.Load(parseResult.GetValue(GlobalOptions.Config));

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

        return new CliContext(
            new OsduClient(config, loggerFactory: loggerFactory),
            new OutputWriter(format, Console.Out),
            config);
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
}
