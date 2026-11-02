using System.Text.Json;
using Equinor.OsduCsharpClient.Facade;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Normalises whatever a generated Kiota operation returns into a JSON string.
/// </summary>
/// <remarks>
/// Kiota's return type varies by how the endpoint is described in the spec: a typed model
/// (<see cref="IParsable"/>), a collection of them, a bare <see cref="string"/> when the
/// response body is untyped — OSDU storage does all three — or a <see cref="Stream"/> when
/// the spec declares no content at all, as Indexer's reindex and provision operations do. The generator does not want to
/// know which; it always emits <c>await OsduJson.ToJsonAsync(result)</c> and lets C#
/// overload resolution pick. The overloads are unambiguous because <see cref="string"/> and
/// <c>List&lt;T&gt;</c> and <see cref="Stream"/> do not themselves implement <see cref="IParsable"/>.
/// <para>Every overload takes the command's cancellation token so the generator can pass it
/// without knowing which one it gets. Only the <see cref="Stream"/> overload still reads from
/// the network, so only it can block, but Ctrl+C has to reach it.</para>
/// </remarks>
public static class OsduJson
{
    /// <summary>Untyped response — Kiota already handed us the raw JSON text.</summary>
    public static Task<string?> ToJsonAsync(string? value, CancellationToken cancellationToken = default) =>
        Task.FromResult(value);

    /// <summary>Undescribed response — read the body as text.</summary>
    /// <remarks>
    /// Nothing says the body is JSON, so it is checked: JSON passes through, an empty body is
    /// no output, and anything else becomes a JSON string so the output writer can print it
    /// rather than fail to parse it.
    /// <para>The exception is an HTML page, such as a sign-in screen or a gateway's answered
    /// with 200. It is not the operation's result, and printing it would report success for an
    /// operation that never ran. A typed response reports it through <see cref="TextBodyParseNode"/>;
    /// a stream carries no content type, so the page is recognised by its markup.</para>
    /// </remarks>
    /// <exception cref="OsduException">When the body is an HTML page.</exception>
    public static async Task<string?> ToJsonAsync(Stream? value, CancellationToken cancellationToken = default)
    {
        if (value is null) return null;

        using var reader = new StreamReader(value);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (IsHtmlPage(text))
        {
            var summary = TextBodyParseNode.Summarise(text, html: true);
            throw new OsduException(summary.Length == 0
                ? "The service answered with an empty HTML page where a result was expected."
                : $"The service answered with an HTML page where a result was expected: {summary}");
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(text);
        }
    }

    /// <summary>Single typed model.</summary>
    public static async Task<string?> ToJsonAsync<T>(T? value, CancellationToken cancellationToken = default)
        where T : IParsable =>
        value is null ? null : await KiotaJsonSerializer.SerializeAsStringAsync(
            value, serializeOnlyChangedValues: true, cancellationToken);

    /// <summary>Collection of typed models.</summary>
    public static async Task<string?> ToJsonAsync<T>(IEnumerable<T>? value, CancellationToken cancellationToken = default)
        where T : IParsable =>
        value is null ? null : await KiotaJsonSerializer.SerializeAsStringAsync(
            value, serializeOnlyChangedValues: true, cancellationToken);

    /// <summary>Whether <paramref name="text"/> is an HTML document rather than text that mentions one.</summary>
    /// <remarks>
    /// What may come before the document's start is skipped: comments, which gateways put above
    /// their pages, and the XML declaration an XHTML page opens with.
    /// </remarks>
    private static bool IsHtmlPage(string text)
    {
        var start = text.AsSpan().TrimStart();
        while (Prolog(start) is var skipped and > 0)
            start = start[skipped..].TrimStart();

        return start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The length of the comment or XML declaration <paramref name="text"/> starts with, or 0.</summary>
    private static int Prolog(ReadOnlySpan<char> text)
    {
        foreach (var (open, close) in new[] { ("<!--", "-->"), ("<?xml", "?>") })
        {
            if (!text.StartsWith(open, StringComparison.OrdinalIgnoreCase)) continue;
            var end = text[open.Length..].IndexOf(close, StringComparison.Ordinal);
            return end < 0 ? 0 : open.Length + end + close.Length;
        }

        return 0;
    }
}
