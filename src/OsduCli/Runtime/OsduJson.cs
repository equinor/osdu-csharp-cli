using System.Text.Json;
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
/// </remarks>
public static class OsduJson
{
    /// <summary>Untyped response — Kiota already handed us the raw JSON text.</summary>
    public static Task<string?> ToJsonAsync(string? value) => Task.FromResult(value);

    /// <summary>Undescribed response — read the body as text.</summary>
    /// <remarks>
    /// Nothing says the body is JSON, so it is checked: JSON passes through, an empty body is
    /// no output, and anything else becomes a JSON string so the output writer can print it
    /// rather than fail to parse it.
    /// </remarks>
    public static async Task<string?> ToJsonAsync(Stream? value)
    {
        if (value is null) return null;

        using var reader = new StreamReader(value);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;

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
    public static async Task<string?> ToJsonAsync<T>(T? value) where T : IParsable =>
        value is null ? null : await KiotaJsonSerializer.SerializeAsStringAsync(value);

    /// <summary>Collection of typed models.</summary>
    public static async Task<string?> ToJsonAsync<T>(IEnumerable<T>? value) where T : IParsable =>
        value is null ? null : await KiotaJsonSerializer.SerializeAsStringAsync(value);
}
