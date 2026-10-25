using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 015 (H4): solicitudes de cambios de seguridad. Aprobar ejecuta la misma operación con la carga guardada y el
/// aprobador como autor; la operación compara el antes en su transacción y marca la solicitud como aplicada en ella.
/// </summary>
public static class SecurityChangeEndpoints
{
    public static void MapSecurityChanges(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/security-changes").RequireAuthorization();
        g.MapGet("/", (string? status, HttpContext http, SecurityApproval gate, CancellationToken ct) => gate.ListAsync(http.CurrentUser(), status, ct))
            .Requires(Capability.UserAdmin, Capability.Superuser);
        // La aprobación reaplica la operación con su propia regla (quién la revisa, beneficiario, huella).
        g.MapPost("/{id:int}/approve", Approve).Requires(Capability.UserAdmin, Capability.Superuser).Writes(Effect.Security, Gates.SecurityApproval)
            .AppliesReviewed("hash");
        g.MapPost("/{id:int}/reject", (int id, ReviewSecurityChangeRequest req, HttpContext http, SecurityApproval gate, CancellationToken ct) =>
            gate.RejectAsync(id, req, http, ct)).Requires(Capability.UserAdmin, Capability.Superuser).Writes(Effect.Security, Gates.NoGrant);
    }

    private static async Task<SecurityChangeDto> Approve(int id, ReviewSecurityChangeRequest req, HttpContext http, SecurityApproval gate, Db db,
        MetadataStore store, ApiKeyService keys, OAuthClientService clients, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var approving = await gate.ClaimAsync(id, req, user, ct);
        try
        {
            var target = approving.TargetId ?? 0;
            _ = approving.Kind switch
            {
                SecurityChangeKinds.UserCreate => await AdminEndpoints.CreateUserCore(approving.Read<AdminEndpoints.UserPayload>(), http, db, gate, approving),
                SecurityChangeKinds.UserUpdate => await AdminEndpoints.UpdateUserCore(target, approving.Read<AdminEndpoints.UserPayload>(), http, db, gate, approving),
                SecurityChangeKinds.RoleCreate => await AdminEndpoints.SaveRole(null, approving.Read<AdminEndpoints.RoleRequest>(), http, db, store, gate, approving, ct),
                SecurityChangeKinds.RoleUpdate => await AdminEndpoints.SaveRole(target, approving.Read<AdminEndpoints.RoleRequest>(), http, db, store, gate, approving, ct),
                SecurityChangeKinds.TeamCreate => await AdminEndpoints.SaveTeam(null, approving.Read<AdminEndpoints.TeamRequest>(), http, db,
                    http.RequestServices.GetRequiredService<PasswordService>(), gate, approving),
                SecurityChangeKinds.TeamUpdate => await AdminEndpoints.SaveTeam(target, approving.Read<AdminEndpoints.TeamRequest>(), http, db,
                    http.RequestServices.GetRequiredService<PasswordService>(), gate, approving),
                SecurityChangeKinds.UnitMove => await AdminEndpoints.MoveUnit(target, approving.Read<AdminEndpoints.MoveUnitRequest>(), http, db, gate, approving),
                SecurityChangeKinds.ApiKeyCreate => await keys.CreateCoreAsync(approving.Read<ApiKeyService.ApiKeyPayload>(), null, http, approving, ct),
                SecurityChangeKinds.ApiKeyUpdate => await keys.UpdateCoreAsync(target, approving.Read<UpdateApiKeyRequest>(), http, approving, ct),
                SecurityChangeKinds.OAuthClientCreate => await clients.CreateCoreAsync(approving.Read<OAuthClientService.OAuthClientPayload>(), null, http, approving, ct),
                SecurityChangeKinds.OAuthClientUpdate => await clients.UpdateCoreAsync(target, approving.Read<SaveOAuthClientRequest>(), http, approving, ct),
                _ => throw new Conflict("Tipo de solicitud desconocido."),
            };
        }
        catch
        {
            // La operación no se aplicó (su transacción se revirtió): la solicitud vuelve a quedar pendiente.
            await gate.ReleaseAsync(id);
            throw;
        }
        return await gate.GetAsync(id, user, ct);
    }
}
