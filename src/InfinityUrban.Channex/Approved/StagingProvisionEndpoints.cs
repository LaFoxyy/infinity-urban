namespace Stayline;

public static class StagingProvisionEndpoints
{
    public static IEndpointRouteBuilder MapStagingProvisionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/properties/{id:guid}/channex/provision", async (Guid id, StagingProvisionService service, CancellationToken ct) =>
            Results.Ok(await service.ProvisionAsync(id, ct)));
        return app;
    }
}
