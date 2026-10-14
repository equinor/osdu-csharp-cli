    /// <summary>Re-index a kind.</summary>
    /// <remarks>POST /reindex on the Storage service.</remarks>
    private static Command BuildIndexReindex()
    {
        var forceCleanOption = new Option<bool>("--force-clean")
        {
            Description = "Clean first.",
        };
        var kindBodyOption = new Option<string>("--kind")
        {
            Description = "Kind.",
            Required = true,
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Go ahead without asking. With --force-clean, the command says what it is about to do and asks first, and refuses when there is no terminal to ask at.",
        };

        var command = new Command("reindex", "Re-index a kind.");
        command.Options.Add(forceCleanOption);
        command.Options.Add(kindBodyOption);
        command.Options.Add(yesOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var forceClean = parseResult.GetValue(forceCleanOption);
            var bodyNode = new JsonObject();
            var kindValue = parseResult.GetValue(kindBodyOption)!;
            bodyNode["kind"] = JsonValue.Create(kindValue);
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            // Said, and asked, before anything is sent; --yes skips only the question.
            if (forceClean == true && !Confirmation.Confirm(
                    "Delete the index of " + kindValue + " and rebuild it.",
                    parseResult.GetValue(yesOption), context))
                return 1;

            var result = await context.Client.Storage.Reindex.PostAsync(body, configuration =>
            {
                configuration.QueryParameters.ForceClean = forceClean;
            }, cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken));

        return command;
    }
