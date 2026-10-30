using System.Data.Common;
using System.Text.Json;
using Dapper;

namespace Erp.Api.Security;

public static class AuditActions
{
    public const string LoginOk = "login";
    public const string LoginFailed = "login.failed";
    public const string Logout = "logout";
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string ModelChange = "model.change";
    public const string UserChange = "security.user";
    public const string RoleChange = "security.role";
    /// <summary>Spec 007: vista personal del listado creada, cambiada o borrada.</summary>
    public const string ViewChange = "view.change";
    /// <summary>Spec 008: destino, secreto, prueba o reintento de un webhook (nunca el secreto ni la URL completa).</summary>
    public const string WebhookChange = "integration.webhook";
    /// <summary>Spec 008: alta, cambio, rotación o revocación de una clave de API (nunca la clave).</summary>
    public const string ApiKeyChange = "integration.apikey";
    /// <summary>Spec 018: alta, cambio, rotación o revocación de un cliente OAuth (nunca el secreto).</summary>
    public const string OAuthClientChange = "integration.oauthclient";
    /// <summary>Spec 021: token emitido por <c>/oauth/token</c> (cliente, alcances y vigencia; nunca el token).</summary>
    public const string OAuthToken = "integration.oauthtoken";
    /// <summary>Spec 005: escrituras que hace una automatización síncrona en otro registro (spec 034: constantes, no texto suelto).</summary>
    public const string AutomationUpdate = "automation.update";
    public const string AutomationCreate = "automation.create";
    /// <summary>Spec 002 (H7): instalar y actualizar un módulo.</summary>
    public const string ModuleInstall = "module.install";
    public const string ModuleUpgrade = "module.upgrade";
    /// <summary>Prefijo de una transición del proceso (<c>workflow.{nombre}</c>).</summary>
    public const string WorkflowPrefix = "workflow.";
    public const string Import = "data.import";
    public const string Export = "data.export";
    /// <summary>Spec 009: paquetes de metadatos entre entornos (exportar, subir, rechazo al subir, aprobar, rechazar, aplicar).</summary>
    public const string EnvironmentExport = "environment.export";
    public const string EnvironmentUpload = "environment.upload";
    public const string EnvironmentRejected = "environment.rejected";
    public const string EnvironmentApprove = "environment.approve";
    public const string EnvironmentReject = "environment.reject";
    public const string EnvironmentApply = "environment.apply";
    /// <summary>Spec 017: pedir la reversión de un paquete aplicado y aplicarla (aprobar y rechazar usan las de la 009).</summary>
    public const string EnvironmentRevertRequest = "environment.revertRequest";
    public const string EnvironmentRevert = "environment.revert";
    /// <summary>Spec 010: alta o cambio de una empresa; alta, reemplazo o baja de un tipo de cambio; cambio de empresa actual.</summary>
    public const string CompanyChange = "company.change";
    public const string RateChange = "rate.change";
    public const string CompanySwitch = "user.company";
    /// <summary>Spec 011: solicitud al asistente IA (con su resultado), propuesta aplicada y propuesta descartada.</summary>
    public const string AssistantPropose = "assistant.propose";
    public const string AssistantApply = "assistant.apply";
    public const string AssistantDiscard = "assistant.discard";
    /// <summary>Spec 011: un administrador guardó o quitó la clave de Anthropic desde la app (nunca la clave).</summary>
    public const string AssistantKey = "assistant.key";
    /// <summary>Spec 015: unidades (alta y movimiento) y equipos (alta y cambios), con antes y después.</summary>
    public const string UnitChange = "security.unit";
    public const string TeamChange = "security.team";
    /// <summary>Spec 015 (H4): solicitud de un cambio de seguridad que amplía acceso, su aprobación y su rechazo.</summary>
    public const string SecurityRequest = "security.request";
    public const string SecurityApprove = "security.approve";
    public const string SecurityReject = "security.reject";
    /// <summary>Spec 016: activar, pausar o ejecutar ahora una tarea programada; reintentar un trabajo asíncrono.</summary>
    public const string ScheduleChange = "automation.schedule";
    public const string AutomationRetry = "automation.retry";
    /// <summary>Spec 027: subir, reemplazar o archivar una imagen (logo de empresa o imagen de plantilla); nunca el contenido.</summary>
    public const string FileChange = "file.change";
    /// <summary>Spec 027: PDF generado (catálogo, registro y plantilla; nunca el contenido).</summary>
    public const string PrintPdf = "data.print";
    /// <summary>Spec 033: sesión de ERP AI Studio pedida, terminada (estado, pasos, tokens y costo), cancelada, descartada y aplicada.</summary>
    public const string StudioStart = "studio.start";
    public const string StudioFinish = "studio.finish";
    public const string StudioCancel = "studio.cancel";
    public const string StudioDiscard = "studio.discard";
    public const string StudioApply = "studio.apply";
    /// <summary>Spec 036: mensaje de seguimiento en una conversación de Studio (turno y longitud; nunca el texto).</summary>
    public const string StudioMessage = "studio.message";
    /// <summary>
    /// Spec 038: conexión MCP creada (al canjear el código: cliente y alcances), revocada (por la persona, por el cliente o al
    /// reconectar) y revocada por reuso de un código o de un <i>refresh token</i>. Nunca los tokens.
    /// </summary>
    public const string McpConnect = "mcp.connect";
    public const string McpRevoke = "mcp.revoke";
    public const string McpReuse = "mcp.reuse";
}

public static class Audit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Escribe en la bitácora dentro de la misma transacción del cambio: si el cambio se revierte, la bitácora también.</summary>
    public static Task WriteAsync(DbConnection cn, DbTransaction? tx, HttpContext http, string action,
        string? entity = null, string? recordId = null, object? before = null, object? after = null,
        int? userId = null, string? username = null)
    {
        var user = http.Items[UserContextExtensions.ItemKey] as UserContext;
        return cn.ExecuteAsync("""
            INSERT meta.AuditLog (UserId, Username, Action, EntityName, RecordId, BeforeJson, AfterJson, IpAddress)
            VALUES (@UserId, @Username, @Action, @Entity, @RecordId, @Before, @After, @Ip)
            """, new
        {
            UserId = userId ?? user?.Id,
            Username = username ?? user?.Username,
            Action = action,
            Entity = entity,
            RecordId = recordId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        }, tx);
    }
}
