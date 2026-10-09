    /// <summary>Search.</summary>
    /// <remarks>POST /query on the Storage service.</remarks>
    private static Command BuildRecordSearch()
    {
        var kindBodyOption = new Option<string>("--kind")
        {
            Description = "Kind.",
            Required = true,
        };
        var limitBodyOption = new Option<int?>("--limit", "-l")
        {
            Description = "Limit.",
        };
        var offsetBodyOption = new Option<int?>("--offset")
        {
            Description = "Offset.",
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Return every match, fetched 1000 at a time through /query_with_cursor. Cannot be combined with --limit or --offset.",
        };

        var command = new Command("search", "Search.");
        command.Options.Add(kindBodyOption);
        command.Options.Add(limitBodyOption);
        command.Options.Add(offsetBodyOption);
        command.Options.Add(allOption);

        // Options paging cannot combine, rejected before configuration is read.
        command.Validators.Add(result =>
        {
            var all = result.GetValue(allOption);
            if (all && result.GetResult(limitBodyOption) is not null)
                result.AddError("--all and --limit cannot be used together: --all is every match, --limit at most that many.");
            if ((all || result.GetValue(limitBodyOption) > 1000) && result.GetResult(offsetBodyOption) is not null)
                result.AddError("--offset cannot be combined with --all or a --limit above 1000: those page through /query_with_cursor, which starts at the first match.");
        });

        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>
        {
            var bodyNode = new JsonObject();
            var kindValue = parseResult.GetValue(kindBodyOption)!;
            bodyNode["kind"] = JsonValue.Create(kindValue);
            var limitValue = parseResult.GetValue(limitBodyOption);
            if (limitValue is not null)
                bodyNode["limit"] = JsonValue.Create(limitValue);
            var offsetValue = parseResult.GetValue(offsetBodyOption);
            if (offsetValue is not null)
                bodyNode["offset"] = JsonValue.Create(offsetValue);
            var bodyJson = bodyNode.ToJsonString();

            var allValue = parseResult.GetValue(allOption);
            if (allValue || limitValue > 1000)
            {
                var pagedJson = await CursorPaging.CollectAsync(
                    bodyNode, allValue ? null : limitValue, 1000,
                    "limit", "cursor", "results", "totalCount",
                    async (page, pageCancellation) => await OsduJson.ToJsonAsync(
                        await context.Client.Storage.Query_with_cursor.PostAsync(
                            await KiotaJsonSerializer.DeserializeAsync<CursorQueryRequest>(
                                page.ToJsonString(), CursorQueryRequest.CreateFromDiscriminatorValue, pageCancellation),
                            cancellationToken: pageCancellation)),
                    (cursor, releaseCancellation) => context.Client.Storage.Query_with_cursor[cursor].DeleteAsync(cancellationToken: releaseCancellation),
                    Console.IsErrorRedirected ? null : Console.Error,
                    cancellationToken);
                context.Output.WriteTotal(pagedJson, "totalCount");
                return context.Output.Write(
                    pagedJson,
                    OutputSpec.Table("results", ("Id", "id")));
            }

            var body = await KiotaJsonSerializer.DeserializeAsync<QueryRequest>(
                bodyJson, QueryRequest.CreateFromDiscriminatorValue, cancellationToken);

            var result = await context.Client.Storage.Query.PostAsync(body, cancellationToken: cancellationToken);

            var json = await OsduJson.ToJsonAsync(result);
            context.Output.WriteTotal(json, "totalCount");
            return context.Output.Write(
                json,
                OutputSpec.Table("results", ("Id", "id")));
        }, cancellationToken));

        return command;
    }
