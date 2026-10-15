    /// <summary>Replay.</summary>
    /// <remarks>POST /replay on the Storage service.</remarks>
    private static Command BuildRecordReplay()
    {
        var filterKindsBodyOption = new Option<string[]>("--kind")
        {
            Description = "Kind.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("replay", "Replay.");
        command.Options.Add(filterKindsBodyOption);

        // The spec's maxItems, checked here rather than by the service.
        command.Validators.Add(result =>
        {
            if (OptionValues.Usable(result, filterKindsBodyOption) is { Length: > 1 })
                result.AddError("--kind takes one value.");
        });

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            var filterKindsValue = parseResult.GetValue(filterKindsBodyOption);
            if (filterKindsValue is { Length: > 0 })
                CliContext.Child(bodyNode, "filter")["kinds"] = new JsonArray(filterKindsValue.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<ReplayRequest>(
                bodyJson, ReplayRequest.CreateFromDiscriminatorValue, cancellationToken);

            var result = await context.Client.Storage.Replay.PostAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken));

        return command;
    }
