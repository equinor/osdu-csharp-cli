    /// <summary>Delete an index.</summary>
    /// <remarks>DELETE /index on the Storage service.</remarks>
    private static Command BuildIndexDelete()
    {
        var kindOption = new Option<string>("--kind")
        {
            Description = "Kind.",
            Required = true,
        };

        var command = new Command("delete", "Delete an index.");
        CommandRoles.Set(command, "users.datalake.ops", operatorRole: true);
        command.Options.Add(kindOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var kind = parseResult.GetValue(kindOption)!;

            var result = await context.Client.Storage.Index.DeleteAsync(configuration =>
            {
                configuration.QueryParameters.Kind = kind;
            }, cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken, "users.datalake.ops"));

        return command;
    }
