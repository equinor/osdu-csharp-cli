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
}
