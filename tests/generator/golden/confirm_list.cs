    /// <summary>Purge.</summary>
    /// <remarks>POST /purge on the Storage service.</remarks>
    private static Command BuildIndexPurge()
    {
        var returnedfieldsBodyOption = new Option<string[]>("--id")
        {
            Description = "Ids.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Go ahead without asking. The command says what it is about to do and asks first, and refuses when there is no terminal to ask at.",
        };

        var command = new Command("purge", "Purge.");
        command.Options.Add(returnedfieldsBodyOption);
        command.Options.Add(yesOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            var returnedfieldsValue = parseResult.GetValue(returnedfieldsBodyOption)!;
            bodyNode["returnedFields"] = new JsonArray(returnedfieldsValue.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            // Said, and asked, before anything is sent; --yes skips only the question.
            if (!Confirmation.Confirm(
                    "Purge " + Confirmation.List(returnedfieldsValue) + " \"for good\".",
                    parseResult.GetValue(yesOption), context))
                return 1;

            var result = await context.Client.Storage.Purge.PostAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken));

        return command;
    }
