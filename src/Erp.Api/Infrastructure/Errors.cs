using Erp.Api.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Api.Infrastructure;

/// <summary>Errores esperados del dominio. Su mensaje es seguro para mostrarse al usuario.</summary>
public class AppException(int status, string title, IDictionary<string, string[]>? errors = null) : Exception(title)
{
    public int Status { get; } = status;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed class ValidationFailed(IDictionary<string, string[]> errors, string title = "Hay datos inválidos.")
    : AppException(StatusCodes.Status400BadRequest, title, errors)
{
    /// <summary>Spec 045 (D-05): catálogo (raíz) y campo de un choque de valor único, para su rastro en la bitácora.</summary>
    public (string Entity, string Field)? Unique { get; init; }
}

public sealed class NotFound(string title = "No encontrado.") : AppException(StatusCodes.Status404NotFound, title);
public sealed class Forbidden(string title = "No tienes permiso para esta acción.") : AppException(StatusCodes.Status403Forbidden, title);
public sealed class Conflict(string title) : AppException(StatusCodes.Status409Conflict, title);

/// <summary>
/// Constitución II.6: al cliente solo llega ProblemDetails. Las excepciones no esperadas
/// se registran con detalle y se responden con un mensaje genérico + traceId.
/// </summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        ProblemDetails pd;
        if (ex is AppException app)
        {
            pd = new ProblemDetails { Status = app.Status, Title = app.Message };
            if (app.Errors is not null) pd.Extensions["errors"] = app.Errors;
            if (ex is ValidationFailed { Unique: { } unique }) await UniqueTrailAsync(http, unique.Entity, unique.Field, 1, ct);
        }
        else if (ex is Expressions.ExprException expr)
        {
            // Un error de fórmula es un problema del modelo: el mensaje es seguro y útil para el modelador.
            log.LogWarning("Error al evaluar expresión en {Path}: {Message}", http.Request.Path, expr.Message);
            pd = new ProblemDetails { Status = 400, Title = $"Error al calcular: {expr.Message}" };
        }
        else if (ex is BadHttpRequestException or System.Text.Json.JsonException)
        {
            pd = new ProblemDetails { Status = 400, Title = "La petición no tiene un formato válido." };
        }
        else
        {
            log.LogError(ex, "Error no controlado en {Path}", http.Request.Path);
            pd = new ProblemDetails { Status = 500, Title = "Ocurrió un error inesperado." };
        }
        http.Response.StatusCode = pd.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd, Exception = ex });
    }

    /// <summary>
    /// Spec 045 (auditoría 8, D-05): el rastro de un choque de valor único, en su propia conexión (la transacción del alta ya
    /// se revirtió). Es rastro, no control: si falla, se registra en el log y la respuesta no cambia.
    /// </summary>
    internal static async Task UniqueTrailAsync(HttpContext http, string entity, string field, int rows, CancellationToken ct)
    {
        try
        {
            await using var cn = await http.RequestServices.GetRequiredService<Db>().OpenAsync(ct);
            await Audit.WriteAsync(cn, null, http, AuditActions.UniqueConflict, entity, null, after: new { field, rows });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILogger<ProblemExceptionHandler>>().LogWarning(e, "No se pudo registrar el choque de valor único en {Entity}.{Field}", entity, field);
        }
    }
}
