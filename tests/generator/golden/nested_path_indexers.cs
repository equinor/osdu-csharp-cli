    /// <summary>Get a version.</summary>
    /// <remarks>GET /records/{id}/{version} on the Storage service.</remarks>
    private static Command BuildRecordVersionGet()
    {
        var idOption = new Option<string>("--id")
        {
            Description = "Id.",
            Required = true,
        };
        var versionOption = new Option<long>("--version")
        {
            Description = "Version.",
            Required = true,
        };

        var command = new Command("get", "Get a version.");
        command.Options.Add(idOption);
        command.Options.Add(versionOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var id = parseResult.GetValue(idOption)!;
            var version = parseResult.GetValue(versionOption);

            var result = await context.Client.Storage.Records[id][version].GetAsync(cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken));

        return command;
    }
