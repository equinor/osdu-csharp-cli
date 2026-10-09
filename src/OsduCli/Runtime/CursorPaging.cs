using System.Globalization;
using System.Text.Json.Nodes;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// Follows a cursor endpoint page by page and hands back the pages as one response, for a
/// command whose manifest declares <c>paging</c>.
/// </summary>
/// <remarks>
/// <para>Search answers at most 1000 records a request, and <c>--offset</c> cannot reach past
/// the first 10000. Its cursor endpoint takes the same request plus a cursor, and returns a
/// page and the cursor for the next. Following it is what turns <c>--limit 25000</c>, or
/// <c>--all</c>, into an answer; there was no way to get one before.</para>
///
/// <para>The response is shaped like the ordinary endpoint's — the results under the same
/// key, and the total the first page reported — so the command renders it exactly as it
/// renders one page.</para>
/// </remarks>
public static class CursorPaging
{
    /// <summary>Requests that are still running when the command is interrupted get this long to release their cursor.</summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fetches pages until the results run out or <paramref name="limit"/> is reached.
    /// </summary>
    /// <param name="request">The request as the ordinary endpoint would get it; each page sends a copy.</param>
    /// <param name="limit">How many results to stop at, or null for all of them.</param>
    /// <param name="pageSize">The most one page may ask for.</param>
    /// <param name="limitProperty">The request property that sets a page's size.</param>
    /// <param name="cursorProperty">The request and response property carrying the cursor.</param>
    /// <param name="resultsProperty">The response array the pages are joined from.</param>
    /// <param name="totalProperty">The response property holding the match count, or null.</param>
    /// <param name="fetch">Sends one page's request and returns the response as JSON.</param>
    /// <param name="release">Releases a cursor the command stops following before its end.</param>
    /// <param name="progress">Where to report progress, or null for nowhere.</param>
    public static async Task<string> CollectAsync(
        JsonObject request, int? limit, int pageSize,
        string limitProperty, string cursorProperty, string resultsProperty, string? totalProperty,
        Func<JsonObject, CancellationToken, Task<string?>> fetch,
        Func<string, CancellationToken, Task> release,
        TextWriter? progress,
        CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        long? total = null;
        string? cursor = null;
        var reported = false;

        try
        {
            while (limit is not { } most || results.Count < most)
            {
                var page = (JsonObject)request.DeepClone();
                var asked = limit is { } cap ? Math.Min(pageSize, cap - results.Count) : pageSize;
                page[limitProperty] = asked;
                if (cursor is not null)
                    page[cursorProperty] = cursor;

                var response = JsonNode.Parse(await fetch(page, cancellationToken) ?? "null") as JsonObject;
                if (total is null && totalProperty is not null
                    && response?[totalProperty] is JsonValue count && count.TryGetValue<long>(out var matches))
                {
                    total = matches;
                }

                var items = response?[resultsProperty] as JsonArray;
                var received = items?.Count ?? 0;
                foreach (var item in items?.ToArray() ?? [])
                {
                    items!.Remove(item);
                    results.Add(item);
                }

                cursor = response?[cursorProperty] is JsonValue next && next.TryGetValue<string>(out var text)
                         && !string.IsNullOrEmpty(text)
                    ? text
                    : null;

                if (progress is not null)
                {
                    progress.Write($"\rFetched {Progress(results.Count, total)}");
                    reported = true;
                }

                // A page short of what was asked for, or no cursor to follow, is the end. Asking
                // once more would only fetch an empty page.
                if (received < asked || cursor is null)
                    break;
            }
        }
        finally
        {
            if (reported)
                progress!.WriteLine();
            // Stopping at a limit, or on an error or an interruption, leaves the cursor open on
            // the service until it expires. Released quietly: the command's own outcome is
            // what matters, and a failed release changes nothing about it.
            if (cursor is not null)
                await ReleaseQuietlyAsync(release, cursor);
        }

        var combined = new JsonObject { [resultsProperty] = results };
        if (totalProperty is not null && total is not null)
            combined[totalProperty] = total;
        return combined.ToJsonString();
    }

    /// <summary>How far a run has got, against the total when the total can be trusted.</summary>
    /// <remarks>
    /// Exactly 10000 is Search's cap rather than a count unless trackTotalCount was asked
    /// for, and is marked as one, as the summary line marks it. A total below what has been
    /// fetched is no total at all, so it is left out rather than printed as
    /// <c>12,000 of 10,000</c>.
    /// </remarks>
    internal static string Progress(int fetched, long? total)
    {
        var count = fetched.ToString("N0", CultureInfo.InvariantCulture);
        if (total is not { } all || all < fetched)
            return count;
        var of = all.ToString("N0", CultureInfo.InvariantCulture);
        return all == OutputWriter.SearchCountCap ? $"{count} of {of}+" : $"{count} of {of}";
    }

    private static async Task ReleaseQuietlyAsync(Func<string, CancellationToken, Task> release, string cursor)
    {
        // Not the command's token: when the command was interrupted, that one is cancelled.
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            await release(cursor, timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // See above.
        }
    }
}
