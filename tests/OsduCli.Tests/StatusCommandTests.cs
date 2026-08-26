using System.Text.Json.Nodes;
using Equinor.OsduCli.Commands;
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
}
