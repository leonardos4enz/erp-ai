using System.Security.Claims;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Erp.Api.Endpoints;

public static class AuthEndpoints
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const string StampClaim = "erp:stamp";

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record MeResponse(int Id, string Username, string DisplayName, IReadOnlyList<string> Roles,
        bool IsSuperuser, bool CanManageModel, bool CanManageUsers, bool CanViewAudit,
        IReadOnlyDictionary<string, EntityAccess> Entities, string? Unit);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");
        g.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting("login");
        g.MapPost("/logout", Logout).RequireAuthorization();
        g.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Login(LoginRequest req, HttpContext http, Db db, PasswordService passwords,
        UserContextLoader loader, MetadataStore store, CancellationToken ct)
    {
        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";
        // Mensaje único para cualquier fallo: no revela si el usuario existe, está inactivo o bloqueado.
        var invalid = Results.Problem(title: "Usuario o contraseña incorrectos, o la cuenta está bloqueada temporalmente.", statusCode: 401);
        if (username.Length is 0 or > 100 || password.Length is 0 or > PasswordService.MaxLength) return invalid;

        await using var cn = await db.OpenAsync(ct);
        var user = await cn.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT Id, Username, PasswordHash, SecurityStamp, IsActive, FailedAttempts, LockoutEnd FROM meta.[User] WHERE Username = @username",
            new { username });

        var ok = passwords.Verify(user?.PasswordHash, password, out var needsRehash);
        var locked = user?.LockoutEnd > DateTime.UtcNow;

        if (user is null || !user.IsActive || locked || !ok)
        {
            if (user is not null && !locked && !ok)
            {
                var attempts = user.FailedAttempts + 1;
                await cn.ExecuteAsync("""
                    UPDATE meta.[User] SET FailedAttempts = CASE WHEN @lock = 1 THEN 0 ELSE @attempts END,
                        LockoutEnd = CASE WHEN @lock = 1 THEN @until ELSE LockoutEnd END WHERE Id = @Id
                    """, new { user.Id, attempts, @lock = attempts >= MaxFailedAttempts, until = DateTime.UtcNow.Add(LockoutDuration) });
            }
            await Audit.WriteAsync(cn, null, http, AuditActions.LoginFailed, userId: user?.Id, username: username[..Math.Min(username.Length, 100)]);
            return invalid;
        }

        await cn.ExecuteAsync("""
            UPDATE meta.[User] SET FailedAttempts = 0, LockoutEnd = NULL, LastLoginAt = SYSUTCDATETIME(),
                PasswordHash = COALESCE(@rehash, PasswordHash) WHERE Id = @Id
            """, new { user.Id, rehash = needsRehash ? passwords.Hash(password) : null });

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(StampClaim, user.SecurityStamp.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        var ctx = (await loader.LoadAsync(user.Id, user.SecurityStamp, ct))!;
        http.Items[UserContextExtensions.ItemKey] = ctx;
        await Audit.WriteAsync(cn, null, http, AuditActions.LoginOk);
        return Results.Ok(await BuildMe(ctx, store, ct));
    }

    private static async Task<IResult> Logout(HttpContext http, Db db)
    {
        await using (var cn = await db.OpenAsync())
            await Audit.WriteAsync(cn, null, http, AuditActions.Logout);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(HttpContext http, MetadataStore store, CancellationToken ct) =>
        Results.Ok(await BuildMe(http.CurrentUser(), store, ct));

    internal static async Task<MeResponse> BuildMe(UserContext u, MetadataStore store, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var entities = model.Values.Where(e => !e.IsArchived && !e.IsChild)
            .Select(e => (e.Name, Access: u.Access(e.Name)))
            .Where(x => x.Access.Read || x.Access.Create)
            .ToDictionary(x => x.Name, x => x.Access);
        return new MeResponse(u.Id, u.Username, u.DisplayName, u.Roles, u.IsSuperuser, u.CanManageModel,
            u.CanManageUsers, u.CanViewAudit, entities, u.Unit);
    }

    private sealed record UserRow(int Id, string Username, string PasswordHash, Guid SecurityStamp, bool IsActive,
        int FailedAttempts, DateTime? LockoutEnd);
}
