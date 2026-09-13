    /// <summary>Count values.</summary>
    /// <remarks>POST /query on the Storage service.</remarks>
    private static Command BuildRecordAggregate()
    {
        var kindBodyOption = new Option<string>("--kind")
        {
            Description = "Kind.",
            Required = true,
        };

        var command = new Command("aggregate", "Count values.");
        command.Options.Add(kindBodyOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            bodyNode["limit"] = JsonValue.Create(1);
            CliContext.Child(bodyNode, "spatialFilter")["field"] = JsonValue.Create("data.Location");
            var kindValue = parseResult.GetValue(kindBodyOption)!;
            bodyNode["kind"] = JsonValue.Create(kindValue);
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            var result = await context.Client.Storage.Query.PostAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Table("aggregations", ("Value", "key"), ("Count", "count")));
        }, cancellationToken));

        return command;
    }
