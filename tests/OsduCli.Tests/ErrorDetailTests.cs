using Equinor.OsduCli.Runtime;
using Microsoft.Kiota.Abstractions.Serialization;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Covers pulling readable text out of an OSDU error body.
/// </summary>
/// <remarks>
/// Kiota overrides an error type's <c>Message</c> with whatever property the spec called
/// <c>message</c>. Schema Service nests the real content one level down, so nothing matched
/// and the CLI printed "error: 400 from the service." — a failure with no reason. A tester hit
/// exactly that on `schema add`.
/// </remarks>
public class ErrorDetailTests
{
    private static UntypedObject Object(params (string Key, UntypedNode Value)[] fields) =>
        new(fields.ToDictionary(f => f.Key, f => f.Value));

    private static UntypedNode Text(string value) => new UntypedString(value);

    /// <summary>The shape Schema Service actually returns for a malformed request.</summary>
    private static UntypedObject ValidationFailure() => Object(
        ("error", Object(
            ("code", new UntypedInteger(400)),
            ("message", Text("Validation Error")),
            ("errors", new UntypedArray(
            [
                Object(("reason", Text("badRequest")), ("message", Text("schema must not be null"))),
                Object(("reason", Text("badRequest")), ("message", Text("status must not be null"))),
            ])))));

    [Fact]
    public void TheSummaryAndTheSpecificFailuresAreBothReported()
    {
        // "Validation Error" alone sends the user to read the raw response.
        var detail = CliRunner.Flatten(ValidationFailure());

        Assert.Equal("Validation Error: schema must not be null; status must not be null", detail);
    }

    [Fact]
    public void ASummaryWithNoNestedErrorsStandsAlone()
    {
        var detail = CliRunner.Flatten(Object(
            ("error", Object(("message", Text("Schema Id is already present"))))));

        Assert.Equal("Schema Id is already present", detail);
    }

    [Fact]
    public void ReasonIsUsedWhenThereIsNoMessage()
    {
        var detail = CliRunner.Flatten(Object(("error", Object(("reason", Text("badRequest"))))));

        Assert.Equal("badRequest", detail);
    }

    [Fact]
    public void DetailsAreCappedSoAnErrorDoesNotBecomeADump()
    {
        var many = Enumerable.Range(1, 6)
            .Select(i => (UntypedNode)Object(("message", Text($"problem {i}"))))
            .ToArray();
        var detail = CliRunner.Flatten(Object(
            ("error", Object(("message", Text("Validation Error")),
                             ("errors", new UntypedArray(many))))));

        Assert.Equal("Validation Error: problem 1; problem 2; problem 3", detail);
        Assert.DoesNotContain("problem 4", detail);
    }

    [Fact]
    public void ADetailIdenticalToTheSummaryIsNotRepeated()
    {
        // Several services echo the single error as both summary and detail.
        var detail = CliRunner.Flatten(Object(
            ("error", Object(("message", Text("Schema Id is already present")),
                             ("errors", new UntypedArray(
                             [
                                 Object(("message", Text("Schema Id is already present"))),
                             ]))))));

        Assert.Equal("Schema Id is already present", detail);
    }

    [Fact]
    public void AnUnrecognisedShapeYieldsNothingRatherThanNoise()
    {
        Assert.Null(CliRunner.Flatten(null));
        Assert.Null(CliRunner.Flatten(new UntypedInteger(400)));
    }
}
