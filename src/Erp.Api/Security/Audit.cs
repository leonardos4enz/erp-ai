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
