    /// <summary>Add records.</summary>
    /// <remarks>PUT /records on the Storage service.</remarks>
    private static Command BuildRecordAdd()
    {
        var fileOption = new Option<string>("--file", "-f")
        {
            Description = "JSON file.",
            Required = true,
        };

        var command = new Command("add", "Add records.");
        command.Options.Add(fileOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyJson = CliContext.WrapAsArray(await CliContext.ReadBodyFileAsync(parseResult.GetValue(fileOption)!, cancellationToken));
            var body = (await KiotaJsonSerializer.DeserializeCollectionAsync<Record>(
                bodyJson, Record.CreateFromDiscriminatorValue, cancellationToken)).ToList();

            var result = await context.Client.Storage.Records.PutAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Unwrap("recordIds"));
        }, cancellationToken));

        return command;
    }
