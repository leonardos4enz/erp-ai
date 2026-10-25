using Erp.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Erp.Api.Infrastructure;

/// <summary>
/// Spec 029 (H1): capacidad que exige una ruta (constitución II.2: «cada endpoint declara el permiso requerido»). Se declara
/// como metadato del endpoint con <see cref="EndpointCapabilities.Requires{TBuilder}"/> y un filtro común la comprueba antes
/// del handler; el inventario (<c>docs/sdd/rutas.md</c>) se genera de estos metadatos.
/// </summary>
public enum Capability
{
    /// <summary>Sin sesión (lista blanca: inicio de sesión, salud, servidor de tokens, rutas inexistentes).</summary>
    Anonymous,
    /// <summary>Cualquier sesión válida: lo que devuelve es del propio usuario (sesión, empresa, sus vistas, sus conversaciones).</summary>
    Authenticated,
    /// <summary>Datos de un catálogo: el motor exige el permiso del catálogo, el alcance por registro y la seguridad por campo.</summary>
    Records,
    /// <summary>Administrar el modelo (<see cref="UserContext.CanManageModel"/>; el superusuario la tiene).</summary>
    Modeler,
    /// <summary>Administrar usuarios (<see cref="UserContext.CanManageUsers"/>).</summary>
    UserAdmin,
    /// <summary>Ver la auditoría (<see cref="UserContext.CanViewAudit"/>).</summary>
    Audit,
    /// <summary>Administrador del sistema.</summary>
    Superuser,
}

/// <summary>Qué cambia una ruta que escribe. Las de lectura no lo declaran.</summary>
public enum Effect
{
    /// <summary>No guarda nada aunque no sea GET (cálculo en vivo, vista previa, validar una expresión).</summary>
    None,
    /// <summary>Registros de catálogos (pasa por el motor: reglas, auditoría, automatizaciones).</summary>
    Data,
    /// <summary>Metadatos del modelo (catálogos, plantillas, imágenes, módulos, paquetes).</summary>
    Model,
    /// <summary>Quién puede qué: usuarios, roles, unidades, equipos, claves, clientes OAuth, empresas de usuarios.</summary>
    Security,
    /// <summary>Configuración del entorno que no es modelo ni seguridad (empresas, tipos de cambio, webhooks, tareas, clave del asistente).</summary>
    Config,
    /// <summary>Solo la sesión o lo personal de quien llama (cerrar sesión, cambiar de empresa, sus vistas, descartar su solicitud).</summary>
    Session,
}

/// <summary>Por qué compuerta pasa un cambio al modelo o a la seguridad.</summary>
public static class Gates
{
    /// <summary>Cambio directo al modelo: solo donde el entorno lo permite (<c>EnvironmentOptions.RequireDirectChange</c>, spec 012).</summary>
    public const string DirectChange = "cambio directo";
    /// <summary>Paquete de entorno: aprobación de otro administrador donde se exige (spec 009).</summary>
    public const string PackageApproval = "aprobación del paquete";
    /// <summary>Cambio de seguridad: se aplica o espera aprobación de otro administrador (spec 015, <c>SecurityApproval</c>).</summary>
    public const string SecurityApproval = "aprobación de seguridad";
    /// <summary>Reducir o revisar: no amplía acceso (revocar, rechazar, aprobar con las reglas de quién aprueba).</summary>
    public const string NoGrant = "no amplía acceso";
    /// <summary>Datos del entorno que solo el superusuario cambia (sin flujo de aprobación).</summary>
    public const string SuperuserOnly = "solo superusuario";
}

public sealed record CapabilityMetadata(IReadOnlyList<Capability> AnyOf)
{
    public override string ToString() => string.Join(" o ", AnyOf.Select(Name));

    public static string Name(Capability c) => c switch
    {
        Capability.Anonymous => "anónima",
        Capability.Authenticated => "sesión",
        Capability.Records => "registros",
        Capability.Modeler => "modelador",
        Capability.UserAdmin => "usuarios",
        Capability.Audit => "auditoría",
        Capability.Superuser => "superusuario",
        _ => c.ToString(),
    };
}

public sealed record CapabilityRequirement(CapabilityMetadata Metadata) : IAuthorizationRequirement;

/// <summary>Cumple el requisito si el usuario de la petición (cargado por la autenticación) tiene alguna de las capacidades.</summary>
public sealed class CapabilityHandler : AuthorizationHandler<CapabilityRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CapabilityRequirement requirement)
    {
        if (context.Resource is HttpContext http && http.Items[UserContextExtensions.ItemKey] is UserContext u
            && requirement.Metadata.AnyOf.Any(c => EndpointCapabilities.Has(c, u)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Spec 031 (H6): requisito de la política por omisión (<c>FallbackPolicy</c>). Ningún manejador lo cumple: una ruta que no
/// declara su capacidad no responde a nadie, ni al superusuario (401 sin sesión, 403 con ella).
/// </summary>
public sealed class UndeclaredRouteRequirement : IAuthorizationRequirement;

/// <summary>Sin la capacidad: el mismo ProblemDetails que <see cref="Forbidden"/> (constitución II.6), no un 403 vacío.</summary>
public sealed class CapabilityResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext http, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && result.AuthorizationFailure?.FailedRequirements.Any(r => r is CapabilityRequirement or UndeclaredRouteRequirement) == true)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails = { Status = 403, Title = new Forbidden().Message },
            });
            return;
        }
        await _default.HandleAsync(next, http, policy, result);
    }
}

public sealed record EffectMetadata(Effect Effect, string? Gate);

/// <summary>Ruta costosa (CPU, memoria, LLM o búsqueda en muchos catálogos): debe tener límite de frecuencia.</summary>
public sealed record CostlyMetadata(string Why, string? OwnLimit);

/// <summary>Paso «aplicar» de un «revisar → aplicar»: recibe la huella de lo revisado (lecciones 012-5, 021-2).</summary>
public sealed record ReviewApplyMetadata(string Fingerprint);

public static class EndpointCapabilities
{
    /// <summary>
    /// Declara la capacidad (o capacidades alternativas) de la ruta y la exige. Una sola declaración por ruta y nunca en un
    /// grupo: la prueba de inventario lo comprueba.
    /// </summary>
    public static TBuilder Requires<TBuilder>(this TBuilder builder, params Capability[] anyOf) where TBuilder : IEndpointConventionBuilder
    {
        if (anyOf.Length == 0) throw new ArgumentException("Declara al menos una capacidad.", nameof(anyOf));
        var meta = new CapabilityMetadata(anyOf);
        builder.WithMetadata(meta);
        if (anyOf.Contains(Capability.Anonymous))
        {
            builder.Add(e => e.Metadata.Add(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute()));
            return builder;
        }
        // Requisito de autorización (no filtro): se evalúa en UseAuthorization, antes de leer el cuerpo de la petición.
        builder.RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new CapabilityRequirement(meta)).Build());
        return builder;
    }

    public static bool Has(Capability c, UserContext u) => c switch
    {
        Capability.Anonymous or Capability.Authenticated or Capability.Records => true,
        Capability.Modeler => u.CanManageModel,
        Capability.UserAdmin => u.CanManageUsers,
        Capability.Audit => u.CanViewAudit,
        Capability.Superuser => u.IsSuperuser,
        _ => false,
    };

    /// <summary>
    /// Declara qué cambia la ruta. Con <see cref="Gates.DirectChange"/> el filtro exige además que el entorno permita
    /// cambios directos al modelo (spec 012): la declaración es la compuerta.
    /// </summary>
    public static TBuilder Writes<TBuilder>(this TBuilder builder, Effect effect, string? gate = null) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new EffectMetadata(effect, gate));
        if (gate == Gates.DirectChange)
            builder.AddEndpointFilter(async (ctx, next) =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<Environments.EnvironmentOptions>().RequireDirectChange("cambiar el modelo directamente");
                return await next(ctx);
            });
        return builder;
    }

    public static TBuilder ReadsOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new EffectMetadata(Effect.None, null));

    /// <param name="ownLimit">El límite no es una política de frecuencia sino del servicio (p. ej. solicitudes por hora del asistente).</param>
    public static TBuilder Costly<TBuilder>(this TBuilder builder, string why, string? ownLimit = null) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new CostlyMetadata(why, ownLimit));

    public static TBuilder AppliesReviewed<TBuilder>(this TBuilder builder, string fingerprint) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ReviewApplyMetadata(fingerprint));
}
