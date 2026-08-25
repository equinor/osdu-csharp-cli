namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Declarative description of how one command's JSON response is rendered as a table.
/// </summary>
/// <remarks>
/// The Python CLI encodes this as a JMESPath expression, e.g.
/// <c>"results || {Id:id,Version:version,Kind:kind}"</c>. We model it as data instead of
/// an expression language for three reasons: a generator can emit and validate it, a
/// reviewer can read it in the manifest without knowing JMESPath, and it needs no
/// expression evaluator at runtime — which keeps the NativeAOT story trivial.
///
/// <see cref="Root"/> is the optional property to unwrap before projecting (the
/// <c>results</c> in the example above). <see cref="Columns"/> maps a display header to a
/// dotted path within each element.
/// </remarks>
/// <param name="Root">Property to unwrap before projecting, or null to project the root.</param>
/// <param name="Columns">Ordered header → dotted-path pairs. Empty means "print raw JSON".</param>
public sealed record OutputSpec(string? Root, IReadOnlyList<(string Header, string Path)> Columns)
{
    /// <summary>Print the response as indented JSON, with no table projection.</summary>
    public static readonly OutputSpec Raw = new(null, []);

    /// <summary>Unwrap <paramref name="root"/>, then print it as indented JSON.</summary>
    public static OutputSpec Unwrap(string root) => new(root, []);

    /// <summary>Unwrap <paramref name="root"/> (nullable), then project the given columns.</summary>
    public static OutputSpec Table(string? root, params (string Header, string Path)[] columns) =>
        new(root, columns);

    /// <summary>
    /// Builds a table whose columns are the fields the caller asked for.
    /// </summary>
    /// <remarks>
    /// Used where a command lets the user project the response — <c>record search
    /// --returned-fields</c>. Without this the manifest's fixed columns would still be
    /// rendered, so asking for <c>data.FacilityName</c> would return it over the wire and
    /// then not show it, which is worse than not offering the flag.
    ///
    /// The header is the last dotted segment, capitalised: <c>data.FacilityName</c> becomes
    /// <c>FacilityName</c>. Two requested fields ending in the same segment therefore share
    /// a header; the full path is what disambiguates them, and showing it would make the
    /// table unreadable for the common case.
    /// </remarks>
    public static OutputSpec FromFields(string? root, IReadOnlyList<string> fields) =>
        new(root, fields.Select(field =>
        {
            var leaf = field.Split('.')[^1];
            return (leaf.Length > 0 ? char.ToUpperInvariant(leaf[0]) + leaf[1..] : field, field);
        }).ToList());
}
