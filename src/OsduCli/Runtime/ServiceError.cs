using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// A refusal read for the reason the service gives, wherever in the body it gives it.
/// </summary>
/// <remarks>
/// <para>Kiota reads an error body only for the statuses a spec describes, and throws a bare
/// exception for the rest. Wellbore DDMS describes only 422, so a missing bulk, a wrong curve
/// name and a missing record all printed "404 from the service." and stopped, though each
/// response said why. <see cref="ServiceClients"/> reads every status a spec leaves out with
/// this, and the bulk writes read every failure with it.</para>
///
/// <para>Where the reason is, in the order it is looked for:</para>
/// <list type="bullet">
/// <item><c>detail</c>, from Wellbore DDMS: a sentence, a list of <c>{loc, msg}</c> entries for a
/// request it cannot validate, or, from its bulk reads, the JSON of another answer.</item>
/// <item><c>message</c>, from the Java services, and where the text body reader puts the title
/// of a gateway's error page.</item>
/// <item><c>errors</c>, a list of <c>{message}</c>: Wellbore DDMS passing on Storage's answer.</item>
/// <item><c>reason</c>, the status's name: all Entitlements gives when <c>message</c> is empty.</item>
/// </list>
/// </remarks>
internal sealed class ServiceError : ApiException, IParsable
{
    private UntypedNode? _detail;
    private string? _message;
    private UntypedNode? _errors;
    private string? _reason;

    public override string Message =>
        Reason(_detail) ?? Unwrapped(_message) ?? Reason(_errors) ?? Text(_reason) ?? string.Empty;

    public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() =>
        new Dictionary<string, Action<IParseNode>>
        {
            ["detail"] = node => _detail = node.GetObjectValue(UntypedNode.CreateFromDiscriminatorValue),
            ["message"] = node => _message = node.GetStringValue(),
            ["errors"] = node => _errors = node.GetObjectValue(UntypedNode.CreateFromDiscriminatorValue),
            ["reason"] = node => _reason = node.GetStringValue(),
        };

    public void Serialize(ISerializationWriter writer)
    {
    }

    public static ServiceError CreateFromDiscriminatorValue(IParseNode parseNode) => new();

    /// <summary>The sentence, or the first three messages in a list, with where they apply.</summary>
    internal static string? Reason(UntypedNode? node) => node switch
    {
        UntypedString text => Unwrapped(text.GetValue()),
        UntypedArray entries => Join(entries.GetValue().Select(Entry).OfType<string>().Take(3)),
        _ => null,
    };

    private static string? Entry(UntypedNode entry)
    {
        if (entry is UntypedString text)
            return Text(text.GetValue());
        if (entry is not UntypedObject fields)
            return null;
        var values = fields.GetValue();
        var message = Said(values, "msg") ?? Said(values, "message") ?? Said(values, "reason");
        // FastAPI's location starts with where in the request it was found: "body".
        var where = values.TryGetValue("loc", out var loc) && loc is UntypedArray path
            ? string.Join('.', path.GetValue().OfType<UntypedString>().Select(part => part.GetValue()).Skip(1))
            : "";
        return where.Length > 0 && message is not null ? $"{where}: {message}" : message;
    }

    private static string? Said(IDictionary<string, UntypedNode> values, string name) =>
        values.TryGetValue(name, out var value) && value is UntypedString text ? Text(text.GetValue()) : null;

    /// <summary>
    /// The text, or the reason inside it when it is itself an answer: a bulk read that fails
    /// answers <c>{"detail": "{\"detail\": \"Bulk error: curves=['DEPT']\"}"}</c>.
    /// </summary>
    private static string? Unwrapped(string? text)
    {
        if (Text(text) is not { } said || !said.TrimStart().StartsWith('{'))
            return Text(text);
        try
        {
            using var answer = JsonDocument.Parse(said);
            foreach (var name in (string[])["detail", "message"])
            {
                if (answer.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return Unwrapped(value.GetString()) ?? said;
            }
        }
        catch (JsonException)
        {
            // A sentence that happens to start with a brace.
        }
        return said;
    }

    private static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string? Join(IEnumerable<string> messages) =>
        string.Join("; ", messages) is { Length: > 0 } joined ? joined : null;
}
