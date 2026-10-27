using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Erp.Api.Studio;

/// <summary>Una llamada a una ruta de la API tal como aparece en el inventario (<c>docs/sdd/rutas.md</c>).</summary>
public sealed record RouteCall(string Method = "GET", string Route = "", IReadOnlyDictionary<string, string?>? RouteValues = null,
    IReadOnlyDictionary<string, string?>? Query = null, object? Body = null);

/// <param name="Error">El mensaje que la API le daría a la persona (ProblemDetails), nunca el detalle técnico.</param>
public sealed record RouteResult(int Status, JsonNode? Body, string? Error = null, IDictionary<string, string[]>? Errors = null)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// Spec 033 (H1): ejecuta una ruta de la API en el mismo proceso, con la identidad de la persona y por el mismo
/// <c>RequestDelegate</c> que atiende al navegador: capacidad declarada de la ruta (029), filtros de endpoint (compuerta de
/// cambio directo), <c>RecordFilter</c>, <c>FieldAccess</c>, empresa y <c>viewer</c> son los de siempre porque es la misma
/// ruta. Así una herramienta del agente (o del servidor MCP de la 035) nunca ve ni hace más que la persona.
/// </summary>
public sealed partial class RouteDispatcher(EndpointDataSource endpoints, ILogger<RouteDispatcher> log)
{
    public const string Scheme = "erp.studio";

    public static string Pattern(RouteEndpoint e) => "/" + (e.RoutePattern.RawText ?? "").TrimStart('/');

    public RouteEndpoint? Find(string method, string route) =>
        endpoints.Endpoints.OfType<RouteEndpoint>().FirstOrDefault(e =>
            string.Equals(Pattern(e), route, StringComparison.Ordinal)
            && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains(method, StringComparer.OrdinalIgnoreCase));

    /// <summary>¿La ruta cambia algo? Las de lectura son GET o declaran <c>ReadsOnly</c> (efecto «ninguno»).</summary>
    public static bool Writes(Endpoint e, string method) =>
        !HttpMethods.IsGet(method) && e.Metadata.GetMetadata<EffectMetadata>() is not { Effect: Effect.None };

    /// <param name="services">Proveedor del destino: el de la aplicación (entorno real) o el de la copia aislada.</param>
    /// <param name="allowWrites">Solo en la copia aislada. En el entorno real ninguna herramienta escribe.</param>
    public async Task<RouteResult> SendAsync(IServiceProvider services, UserContext user, RouteCall call, bool allowWrites, CancellationToken ct)
    {
        var endpoint = Find(call.Method, call.Route)
            ?? throw new InvalidOperationException($"La herramienta usa una ruta que no existe: {call.Method} {call.Route}.");
        if (!allowWrites && Writes(endpoint, call.Method))
            return new(403, null, "En el entorno real el agente solo lee: aplicar lo hace la persona.");
        var capability = endpoint.Metadata.GetMetadata<CapabilityMetadata>();
        if (capability is null || capability.AnyOf.Contains(Capability.Anonymous)
            || !capability.AnyOf.Any(c => EndpointCapabilities.Has(c, user)))
            return new(403, null, new Forbidden().Message);

        await using var scope = services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Items[UserContextExtensions.ItemKey] = user;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username)], Scheme));
        http.RequestAborted = ct;
        var req = http.Request;
        req.Method = call.Method.ToUpperInvariant();
        req.Scheme = "https";
        req.Host = new HostString("erp.studio");
        req.Path = Expand(call.Route, call.RouteValues);
        foreach (var (k, v) in call.RouteValues ?? new Dictionary<string, string?>()) req.RouteValues[k] = v;
        if (call.Query is { Count: > 0 } q) req.QueryString = QueryString.Create(q.Where(p => p.Value is not null));
        // Sin esta característica (la pone Kestrel) el enlace de parámetros de las rutas mínimas no lee el cuerpo.
        http.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(call.Body is not null));
        if (call.Body is not null)
        {
            var bytes = call.Body is JsonNode node ? Encoding.UTF8.GetBytes(node.ToJsonString()) : JsonSerializer.SerializeToUtf8Bytes(call.Body);
            req.Body = new MemoryStream(bytes);
            req.ContentLength = bytes.Length;
            req.ContentType = "application/json";
        }
        var output = new MemoryStream();
        http.Response.Body = output;
        http.SetEndpoint(endpoint);
        try
        {
            await endpoint.RequestDelegate!(http);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Constitución II.6: a la herramienta llega lo mismo que el navegador recibiría (ProblemExceptionHandler).
        catch (AppException ex) { return new(ex.Status, null, ex.Message, ex.Errors); }
        catch (Expressions.ExprException ex) { return new(400, null, $"Error al calcular: {ex.Message}"); }
        catch (Exception ex) when (ex is BadHttpRequestException or JsonException) { return new(400, null, "La petición no tiene un formato válido."); }
        catch (Exception ex)
        {
            log.LogError(ex, "Error no controlado en la herramienta {Method} {Route}", call.Method, call.Route);
            return new(500, null, "Ocurrió un error inesperado.");
        }

        var status = http.Response.StatusCode;
        JsonNode? body = null;
        if (output.Length > 0 && (http.Response.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try { body = JsonNode.Parse(output.ToArray()); }
            catch (JsonException) { body = null; }
        }
        if (status < 400) return new(status, body);
        var title = body?["title"]?.GetValueKind() == JsonValueKind.String ? body["title"]!.GetValue<string>() : null;
        IDictionary<string, string[]>? errors = null;
        if (body?["errors"] is JsonObject e)
            errors = e.Where(p => p.Value is JsonArray).ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(x => x?.ToString() ?? "").ToArray());
        return new(status, null, title ?? (status == 400 ? "La petición no tiene un formato válido." : "La API rechazó la petición."), errors);
    }

    /// <summary>La ruta del inventario con sus valores (cada valor escapado como un segmento).</summary>
    public static string Expand(string route, IReadOnlyDictionary<string, string?>? values) =>
        Param().Replace(route, m => Uri.EscapeDataString(values?.GetValueOrDefault(m.Groups[1].Value) ?? "")).TrimEnd('/') is { Length: > 0 } p ? p : "/";

    private sealed class BodyDetection(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => canHaveBody;
    }

    [GeneratedRegex(@"\{([A-Za-z]+)(?::[a-z]+)?\}")]
    private static partial Regex Param();
}
