using System.Text.Json.Nodes;
using Equinor.OsduCsharpClient.SchemaService.Models;
using Equinor.OsduCsharpClient.Search.Models;
using Equinor.OsduCli.Runtime;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;
using Xunit;

namespace OsduCli.Tests;

/// <summary>
/// Request bodies the CLI builds as JSON and then hands to a Kiota model, which is what is
/// sent. A model that cannot hold part of the JSON drops it silently, so each is checked to
/// come back out unchanged.
/// </summary>
public class RequestBodyRoundTripTests
{
    // What CliContext.Create registers before a command runs.
    public RequestBodyRoundTripTests()
    {
        ApiClientBuilder.RegisterDefaultSerializer<JsonSerializationWriterFactory>();
        ApiClientBuilder.RegisterDefaultDeserializer<JsonParseNodeFactory>();
        TextBodyParseNodeFactory.Register();
    }

    private static async Task<JsonNode?> RoundTrip<T>(string json, ParsableFactory<T> factory) where T : IParsable
    {
        var model = await KiotaJsonSerializer.DeserializeAsync(json, factory, TestContext.Current.CancellationToken);
        return JsonNode.Parse(await KiotaJsonSerializer.SerializeAsStringAsync(model!,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // `schema add` and `schema update`: the schema itself is a free-form JSON document, held by
    // an additional-data model since client 3.0.0.
    [Fact]
    public async Task ANestedSchemaIsSentWhole()
    {
        const string json = """
            {
              "schemaInfo": {
                "schemaIdentity": {
                  "authority": "test", "source": "cli", "entityType": "thing",
                  "schemaVersionMajor": 1, "schemaVersionMinor": 0, "schemaVersionPatch": 0
                },
                "status": "DEVELOPMENT"
              },
              "schema": {
                "$schema": "http://json-schema.org/draft-07/schema#",
                "type": "object",
                "properties": {
                  "data": {
                    "type": "object",
                    "properties": { "Name": { "type": "string" }, "Tags": { "type": "array", "items": { "type": "string" } } }
                  }
                },
                "required": ["data"]
              }
            }
            """;

        var sent = await RoundTrip(json, SchemaRequest.CreateFromDiscriminatorValue);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json)!["schema"], sent!["schema"]), sent.ToJsonString());
    }

    // `search query --bounding-box` and `--within-distance` set spatialFilter as JSON nodes; the
    // spec has described it as a oneOf of single-member objects since client 3.0.0.
    [Theory]
    [InlineData("""{ "field": "data.SpatialLocation", "byBoundingBox": { "topLeft": { "latitude": 60.5, "longitude": 2.1 }, "bottomRight": { "latitude": 58.2, "longitude": 4.3 } } }""")]
    [InlineData("""{ "field": "data.SpatialLocation", "byDistance": { "point": { "latitude": 60.5, "longitude": 2.1 }, "distance": 1500 } }""")]
    public async Task ASpatialFilterIsSentWhole(string filter)
    {
        var json = $$"""{ "kind": "*:*:*:*", "spatialFilter": {{filter}} }""";

        var sent = await RoundTrip(json, QueryRequest.CreateFromDiscriminatorValue);
        var sentByCursor = await RoundTrip(json, CursorQueryRequest.CreateFromDiscriminatorValue);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(filter), sent!["spatialFilter"]), sent.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(filter), sentByCursor!["spatialFilter"]), sentByCursor.ToJsonString());
    }
}
