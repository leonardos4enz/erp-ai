using Erp.Api.Approvals;
using Erp.Api.Infrastructure;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 053 (H2): la matriz de autorización de la empresa actual. La administra quien administra usuarios; cada cambio pasa por
/// la compuerta de seguridad de la 015 (aprobación de otro administrador donde se exige).
/// </summary>
public static class ApprovalEndpoints
{
    public static void MapApprovals(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/approvals").RequireAuthorization();
        g.MapGet("/", (HttpContext http, ApprovalConfig config, CancellationToken ct) => config.GetAsync(http.CurrentUser(), ct))
            .Requires(Capability.UserAdmin);
        g.MapPost("/profiles", (ApprovalProfileRequest req, HttpContext http, ApprovalConfig config, CancellationToken ct) =>
            config.SaveProfileAsync(null, req with { CompanyId = null }, http, null, ct)).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
        g.MapPut("/profiles/{id:int}", (int id, ApprovalProfileRequest req, HttpContext http, ApprovalConfig config, CancellationToken ct) =>
            config.SaveProfileAsync(id, req with { CompanyId = null }, http, null, ct)).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
        g.MapPost("/rules", (ApprovalRuleRequest req, HttpContext http, ApprovalConfig config, CancellationToken ct) =>
            config.SaveRuleAsync(null, req with { CompanyId = null }, http, null, ct)).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
        g.MapPut("/rules/{id:int}", (int id, ApprovalRuleRequest req, HttpContext http, ApprovalConfig config, CancellationToken ct) =>
            config.SaveRuleAsync(id, req with { CompanyId = null }, http, null, ct)).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
    }
}
