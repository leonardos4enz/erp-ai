using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Mcp;

/// <summary>
/// Spec 044 (H4): <c>entregar</c> por MCP. La entrega se lee y su plan se compila contra el modelo real como en Studio
/// (<see cref="StudioDelivery"/>); si se acepta, queda como una conversación de Studio de la persona con origen MCP y la
/// propuesta vigente, que ella revisa y aplica con la huella de siempre (CLARIFY 4). La clave de idempotencia, la conversación,
/// su paso y la bitácora van en una sola transacción: exactamente una vez por clave (H4 CA2).
/// </summary>
public sealed class McpProposals(Db db, MetadataStore store, McpOptions options, JsonSerializerOptions json)
{
    public const string Origin = "mcp";

    /// <summary>El resultado de la herramienta (el objeto <c>result</c> de JSON-RPC), nuevo o el guardado con la clave.</summary>
    public async Task<JsonObject> DeliverAsync(HttpContext http, UserContext user, int grantId, string key, JsonObject input, CancellationToken ct)
    {
        var hash = McpIdempotency.Hash(StudioTools.Deliver, input);
        var d = await StudioDelivery.ParseAsync(JsonSerializer.SerializeToElement(input), json, store, db, ct);
        // H4 CA1: un plan que no compila (o una entrada inválida) vuelve al cliente y no se guarda nada (tampoco la clave).
        if (d.Error is not null) return McpServer.ToolErrorResult(d.Error, 400);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // El bloqueo de Studio de la persona: el tope del día no se rebasa con entregas a la vez desde varias conexiones.
        await StudioService.LockUserAsync(cn, tx, user.Id);
        if (await McpWrites.LockAsync(cn, tx, grantId, key) is { } used)
        {
            if (used.Tool != StudioTools.Deliver || !CryptographicOperations.FixedTimeEquals(used.InputHash, hash))
                return McpServer.ToolErrorResult(McpServer.KeyConflict, 409);
            if (used.Status == "done" && used.ResultJson is not null) return (JsonObject)JsonNode.Parse(used.ResultJson)!;
            return McpServer.ToolErrorResult(McpServer.KeyBusy, 409, 2);
        }
        var today = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM meta.StudioSession WHERE UserId = @Id AND Origin = 'mcp' AND CreatedAt > DATEADD(day, -1, SYSUTCDATETIME())
            """, new { user.Id }, tx);
        if (today >= options.ProposalsPerDay)
            return McpServer.ToolErrorResult($"Llegaste al tope de {options.ProposalsPerDay} propuestas por MCP en 24 horas: revisa las que tienes en ERP AI Studio.", 429, 3600);

        var client = await cn.ExecuteScalarAsync<string>("""
            SELECT c.Name FROM meta.McpGrant g JOIN meta.McpClient c ON c.Id = g.ClientId WHERE g.Id = @grantId
            """, new { grantId }, tx) ?? "Cliente MCP";
        var status = d.PlanJson is null ? StudioStatus.Answered : StudioStatus.Proposed;
        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.StudioSession (UserId, CompanyId, SecurityStamp, Prompt, Scopes, Status, Model, Steps, StartedAt, FinishedAt, Summary, PlanJson,
                                       ReportJson, PlanStatus, Origin, McpGrantId, McpClient)
            OUTPUT inserted.Id
            VALUES (@UserId, @CompanyId, @Stamp, @prompt, N'', @status, @model, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), @Summary, @PlanJson,
                    @report, @planStatus, 'mcp', @grantId, @client)
            """, new
        {
            UserId = user.Id, CompanyId = user.Company?.Id, Stamp = user.SecurityStamp, prompt = StudioAgent.Cut($"{client}: {Title(d.Summary)}", 4000),
            status, model = StudioAgent.Cut($"MCP: {client}", 100), d.Summary, d.PlanJson, report = d.Report.ToJsonString(),
            planStatus = d.PlanJson is null ? null : StudioStatus.Proposed, grantId, client,
        }, tx);
        await cn.ExecuteAsync("""
            INSERT meta.StudioStep (SessionId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs, Turn)
            VALUES (@id, 1, 'deliver', @tool, @input, @output, 'ok', @summary, 0, 1)
            """, new
        {
            id, tool = StudioTools.Deliver, input = StudioAgent.Cut(input.ToJsonString(), 8000),
            output = new JsonObject { ["summary"] = d.Summary }.ToJsonString(), summary = StudioAgent.Cut(d.Summary, 400),
        }, tx);
        var result = McpServer.ToolResult(new JsonObject
        {
            ["proposalId"] = id, ["status"] = status, ["url"] = $"{options.Origin(http)}/admin/studio?s={id}",
        });
        await cn.ExecuteAsync("""
            INSERT meta.McpWrite (GrantId, IdemKey, Tool, InputHash, Status, Attempt, ResultJson) VALUES (@grantId, @key, @tool, @hash, 'done', NEWID(), @result)
            """, new { grantId, key, tool = StudioTools.Deliver, hash, result = result.ToJsonString() }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.McpProposal, entity: "studio", recordId: id.ToString(),
            after: new { client, status, operations = d.PlanJson is null ? 0 : (JsonNode.Parse(d.PlanJson)?["operations"] as JsonArray)?.Count ?? 0 });
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Revisión exploratoria 044 (F-02): el título de la conversación es la primera línea del resumen sin las marcas de Markdown
    /// (la lista y el encabezado de Studio lo muestran como texto).
    /// </summary>
    internal static string Title(string summary)
    {
        var line = summary.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = line.TrimStart('#', '-', '>', ' ');
        return new string(line.Where(c => c is not ('*' or '`' or '_')).ToArray()).Trim();
    }
}
