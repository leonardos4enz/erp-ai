using System.Globalization;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 010: empresas, tipos de cambio y empresa actual. Son datos del entorno (no viajan en paquetes): los administra el
/// superusuario; los administradores de usuarios solo consultan la lista para asignarlas.
/// </summary>
public static class CompanyEndpoints
{
    public const int MaxName = 150;
    public const int MaxRates = 200;
    public const decimal MaxRate = 100_000_000_000m;

    public sealed record CompanyDto(int Id, string Code, string Name, string BaseCurrency, bool IsDefault, bool IsActive, int Users);
    public sealed record CompanyRequest(string? Code, string? Name, string? BaseCurrency, bool IsActive = true);
    public sealed record RateDto(int Id, string From, string To, string Date, decimal Rate, string UpdatedBy, DateTime UpdatedAt);
    public sealed record RateRequest(string? From, string? To, string? Date, decimal? Rate);
    public sealed record SwitchRequest(int CompanyId);

    public static void MapCompanies(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/company", Switch).RequireAuthorization();

        var g = app.MapGroup("/api/admin").RequireAuthorization();
        g.MapGet("/companies", async (HttpContext http, Db db) =>
        {
            var u = http.CurrentUser();
            if (!u.IsSuperuser && !u.CanManageUsers) throw new Forbidden();
            await using var cn = await db.OpenAsync();
            return await LoadAsync(cn, null);
        });
        g.MapPost("/companies", (CompanyRequest req, HttpContext http, Db db, MetadataStore store) => Save(null, req, http, db, store));
        g.MapPut("/companies/{id:int}", (int id, CompanyRequest req, HttpContext http, Db db, MetadataStore store) => Save(id, req, http, db, store));
        g.MapGet("/exchange-rates", ListRates);
        g.MapPut("/exchange-rates", SaveRate);
        g.MapDelete("/exchange-rates/{id:int}", DeleteRate);
    }

    private static void RequireSuperuser(HttpContext http)
    {
        if (!http.CurrentUser().IsSuperuser) throw new Forbidden();
    }

    // ---------------- Empresa actual ----------------

    /// <summary>Solo una de sus empresas activas (el superusuario, cualquiera activa): se valida contra las calculadas en el servidor.</summary>
    private static async Task<AuthEndpoints.MeResponse> Switch(SwitchRequest req, HttpContext http, Db db, UserContextLoader loader,
        MetadataStore store, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (user.IsService) throw new Forbidden();
        var target = user.Companies.FirstOrDefault(c => c.Id == req.CompanyId)
                     ?? throw new AppException(400, "La empresa no existe, está inactiva o no la tienes asignada.");
        await using var cn = await db.OpenAsync(ct);
        await cn.ExecuteAsync("UPDATE meta.[User] SET CompanyId = @id WHERE Id = @userId", new { id = target.Id, userId = user.Id });
        await Audit.WriteAsync(cn, null, http, AuditActions.CompanySwitch, "Usuario", user.Id.ToString(CultureInfo.InvariantCulture),
            before: new { company = user.Company?.Code }, after: new { company = target.Code });
        var ctx = await loader.LoadAsync(user.Id, user.SecurityStamp, ct) ?? throw new AppException(401, "Sesión no válida.");
        http.Items[UserContextExtensions.ItemKey] = ctx;
        return await AuthEndpoints.BuildMe(ctx, store, ct);
    }

    // ---------------- Empresas ----------------

    private static async Task<CompanyDto> Save(int? id, CompanyRequest req, HttpContext http, Db db, MetadataStore store)
    {
        RequireSuperuser(http);
        var errors = new Dictionary<string, string[]>();
        var code = req.Code?.Trim().ToUpperInvariant() ?? "";
        if (id is null && !Companies.IsCode(code)) errors["code"] = ["Usa de 2 a 20 letras sin acento, números, '_' o '-'."];
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxName) errors["name"] = [$"El nombre es obligatorio (máx. {MaxName})."];
        var currency = req.BaseCurrency?.Trim().ToUpperInvariant() ?? "";
        if (!Companies.IsCurrency(currency)) errors["baseCurrency"] = ["Usa el código ISO de 3 letras (p. ej. MXN, USD)."];
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        CompanyDto? before = null;
        if (id is not null)
        {
            before = (await LoadAsync(cn, tx)).FirstOrDefault(c => c.Id == id) ?? throw new NotFound("La empresa no existe.");
            if (before.IsDefault && !req.IsActive)
                throw new ValidationFailed(new Dictionary<string, string[]> { ["isActive"] = ["La empresa predeterminada no se puede desactivar."] });
            // Spec 012 (H7): sus importes «en moneda base» ya están registrados en la moneda anterior; cambiarla los reinterpreta.
            if (!string.Equals(before.BaseCurrency.Trim(), currency, StringComparison.Ordinal))
                foreach (var e in (await store.GetAsync()).Values.Where(e => e.PerCompany && !e.IsChild))
                    if (await cn.ExecuteScalarAsync<int?>($"SELECT TOP 1 1 FROM {Identifiers.AppTable(e.Name)} WHERE {Identifiers.Quote(DdlGenerator.CompanyColumn)} = @id",
                            new { id }, tx) is not null)
                        throw new Conflict($"No se puede cambiar la moneda base: la empresa ya tiene registros en {e.LabelPlural}. Sus importes están en {before.BaseCurrency}.");
            await cn.ExecuteAsync("""
                UPDATE meta.Company SET Name = @name, BaseCurrency = @currency, IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id
                """, new { id, name, currency, req.IsActive }, tx);
        }
        else
        {
            if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.Company WHERE Code = @code", new { code }, tx) is not null)
                throw new ValidationFailed(new Dictionary<string, string[]> { ["code"] = ["Ya existe una empresa con esa clave."] });
            id = await cn.ExecuteScalarAsync<int>("""
                INSERT meta.Company (Code, Name, BaseCurrency, IsActive) OUTPUT inserted.Id VALUES (@code, @name, @currency, @IsActive)
                """, new { code, name, currency, req.IsActive }, tx);
        }
        var after = (await LoadAsync(cn, tx)).First(c => c.Id == id);
        await Audit.WriteAsync(cn, tx, http, AuditActions.CompanyChange, "Empresa", after.Code, before, after);
        await tx.CommitAsync();
        return after;
    }

    private static async Task<IReadOnlyList<CompanyDto>> LoadAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx) =>
        (await cn.QueryAsync<CompanyRow>("""
            SELECT c.Id, c.Code, c.Name, c.BaseCurrency, c.IsDefault, c.IsActive,
                   (SELECT COUNT(*) FROM meta.UserCompany uc JOIN meta.[User] u ON u.Id = uc.UserId
                     WHERE uc.CompanyId = c.Id AND u.IsService = 0) AS Users
              FROM meta.Company c ORDER BY c.IsDefault DESC, c.Name
            """, transaction: tx))
        .Select(c => new CompanyDto(c.Id, c.Code, c.Name, c.BaseCurrency.Trim(), c.IsDefault, c.IsActive, c.Users)).ToList();

    // ---------------- Tipos de cambio ----------------

    private static async Task<IEnumerable<RateDto>> ListRates(string? from, string? to, HttpContext http, Db db)
    {
        RequireSuperuser(http);
        string? Code(string? c) => string.IsNullOrWhiteSpace(c) ? null
            : c.Trim().ToUpperInvariant() is var x && Companies.IsCurrency(x) ? x : throw new AppException(400, "Moneda no válida.");
        await using var cn = await db.OpenAsync();
        return await QueryRatesAsync(cn, null, "(@from IS NULL OR r.FromCurrency = @from) AND (@to IS NULL OR r.ToCurrency = @to)",
            new { from = Code(from), to = Code(to) });
    }

    private static async Task<RateDto> SaveRate(RateRequest req, HttpContext http, Db db)
    {
        RequireSuperuser(http);
        var errors = new Dictionary<string, string[]>();
        var from = req.From?.Trim().ToUpperInvariant() ?? "";
        var to = req.To?.Trim().ToUpperInvariant() ?? "";
        if (!Companies.IsCurrency(from)) errors["from"] = ["Usa el código ISO de 3 letras (p. ej. USD)."];
        if (!Companies.IsCurrency(to)) errors["to"] = ["Usa el código ISO de 3 letras (p. ej. MXN)."];
        else if (from == to) errors["to"] = ["Elige dos monedas distintas."];
        if (!DateOnly.TryParseExact(req.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            errors["date"] = ["Indica la fecha."];
        if (req.Rate is not { } rate || rate <= 0 || rate >= MaxRate) errors["rate"] = ["El tipo de cambio debe ser mayor a cero."];
        else if (decimal.Round(rate, 8) != rate) errors["rate"] = ["Máximo 8 decimales."];
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var day = date.ToDateTime(TimeOnly.MinValue);
        var args = new { from, to, day };
        var before = (await QueryRatesAsync(cn, tx, "r.FromCurrency = @from AND r.ToCurrency = @to AND r.RateDate = @day", args)).FirstOrDefault();
        var user = http.CurrentUser();
        var id = await cn.ExecuteScalarAsync<int>("""
            MERGE meta.ExchangeRate WITH (HOLDLOCK) AS t
            USING (SELECT @from AS FromCurrency, @to AS ToCurrency, @day AS RateDate) AS s
               ON t.FromCurrency = s.FromCurrency AND t.ToCurrency = s.ToCurrency AND t.RateDate = s.RateDate
            WHEN MATCHED THEN UPDATE SET Rate = @rate, UpdatedBy = @uid, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (FromCurrency, ToCurrency, RateDate, Rate, UpdatedBy) VALUES (@from, @to, @day, @rate, @uid)
            OUTPUT inserted.Id;
            """, new { from, to, day, rate = req.Rate, uid = user.Id }, tx);
        var after = (await QueryRatesAsync(cn, tx, "r.Id = @id", new { id })).First();
        await Audit.WriteAsync(cn, tx, http, AuditActions.RateChange, "TipoCambio", $"{from}/{to}/{after.Date}", before, after);
        await tx.CommitAsync();
        return after;
    }

    private static async Task<IResult> DeleteRate(int id, HttpContext http, Db db)
    {
        RequireSuperuser(http);
        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var before = (await QueryRatesAsync(cn, tx, "r.Id = @id", new { id })).FirstOrDefault() ?? throw new NotFound("El tipo de cambio no existe.");
        await cn.ExecuteAsync("DELETE meta.ExchangeRate WHERE Id = @id", new { id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.RateChange, "TipoCambio", $"{before.From}/{before.To}/{before.Date}", before: before);
        await tx.CommitAsync();
        return Results.NoContent();
    }

    /// <summary><paramref name="where"/> es una condición fija de este archivo; los valores van como parámetros.</summary>
    private static async Task<IEnumerable<RateDto>> QueryRatesAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx,
        string where, object args) =>
        (await cn.QueryAsync<RateRow>($"""
            SELECT TOP ({MaxRates}) r.Id, r.FromCurrency, r.ToCurrency, r.RateDate, r.Rate, u.DisplayName, r.UpdatedAt
              FROM meta.ExchangeRate r JOIN meta.[User] u ON u.Id = r.UpdatedBy
             WHERE {where} ORDER BY r.RateDate DESC, r.FromCurrency, r.ToCurrency
            """, args, tx))
        .Select(r => new RateDto(r.Id, r.FromCurrency, r.ToCurrency, r.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.Rate, r.DisplayName, DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)))
        .ToList();

    private sealed record CompanyRow(int Id, string Code, string Name, string BaseCurrency, bool IsDefault, bool IsActive, int Users);
    private sealed record RateRow(int Id, string FromCurrency, string ToCurrency, DateTime RateDate, decimal Rate, string DisplayName, DateTime UpdatedAt);
}
