    /// <summary>Spatial search.</summary>
    /// <remarks>POST /query on the Storage service.</remarks>
    private static Command BuildRecordGeo()
    {
        var kindBodyOption = new Option<string>("--kind")
        {
            Description = "Kind.",
            Required = true,
        };
        var spatialfilterFieldBodyOption = new Option<string>("--spatial-field")
        {
            Description = "Geo field.",
        };
        var spatialfilterByBoundingBoxBodyOption = new Option<double[]>("--bbox")
        {
            Description = "Box.",
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result =>
            {
                var raw = string.Join(",", result.Tokens.Select(token => token.Value));
                var supplied = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (supplied.Length != 4)
                {
                    result.AddError("--bbox needs exactly 4 comma-separated numbers.");
                    return [];
                }
                var values = new double[supplied.Length];
                for (var index = 0; index < supplied.Length; index++)
                    if (!double.TryParse(supplied[index], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out values[index]))
                    {
                        result.AddError($"'{supplied[index]}' is not a number.");
                        return [];
                    }
                return values;
            },
        };

        var command = new Command("geo", "Spatial search.");
        command.Options.Add(kindBodyOption);
        command.Options.Add(spatialfilterFieldBodyOption);
        command.Options.Add(spatialfilterByBoundingBoxBodyOption);

        // Contradictory options, rejected before the service has to
        // decide which one it believes.
        command.Validators.Add(result =>
        {
            if (result.GetResult(spatialfilterFieldBodyOption) is not null && result.GetResult(spatialfilterByBoundingBoxBodyOption) is not null)
                result.AddError("--spatial-field and --bbox cannot be used together.");
        });

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            var kindValue = parseResult.GetValue(kindBodyOption)!;
            bodyNode["kind"] = JsonValue.Create(kindValue);
            var spatialfilterFieldValue = parseResult.GetValue(spatialfilterFieldBodyOption);
            if (spatialfilterFieldValue is not null)
                CliContext.Child(bodyNode, "spatialFilter")["field"] = JsonValue.Create(spatialfilterFieldValue);
            var spatialfilterByBoundingBoxValue = parseResult.GetValue(spatialfilterByBoundingBoxBodyOption);
            if (spatialfilterByBoundingBoxValue is { Length: > 0 })
            {
                CliContext.Child(CliContext.Child(CliContext.Child(bodyNode, "spatialFilter"), "byBoundingBox"), "topLeft")["latitude"] = JsonValue.Create(spatialfilterByBoundingBoxValue[0]);
                CliContext.Child(CliContext.Child(CliContext.Child(bodyNode, "spatialFilter"), "byBoundingBox"), "topLeft")["longitude"] = JsonValue.Create(spatialfilterByBoundingBoxValue[1]);
                CliContext.Child(CliContext.Child(CliContext.Child(bodyNode, "spatialFilter"), "byBoundingBox"), "bottomRight")["latitude"] = JsonValue.Create(spatialfilterByBoundingBoxValue[2]);
                CliContext.Child(CliContext.Child(CliContext.Child(bodyNode, "spatialFilter"), "byBoundingBox"), "bottomRight")["longitude"] = JsonValue.Create(spatialfilterByBoundingBoxValue[3]);
            }
            var bodyJson = bodyNode.ToJsonString();
            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            var result = await context.Client.Storage.Query.PostAsync(body, cancellationToken: cancellationToken);

            return context.Output.Write(
                await OsduJson.ToJsonAsync(result),
                OutputSpec.Table("results", ("Id", "id")));
        }, cancellationToken));

        return command;
    }
