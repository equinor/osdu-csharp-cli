    /// <summary>Search.</summary>
    /// <remarks>POST /query on the Storage service.</remarks>
    private static Command BuildRecordSearch()
    {
        var kindBodyOption = new Option<string>("--kind")
        {
            Description = "Kind.",
            Required = true,
        };
        var returnedfieldsBodyOption = new Option<string[]>("--returned-fields", "-f")
        {
            Description = "Fields.",
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result => result.Tokens
                .SelectMany(token => token.Value.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray(),
        };

        var command = new Command("search", "Search.");
        command.Options.Add(kindBodyOption);
        command.Options.Add(returnedfieldsBodyOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            var kindValue = parseResult.GetValue(kindBodyOption)!;
            bodyNode["kind"] = JsonValue.Create(kindValue);
            var returnedfieldsValue = parseResult.GetValue(returnedfieldsBodyOption);
            if (returnedfieldsValue is { Length: > 0 })
                bodyNode["returnedFields"] = new JsonArray(returnedfieldsValue.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            var result = await context.Client.Storage.Query.PostAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                returnedfieldsValue is { Length: > 0 }
                    ? OutputSpec.FromFields("results", returnedfieldsValue)
                    : OutputSpec.Table("results", ("Id", "id")));
        }, cancellationToken));

        return command;
    }
