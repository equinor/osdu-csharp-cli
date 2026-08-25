    /// <summary>Get a CRS.</summary>
    /// <remarks>GET /v3/coordinate-reference-system on the Storage service.</remarks>
    private static Command BuildCrsGet()
    {
        var recordidOption = new Option<string>("--record-id")
        {
            Description = "Record id.",
        };
        var dataidOption = new Option<string>("--data-id")
        {
            Description = "Data id.",
        };

        var command = new Command("get", "Get a CRS.");
        command.Options.Add(recordidOption);
        command.Options.Add(dataidOption);

        // A parse-time validator, so this costs no config load, no
        // token and no round trip. The service rejects the request
        // anyway; it should not have to.
        command.Validators.Add(result =>
        {
            if (result.GetResult(recordidOption) is null && result.GetResult(dataidOption) is null)
                result.AddError("One of --record-id or --data-id is required.");
        });

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var recordid = parseResult.GetValue(recordidOption);
            var dataid = parseResult.GetValue(dataidOption);

            var result = await context.Client.Storage.V3.CoordinateReferenceSystem.GetAsync(configuration =>
            {
                configuration.QueryParameters.RecordId = recordid;
                configuration.QueryParameters.DataId = dataid;
            }, cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Raw);
        }, cancellationToken));

        return command;
    }
