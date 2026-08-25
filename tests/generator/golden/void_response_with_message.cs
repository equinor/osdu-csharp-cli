    /// <summary>Delete a record.</summary>
    /// <remarks>POST /records/{id}:delete on the Storage service.</remarks>
    private static Command BuildRecordDelete()
    {
        var idOption = new Option<string>("--id")
        {
            Description = "Id.",
            Required = true,
        };

        var command = new Command("delete", "Delete a record.");
        command.Options.Add(idOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var id = parseResult.GetValue(idOption)!;

            await context.Client.Storage.Records.WithIdDelete(id).PostAsync(cancellationToken: cancellationToken);

            return context.Output.WriteMessage("1 record deleted");
        }, cancellationToken));

        return command;
    }
