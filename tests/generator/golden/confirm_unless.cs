    /// <summary>Replay.</summary>
    /// <remarks>POST /replay on the Storage service.</remarks>
    private static Command BuildRecordReplay()
    {
        var kindBodyOption = new Option<string>("--operation")
        {
            Description = "Operation.",
            Required = true,
        };
        var returnedfieldsBodyOption = new Option<string[]>("--kind")
        {
            Description = "Kind.",
            AllowMultipleArgumentsPerToken = true,
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Go ahead without asking. Without --kind, the command says what it is about to do and asks first, and refuses when there is no terminal to ask at.",
        };

        var command = new Command("replay", "Replay.");
        command.Options.Add(kindBodyOption);
        command.Options.Add(returnedfieldsBodyOption);
        command.Options.Add(yesOption);

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

            // Said, and asked, before anything is sent; --yes skips only the question.
            if (parseResult.GetResult(returnedfieldsBodyOption) is null && !Confirmation.Confirm(
                    "Replay every kind as a " + kindValue + ".",
                    parseResult.GetValue(yesOption), context))
                return 1;

            var result = await context.Client.Storage.Replay.PostAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken));

        return command;
    }
