using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;
using Microsoft.Kiota.Serialization.Text;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Reads a response body that is a page of text rather than JSON: an HTML error page from a
/// gateway, or a plain-text one from a proxy.
/// </summary>
/// <remarks>
/// <para>Every generated client maps its error statuses to an error model, and Kiota builds the
/// model by parsing the body with whatever is registered for its content type. Nothing was
/// registered for <c>text/html</c>, so an error page — how some gateways answer a token they
/// reject — ended the command in an unhandled <see cref="InvalidOperationException"/>
/// ("Content type text/html does not have a factory registered to be parsed") and a stack
/// trace, where a JSON error gets <c>error: 401 from the service.</c> and a reason. A tester
/// met exactly that. Kiota's <c>text/plain</c> parser is registered but cannot build an object,
/// so a plain-text error ended the same way.</para>
///
/// <para>So the page's own summary becomes the error's message, and the usual one-line error
/// follows: its title, failing that its first heading, failing that its text.</para>
/// </remarks>
internal sealed class TextBodyParseNodeFactory(string contentType) : IParseNodeFactory
{
    public string ValidContentType { get; } = contentType;

    /// <summary>
    /// Registers this for <c>text/html</c> and <c>text/plain</c>, in place of Kiota's text parser.
    /// </summary>
    /// <remarks>
    /// Assigned rather than added, so it replaces a text parser registered before it. Kiota
    /// registers its own with <c>TryAdd</c> when a client is built, which leaves this in place;
    /// a test holds it to that.
    /// </remarks>
    internal static void Register()
    {
        foreach (var type in new[] { "text/html", "text/plain" })
        {
            ParseNodeFactoryRegistry.DefaultInstance.ContentTypeAssociatedFactories[type] =
                new TextBodyParseNodeFactory(type);
        }
    }

    public async Task<IParseNode> GetRootParseNodeAsync(
        string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new TextBodyParseNode(text, html: ValidContentType == "text/html");
    }
}

/// <summary>
/// A text body that can still become an error model. Everything but objects is read as
/// Kiota's own text parser reads it, so a <c>text/plain</c> response that really is text is
/// unchanged.
/// </summary>
internal sealed partial class TextBodyParseNode(string text, bool html) : IParseNode
{
    /// <summary>Longer than this and a summary stops being a message; <c>--debug</c> shows the whole body.</summary>
    private const int MaxSummaryLength = 200;

    private readonly TextParseNode _text = new(text);

    public Action<IParsable>? OnBeforeAssignFieldValues { get; set; }
    public Action<IParsable>? OnAfterAssignFieldValues { get; set; }

    /// <summary>
    /// The body as an object: parsed as JSON when that is what it is under the wrong content
    /// type, otherwise an error whose message is the page's summary.
    /// </summary>
    /// <exception cref="OsduException">
    /// When the object wanted is not an error — a page where a successful response was
    /// expected, such as a sign-in screen answered with 200. Saying what came back beats an
    /// empty result that looks like an answer.
    /// </exception>
    public T GetObjectValue<T>(ParsableFactory<T> factory) where T : IParsable
    {
        if (Json('{') is { } json)
            return Node(json).GetObjectValue(factory);

        var summary = Summarise(text, html);
        var body = summary.Length == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["message"] = summary };
        var result = Node(JsonSerializer.SerializeToElement(body)).GetObjectValue(factory);
        return result is Exception ? result : throw NotJson(summary);
    }

    /// <summary>
    /// The body as a list of objects, when it is a JSON array under the wrong content type.
    /// </summary>
    /// <exception cref="OsduException">
    /// Otherwise. An error is never a list, so this is a page where a successful response was
    /// expected, and Kiota's text parser answered it with an exception that escaped as a stack
    /// trace.
    /// </exception>
    public IEnumerable<T> GetCollectionOfObjectValues<T>(ParsableFactory<T> factory) where T : IParsable =>
        Json('[') is { } json
            ? Node(json).GetCollectionOfObjectValues(factory)
            : throw NotJson(Summarise(text, html));

    private OsduException NotJson(string summary)
    {
        var what = html ? "an HTML page" : "text";
        return new OsduException(summary.Length == 0
            ? $"The service answered with empty {what} where JSON was expected."
            : $"The service answered with {what} where JSON was expected: {summary}");
    }

    /// <summary>The body as JSON, when it starts with <paramref name="opening"/> and parses.</summary>
    private JsonElement? Json(char opening)
    {
        if (!text.TrimStart().StartsWith(opening))
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JsonParseNode Node(JsonElement element) => new(element)
    {
        // Set by a backing-store proxy around this parser, and owed to the node doing the work.
        OnBeforeAssignFieldValues = OnBeforeAssignFieldValues,
        OnAfterAssignFieldValues = OnAfterAssignFieldValues,
    };

    /// <summary>One line saying what the page says, or empty when it says nothing.</summary>
    internal static string Summarise(string body, bool html)
    {
        var summary = body;
        if (html)
        {
            // Tags removed before entities are decoded, so `&lt;token&gt;` is text that stays,
            // not a tag made by decoding and then removed.
            var visible = Hidden().Replace(body, " ");
            summary = WebUtility.HtmlDecode(Tag().Replace(
                Inner(Title(), visible) ?? Inner(Heading(), visible) ?? visible, " "));
        }

        summary = Whitespace().Replace(summary, " ").Trim();
        return summary.Length <= MaxSummaryLength
            ? summary
            : summary[..MaxSummaryLength].TrimEnd() + "…";
    }

    /// <summary>The first match's content, unless it is only whitespace and tags.</summary>
    private static string? Inner(Regex pattern, string html) =>
        pattern.Match(html) is { Success: true } match
        && !string.IsNullOrWhiteSpace(Tag().Replace(match.Groups[1].Value, ""))
            ? match.Groups[1].Value
            : null;

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<(script|style)\b.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public IParseNode? GetChildNode(string identifier) => _text.GetChildNode(identifier);
    public string? GetStringValue() => _text.GetStringValue();
    public bool? GetBoolValue() => _text.GetBoolValue();
    public byte? GetByteValue() => _text.GetByteValue();
    public sbyte? GetSbyteValue() => _text.GetSbyteValue();
    public int? GetIntValue() => _text.GetIntValue();
    public float? GetFloatValue() => _text.GetFloatValue();
    public long? GetLongValue() => _text.GetLongValue();
    public double? GetDoubleValue() => _text.GetDoubleValue();
    public decimal? GetDecimalValue() => _text.GetDecimalValue();
    public Guid? GetGuidValue() => _text.GetGuidValue();
    public DateTimeOffset? GetDateTimeOffsetValue() => _text.GetDateTimeOffsetValue();
    public TimeSpan? GetTimeSpanValue() => _text.GetTimeSpanValue();
    public Microsoft.Kiota.Abstractions.Date? GetDateValue() => _text.GetDateValue();
    public Microsoft.Kiota.Abstractions.Time? GetTimeValue() => _text.GetTimeValue();
    public byte[]? GetByteArrayValue() => _text.GetByteArrayValue();
    public IEnumerable<T> GetCollectionOfPrimitiveValues<T>() => _text.GetCollectionOfPrimitiveValues<T>();
    public IEnumerable<T?> GetCollectionOfEnumValues<T>() where T : struct, Enum => _text.GetCollectionOfEnumValues<T>();
    public T? GetEnumValue<T>() where T : struct, Enum => _text.GetEnumValue<T>();
}
