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

        g.MapGet("/", (string entity, int? page, int? pageSize, string? search, string? sort, string? dir, string? state, string? filters,
                HttpContext http, RecordService records, CancellationToken ct) =>
            records.ListAsync(entity, new ListQuery(page ?? 1, pageSize ?? 25, search, sort, dir, state, FilterSql.Parse(filters)), http.CurrentUser(), ct));

        g.MapGet("/lookup", (string entity, string? search, string? filterField, string? filterValue, HttpContext http, RecordService records, CancellationToken ct) =>
            records.LookupAsync(entity, search, http.CurrentUser(), ct, filterField, filterValue));

        // Contrato (plan 002): la respuesta envuelve los valores en { values }; spec 007 agrega { layout } y spec 023 { warnings }.
        g.MapPost("/compute", async (string entity, ComputeRequest req, HttpContext http, RecordService records, CancellationToken ct) =>
        {
            var (values, layout, warnings) = await records.ComputeAsync(entity, req.Id, req.Values, http.CurrentUser(), ct);
            return new { values, layout, warnings };
        });

        // Spec 008: exportar el listado como se ve (mismos parámetros que el listado + columnas visibles).
        g.MapGet("/export", async (string entity, string? format, string? columns, string? search, string? sort, string? dir, string? state, string? filters,
            HttpContext http, RecordService records, CancellationToken ct) =>
        {
            if (format is not (null or "csv" or "xlsx")) throw new Infrastructure.AppException(400, "Formato no válido (csv o xlsx).");
            var cols = columns?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var r = await records.ExportAsync(entity, new ListQuery(1, RecordService.MaxExport, search, sort, dir, state, FilterSql.Parse(filters)), cols, http, ct);
            if (r.Truncated) http.Response.Headers["X-ERP-Truncated"] = "true";
            // El nombre del archivo sale de los metadatos (identificador validado), nunca de la petición.
            var file = $"{r.Entity}-{DateTime.Now:yyyyMMdd}";
            return format == "xlsx"
                ? Results.File(ReportExport.Xlsx(r), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx")
                : Results.File(ReportExport.Csv(r), "text/csv; charset=utf-8", file + ".csv");
        });

        // Spec 008: importación masiva. El cuerpo es el archivo tal cual (CSV o XLSX, máx. 2 MB por Kestrel).
        g.MapPost("/import", async (string entity, bool? dryRun, HttpContext http, RecordService records, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            return await records.ImportAsync(entity, ms.ToArray(), dryRun ?? true, http, ct);
        });

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

        app.MapGet("/api/users/lookup", (string? search, bool? teams, RecordService records, CancellationToken ct) =>
            records.UserLookupAsync(search, ct, teams == true)).RequireAuthorization();
    }
}
