using Microsoft.Extensions.Configuration;
using Equinor.OsduCsharpClient.Facade;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Reads a Python <c>osducli</c> profile — the INI files in <c>~/.osducli/</c> — as
/// configuration for this CLI.
/// </summary>
/// <remarks>
/// Existing users already have these, often a dozen of them, one per environment. Reading
/// them directly means no migration step, no second copy of the same values to keep in
/// sync, and both tools usable side by side while a transition happens.
///
/// <para><b>The per-service <c>*_url</c> keys are deliberately ignored.</b> A profile
/// carries entries like <c>storage_url = /api/storage/v2/</c> and
/// <c>unit_url = /api/unit/v3/</c>, but this CLI derives each service's base path from that
/// service's own OpenAPI <c>servers</c> entry and appends spec paths verbatim. Nine of the
/// eighteen specs put the version in the path rather than in <c>servers</c>, so honouring
/// <c>unit_url</c> would produce <c>/api/unit/v3/v3/unit</c> — the same doubling
/// osdu-python-client documents in its own service registry. The specs are also the fresher
/// source: profiles here still name <c>crs/catalog/v2</c> while the vendored spec is v3.</para>
///
/// <para>Record-creation defaults (<c>legal_tag</c>, <c>acl_viewer</c>, <c>acl_owner</c>,
/// <c>other_relevant_data_countries</c>) are not mapped either — nothing consumes them until
/// <c>dataload</c> is ported.</para>
/// </remarks>
public sealed class OsduCliIniConfigurationSource : IConfigurationSource
{
    public required string Path { get; init; }

    public bool Optional { get; init; } = true;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new OsduCliIniConfigurationProvider(this);
}

/// <inheritdoc cref="OsduCliIniConfigurationSource"/>
public sealed class OsduCliIniConfigurationProvider(OsduCliIniConfigurationSource source)
    : ConfigurationProvider
{
    /// <summary>Profile key to configuration key. Anything absent here is ignored.</summary>
    private static readonly Dictionary<string, string> Mapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["server"] = "Osdu:Server",
        ["data_partition_id"] = "Osdu:DataPartitionId",
        ["authority"] = "Osdu:Authority",
        ["client_id"] = "Osdu:ClientId",
        ["scopes"] = "Osdu:Scopes",
        // Not a key the Python CLI writes; it sketched one out and left it
        // commented. Read here so a default account can live beside the
        // environment it belongs to rather than being typed every time.
        ["username"] = "Osdu:Username",
    };

    public override void Load()
    {
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(source.Path))
        {
            if (source.Optional) return;
            throw new OsduException($"Profile not found: {source.Path}");
        }

        foreach (var (key, value) in Parse(File.ReadAllLines(source.Path)))
            if (Mapping.TryGetValue(key, out var configurationKey))
                Data[configurationKey] = value;
    }

    /// <summary>
    /// Flattens the INI to key/value pairs. Sections are not part of the key: the Python CLI
    /// writes everything under <c>[core]</c>, so honouring section names would only invent a
    /// distinction the source format does not make.
    /// </summary>
    internal static IEnumerable<(string Key, string Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;

            // Split on the first '=' only — scopes and authorities contain URLs.
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
                yield return (key, value);
        }
    }
}

public static class OsduCliIniConfigurationExtensions
{
    /// <summary>Adds a Python <c>osducli</c> profile as a configuration source.</summary>
    public static IConfigurationBuilder AddOsduCliProfile(
        this IConfigurationBuilder builder, string path, bool optional = true) =>
        builder.Add(new OsduCliIniConfigurationSource { Path = path, Optional = optional });
}
