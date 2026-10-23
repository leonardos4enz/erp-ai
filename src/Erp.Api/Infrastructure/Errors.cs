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
    : AppException(StatusCodes.Status400BadRequest, title, errors);

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
}
