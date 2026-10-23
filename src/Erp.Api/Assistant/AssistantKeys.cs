using System.Security.Cryptography;
using Dapper;
using Erp.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Erp.Api.Assistant;

/// <summary>De dónde sale la clave: del servidor (user-secrets o variable de entorno), de la app o de ningún lado.</summary>
public sealed record AssistantKeyInfo(string? Source, DateTime? UpdatedAt, string? UpdatedBy)
{
    public const string Server = "server";
    public const string App = "app";
}

/// <summary>
/// Spec 011: la clave de Anthropic. La del servidor manda; si no hay, la que un administrador guardó desde la app, cifrada
/// con Data Protection (como los secretos de webhooks de la 008). Nunca se devuelve al navegador ni se escribe en logs.
/// </summary>
public sealed class AssistantKeys(Db db, AssistantOptions options, IDataProtectionProvider provider, ILogger<AssistantKeys> log)
{
    private readonly IDataProtector protector = provider.CreateProtector("Erp.Assistant.ApiKey.v1");
    private volatile Cached? cache;

    private sealed record Cached(string? Key, AssistantKeyInfo Info);

    public const int MinLength = 20;
    public const int MaxLength = 300;

    /// <summary>Una clave de API no lleva espacios ni caracteres de control (iría en una cabecera HTTP).</summary>
    public static bool IsWellFormed(string? key) =>
        key is { Length: >= MinLength and <= MaxLength } && key.All(c => c is > ' ' and < (char)127);

    public async Task<string?> GetAsync(CancellationToken ct) => (await LoadAsync(ct)).Key;

    public async Task<AssistantKeyInfo> InfoAsync(CancellationToken ct) => (await LoadAsync(ct)).Info;

    public async Task SaveAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx, string? key, int userId)
    {
        if (key is null) await cn.ExecuteAsync("DELETE meta.AssistantKey", transaction: tx);
        else
            await cn.ExecuteAsync("""
                MERGE meta.AssistantKey AS t USING (SELECT 1 AS Id) AS s ON t.Id = s.Id
                WHEN MATCHED THEN UPDATE SET KeyProtected = @p, UpdatedBy = @userId, UpdatedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (Id, KeyProtected, UpdatedBy) VALUES (1, @p, @userId);
                """, new { p = protector.Protect(key), userId }, tx);
        cache = null;
    }

    public void Invalidate() => cache = null;

    private async Task<Cached> LoadAsync(CancellationToken ct)
    {
        if (cache is { } c) return c;
        if (options.Configured) return cache = new(options.ApiKey, new(AssistantKeyInfo.Server, null, null));
        await using var cn = await db.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<(string KeyProtected, DateTime UpdatedAt, string UpdatedBy)?>("""
            SELECT k.KeyProtected, k.UpdatedAt, u.DisplayName AS UpdatedBy
              FROM meta.AssistantKey k JOIN meta.[User] u ON u.Id = k.UpdatedBy WHERE k.Id = 1
            """);
        if (row is not { } r) return cache = new(null, new(null, null, null));
        try
        {
            return cache = new(protector.Unprotect(r.KeyProtected), new(AssistantKeyInfo.App, r.UpdatedAt, r.UpdatedBy));
        }
        catch (CryptographicException)
        {
            // Las llaves de Data Protection cambiaron (otra máquina, llaves borradas): hay que volver a capturar la clave.
            log.LogWarning("La clave del asistente guardada no se puede descifrar; hay que capturarla de nuevo.");
            return cache = new(null, new(null, null, null));
        }
    }
}
