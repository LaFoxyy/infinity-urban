namespace Stayline;

public record StagingImportInput(string? PropertyId, IReadOnlyDictionary<string, decimal>? MissingPriceDefaults = null);

public static class StagingImportEndpoints
{
    public static IEndpointRouteBuilder MapStagingImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/channex/import-preview", async (string? propertyId, StagingImportService imports, CancellationToken ct) =>
            Results.Ok(await imports.PreviewAsync(propertyId, ct)));
        app.MapPost("/api/channex/import", async (StagingImportInput input, StagingImportService imports, CancellationToken ct) =>
            Results.Ok(await imports.ImportAsync(input.PropertyId, input.MissingPriceDefaults, ct)));
        return app;
    }
}
