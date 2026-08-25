    /// <summary>List records.</summary>
    /// <remarks>GET /query/records on the Storage service.</remarks>
    private static Command BuildRecordList()
    {
        var kindOption = new Option<string>("--kind", "-k")
        {
            Description = "Kind.",
            Required = true,
        };
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Max.",
        };

        var command = new Command("list", "List records.");
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var kind = parseResult.GetValue(kindOption)!;
            var limit = parseResult.GetValue(limitOption);

            var result = await context.Client.Storage.Query.Records.GetAsync(configuration =>
            {
                configuration.QueryParameters.Kind = kind;
                configuration.QueryParameters.Limit = limit;
            }, cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Table("results", ("Id", "id"), ("Kind", "kind")));
        }, cancellationToken));

        return command;
    }
