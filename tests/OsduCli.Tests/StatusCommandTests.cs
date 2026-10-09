using System.Net;
using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
using Equinor.OsduCli.Runtime;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Covers the merge of a service's info payload into its status row. A service controls the
/// shape of that payload, so the row has to defend the columns it owns.
/// </summary>
public class StatusCommandTests
{
    private static JsonObject Row(string service = "storage", string status = "ok")
        => new() { ["service"] = service, ["status"] = status };

    [Fact]
    public void PayloadFieldsAreMergedIntoTheRow()
    {
        var row = Row();
        StatusCommand.MergePayload(row, """{"version":"0.29.4","buildTime":"2026-08-05"}""");

        Assert.Equal("0.29.4", row["version"]!.GetValue<string>());
        Assert.Equal("2026-08-05", row["buildTime"]!.GetValue<string>());
    }

    [Fact]
    public void PayloadCannotRenameTheService()
    {
        // Wellbore DDMS's /about really does return this, and it replaced the row's name —
        // printing a service that `status <service>` would then reject as unknown.
        var row = Row("wellbore-ddms");
        StatusCommand.MergePayload(row, """{"service":"Wellbore DDMS OSDU","version":"0.29"}""");

        Assert.Equal("wellbore-ddms", row["service"]!.GetValue<string>());
        Assert.Equal("0.29", row["version"]!.GetValue<string>());
    }

    [Fact]
    public void PayloadCannotReportItsOwnHealth()
    {
        var row = Row(status: "ok");
        StatusCommand.MergePayload(row, """{"status":"DEGRADED"}""");

        Assert.Equal("ok", row["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPayloadLeavesTheRowIntact(string? json)
    {
        var row = Row();
        StatusCommand.MergePayload(row, json);

        Assert.Equal(2, row.Count);
        Assert.Equal("ok", row["status"]!.GetValue<string>());
    }

    [Fact]
    public void ANonObjectPayloadIsIgnoredRatherThanThrowing()
    {
        // A probe that reaches the wrong endpoint can return a bare array or string; the
        // point of `status` is to keep reporting, so this must not become an exception.
        var row = Row();
        StatusCommand.MergePayload(row, """["not","an","object"]""");

        Assert.Equal(2, row.Count);
    }

    // ---- failures -------------------------------------------------------------------------

    [Theory]
    // A service that answers is reachable, and says why it refused.
    [InlineData(401, "refused (401)")]
    [InlineData(403, "refused (403)")]
    [InlineData(404, "error (404)")]
    [InlineData(500, "error (500)")]
    public void AServiceThatAnswersIsNotUnreachable(int code, string expected)
    {
        // Every failure was "unreachable": a 401 from one service on prod looked like a
        // network problem.
        var (status, error) = StatusCommand.Failure(new ApiException("Unauthorized") { ResponseStatusCode = code });

        Assert.Equal(expected, status);
        Assert.Equal($"{code} Unauthorized", error);
    }

    [Fact]
    public void AStatusCodeWithoutAMessageStillSaysWhatHappened()
    {
        // A service's error model with nothing in it, as a gateway's empty 502 gives.
        Assert.Equal(("error (502)", "502 from the service"),
            StatusCommand.Failure(new Equinor.OsduCsharpClient.Legal.Models.AppError { ResponseStatusCode = 502 }));
    }

    [Fact]
    public void ARawRequestThatAnswersIsClassifiedTheSameWay()
    {
        // CRS Conversion is probed without a generated builder.
        var (status, _) = StatusCommand.Failure(
            new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        Assert.Equal("refused (403)", status);
    }

    [Fact]
    public void OnlyAConnectionThatFailsIsUnreachable()
    {
        Assert.Equal(("unreachable", "No such host is known."),
            StatusCommand.Failure(new HttpRequestException("No such host is known.")));
        Assert.Equal("unreachable", StatusCommand.Failure(new TaskCanceledException()).Status);
    }

    [Fact]
    public void FailuresAreExplainedOncePerReason()
    {
        // The reason was only in the JSON output; the table said "unreachable" and stopped.
        var rows = new JsonArray(
            new JsonObject { ["service"] = "storage", ["status"] = "ok" },
            new JsonObject { ["service"] = "crs-catalog", ["status"] = "refused (401)", ["error"] = "401 Unauthorized" },
            new JsonObject { ["service"] = "legal", ["status"] = "refused (401)", ["error"] = "401 Unauthorized" },
            new JsonObject { ["service"] = "file", ["status"] = "unreachable", ["error"] = "No such host is known." });

        Assert.Equal(["crs-catalog, legal: 401 Unauthorized", "file: No such host is known."],
            StatusCommand.FailureNotes(rows));
    }

    private static Task<JsonObject> Probe(Func<Task<string?>> probe, CancellationToken cancellationToken = default) =>
        StatusCommand.ProbeAsync("storage", (_, _) => probe(), null!, cancellationToken);

    [Fact]
    public async Task ATimeoutIsAServiceThatDidNotAnswer()
    {
        // It surfaces as a cancellation, and ended the whole command.
        var row = await Probe(() => throw new TaskCanceledException("timed out"), TestContext.Current.CancellationToken);

        Assert.Equal("unreachable", row["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACancelledCommandStillStops()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Probe(() => throw new OperationCanceledException(cancelled.Token), cancelled.Token));
    }

    [Fact]
    public async Task ASignInProblemIsTheCommandsErrorNotAService()
    {
        // Twelve rows each saying "more than one account is signed in" described no service.
        await Assert.ThrowsAsync<SignInException>(() =>
            Probe(() => throw new SignInException("More than one account is signed in"), TestContext.Current.CancellationToken));
    }
}
