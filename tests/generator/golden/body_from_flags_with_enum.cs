    /// <summary>Search records.</summary>
    /// <remarks>POST /query on the Storage service.</remarks>
    private static Command BuildRecordSearch()
    {
        var kindBodyOption = new Option<string>("--kind", "-k")
        {
            Description = "Kind.",
            Required = true,
        };
        var returnedfieldsBodyOption = new Option<string[]>("--returned-fields", "-f")
        {
            Description = "Fields.",
            AllowMultipleArgumentsPerToken = true,
        };
        var sortOrderBodyOption = new Option<string[]>("--sort-order")
        {
            Description = "Order. One of: ASC, DESC.",
            AllowMultipleArgumentsPerToken = true,
        };
        sortOrderBodyOption.AcceptAnyCasingFromAmong("ASC", "DESC");

        var command = new Command("search", "Search records.");
        command.Options.Add(kindBodyOption);
        command.Options.Add(returnedfieldsBodyOption);
        command.Options.Add(sortOrderBodyOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            var kindValue = parseResult.GetValue(kindBodyOption)!;
            bodyNode["kind"] = JsonValue.Create(kindValue);
            var returnedfieldsValue = parseResult.GetValue(returnedfieldsBodyOption);
            if (returnedfieldsValue is { Length: > 0 })
                bodyNode["returnedFields"] = new JsonArray(returnedfieldsValue.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
            var sortOrderValue = parseResult.GetValue(sortOrderBodyOption);
            if (sortOrderValue is { Length: > 0 })
                CliContext.Child(bodyNode, "sort")["order"] = new JsonArray(sortOrderValue.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            var result = await context.Client.Storage.Query.PostAsync(body, cancellationToken: cancellationToken);

            var json = await OsduJson.ToJsonAsync(result);
            context.Output.WriteTotal(json, "totalCount");
            return context.Output.Write(
                json,
                returnedfieldsValue is { Length: > 0 }
                    ? OutputSpec.FromFields("results", returnedfieldsValue)
                    : OutputSpec.Table("results", ("Id", "id")));
        }, cancellationToken));

        return command;
    }
