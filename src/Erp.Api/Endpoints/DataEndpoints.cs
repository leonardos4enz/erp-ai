using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

public static class DataEndpoints
{
    public sealed record ComputeRequest(long? Id, JsonElement Values);

    public static void MapData(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/data/{entity}").RequireAuthorization();

        g.MapGet("/", (string entity, int? page, int? pageSize, string? search, string? sort, string? dir, string? state,
                HttpContext http, RecordService records, CancellationToken ct) =>
            records.ListAsync(entity, new ListQuery(page ?? 1, pageSize ?? 25, search, sort, dir, state), http.CurrentUser(), ct));

        g.MapGet("/lookup", (string entity, string? search, HttpContext http, RecordService records, CancellationToken ct) =>
            records.LookupAsync(entity, search, http.CurrentUser(), ct));

        // Contrato (plan 002): la respuesta envuelve los valores en { values }.
        g.MapPost("/compute", async (string entity, ComputeRequest req, HttpContext http, RecordService records, CancellationToken ct) =>
            new { values = await records.ComputeAsync(entity, req.Id, req.Values, http.CurrentUser(), ct) });

        g.MapGet("/{id:long}", (string entity, long id, HttpContext http, RecordService records, CancellationToken ct) =>
            records.GetAsync(entity, id, http.CurrentUser(), ct));

        g.MapGet("/{id:long}/history", (string entity, long id, HttpContext http, RecordService records, CancellationToken ct) =>
            records.HistoryAsync(entity, id, http.CurrentUser(), ct));

        g.MapPost("/{id:long}/transition", (string entity, long id, TransitionRequest req, HttpContext http, RecordService records, CancellationToken ct) =>
            records.TransitionAsync(entity, id, req, http, ct));

        g.MapPost("/", async (string entity, JsonElement body, HttpContext http, RecordService records, CancellationToken ct) =>
        {
            var row = await records.CreateAsync(entity, body, http, ct);
            return Results.Created($"/api/data/{entity}/{row["id"]}", row);
        });

        g.MapPut("/{id:long}", (string entity, long id, JsonElement body, HttpContext http, RecordService records, CancellationToken ct) =>
            records.UpdateAsync(entity, id, body, http, ct));

        g.MapDelete("/{id:long}", async (string entity, long id, HttpContext http, RecordService records, CancellationToken ct) =>
        {
            await records.DeleteAsync(entity, id, http, ct);
            return Results.NoContent();
        });

        app.MapGet("/api/users/lookup", (string? search, RecordService records, CancellationToken ct) =>
            records.UserLookupAsync(search, ct)).RequireAuthorization();
    }
}
