using Microsoft.Kiota.Abstractions.Serialization;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Normalises whatever a generated Kiota operation returns into a JSON string.
/// </summary>
/// <remarks>
/// Kiota's return type varies by how the endpoint is described in the spec: a typed model
/// (<see cref="IParsable"/>), a collection of them, or a bare <see cref="string"/> when the
/// response body is untyped — OSDU storage does all three. The generator does not want to
/// know which; it always emits <c>await OsduJson.ToJsonAsync(result)</c> and lets C#
/// overload resolution pick. The overloads are unambiguous because <see cref="string"/> and
/// <c>List&lt;T&gt;</c> do not themselves implement <see cref="IParsable"/>.
/// </remarks>
public static class OsduJson
{
    /// <summary>Untyped response — Kiota already handed us the raw JSON text.</summary>
    public static Task<string?> ToJsonAsync(string? value) => Task.FromResult(value);

    /// <summary>Single typed model.</summary>
    public static async Task<string?> ToJsonAsync<T>(T? value) where T : IParsable =>
        value is null ? null : await KiotaJsonSerializer.SerializeAsStringAsync(value);

    /// <summary>Collection of typed models.</summary>
    public static async Task<string?> ToJsonAsync<T>(IEnumerable<T>? value) where T : IParsable =>
        value is null ? null : await KiotaJsonSerializer.SerializeAsStringAsync(value);
}
