using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Studio;

/// <summary>
/// Spec 033 (H3): los servicios con los que se armó la aplicación (<c>AddErp</c>), para levantar los mismos servicios sobre
/// otra base. Se leen al usarse, cuando la colección ya está completa.
/// </summary>
public sealed class ServiceBlueprint(IServiceCollection services)
{
    public IReadOnlyList<ServiceDescriptor> Descriptors => services.ToList();
}

/// <summary>El entorno aislado no se pudo preparar (sin permiso para crear bases, o el modelo no se pudo copiar).</summary>
public sealed class SandboxUnavailable(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Una copia desechable del entorno: otra base con el modelo (sin datos, usuarios, empresas reales, claves ni destinos de
/// webhooks) y un proveedor de servicios propio. Las mismas rutas de la API corren aquí como su administrador.
/// </summary>
public sealed class Sandbox(string database, ServiceProvider services, UserContext admin) : IAsyncDisposable
{
    public string Database { get; } = database;
    public IServiceProvider Services => services;
    public UserContext Admin { get; } = admin;

    public ValueTask DisposeAsync() => services.DisposeAsync();
}

/// <summary>
/// Spec 033 (H3): crea, copia y borra las bases aisladas. El nombre es <c>&lt;base&gt;_&lt;huella&gt;_S&lt;sesión&gt;_SBX</c>,
/// validado como identificador y siempre distinto de la base real (CLARIFY 2): como las bases <c>_IT</c>/<c>_E2E</c> de la
/// suite, el sufijo impide que un borrado alcance la base del entorno.
/// </summary>
public sealed partial class SandboxFactory(ServiceBlueprint blueprint, Db db, IConfiguration config, MetadataStore store,
    RouteDispatcher routes, ILogger<SandboxFactory> log)
{
    public const string Suffix = "_SBX";

    /// <summary>Prefijo de las bases aisladas de este entorno (la huella distingue dos bases reales con el mismo inicio).</summary>
    public static string Prefix(string realDb)
    {
        var clean = new string(realDb.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        if (clean.Length == 0 || !char.IsAsciiLetter(clean[0])) clean = "Erp" + clean;
        if (clean.Length > 30) clean = clean[..30];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(realDb.ToUpperInvariant())))[..8].ToLowerInvariant();
        return $"{clean.TrimEnd('_')}_{hash}_S";
    }

    public static string NameFor(string realDb, int sessionId) => Validate($"{Prefix(realDb)}{sessionId}{Suffix}", realDb);

    /// <summary>Solo letras, números y «_», empieza con letra, termina en <c>_SBX</c> y no es la base real.</summary>
    public static string Validate(string name, string realDb)
    {
        if (!SandboxName().IsMatch(name) || string.Equals(name, realDb, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(Prefix(realDb), StringComparison.Ordinal))
            throw new InvalidOperationException($"Nombre de base aislada rechazado: {name}.");
        return name;
    }

    public static int? SessionOf(string name, string realDb) =>
        name.StartsWith(Prefix(realDb), StringComparison.Ordinal) && SessionId().Match(name) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    private string RealDb => new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;

    public string NameFor(int sessionId) => NameFor(RealDb, sessionId);

    public async Task<Sandbox> CreateAsync(int sessionId, CancellationToken ct)
    {
        var name = NameFor(sessionId);
        var cs = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = name }.ConnectionString;
        var settings = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Erp"] = cs,
            ["Database:AutoCreate"] = "true",
            ["Bootstrap:AdminPassword"] = "Sbx-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)) + "-9a",
            ["Environments:Name"] = "Aislado (ERP AI Studio)",
            ["Environments:RequireApproval"] = "false",
            ["Environments:AllowDirectChanges"] = "true",
            // Revisión adversarial F-01: la copia no hereda secretos del entorno (firma de paquetes, clave de Anthropic).
            ["Environments:SigningKey"] = "",
            ["Assistant:AnthropicApiKey"] = "",
            ["Assistant:AnthropicWorkspaceId"] = "",
            ["DataProtection:KeysPath"] = "",
        }).Build();

        IServiceCollection services = new ServiceCollection();
        foreach (var d in blueprint.Descriptors)
        {
            // Sin trabajos en segundo plano (webhooks, automatizaciones, Studio): en la copia nada sale ni corre solo.
            if (d.ServiceType == typeof(IHostedService) || d.ServiceType == typeof(IConfiguration) || d.ServiceType == typeof(Db)
                || d.ServiceType == typeof(EnvironmentOptions) || d.ServiceType == typeof(Assistant.AssistantOptions)
                || d.ServiceType == typeof(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider)) continue;
            services.Add(d);
        }
        services.AddSingleton<IConfiguration>(settings);
        services.AddSingleton(new Db(settings));
        services.AddSingleton(EnvironmentOptions.From(settings));
        services.AddSingleton(Assistant.AssistantOptions.From(settings));
        // F-01: llaves en memoria (nada se cifra con las llaves del entorno ni se escribe en su carpeta).
        services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var provider = services.BuildServiceProvider();
        try
        {
            // Defensa en profundidad: la copia nunca habla con la base real.
            var target = new SqlConnectionStringBuilder(provider.GetRequiredService<Db>().ConnectionString).InitialCatalog;
            if (!string.Equals(target, Validate(name, RealDb), StringComparison.Ordinal))
                throw new InvalidOperationException("La copia aislada apunta a otra base.");

            // F-09: una base con ese nombre (huérfana sin barrer, o un id repetido tras restaurar) no se reutiliza.
            await DropAsync(name);
            try { await Bootstrap.RunAsync(provider, settings, log); }
            catch (SqlException ex)
            {
                log.LogWarning("No se pudo crear la base aislada {Db}: {Message}", name, ex.Message);
                throw new SandboxUnavailable("El entorno aislado no está disponible en este servidor (no se pudo crear la base de prueba).", ex);
            }
            await CopyRolesAsync(provider, ct);
            var admin = await AdminAsync(provider, ct);
            await CopyModelAsync(provider, admin, ct);
            return new Sandbox(name, provider, admin);
        }
        catch
        {
            await provider.DisposeAsync();
            await DropAsync(name);
            throw;
        }
    }

    /// <summary>Solo los nombres (el plan puede citarlos en seguridad, procesos o webhooks); sin permisos ni usuarios.</summary>
    private async Task CopyRolesAsync(IServiceProvider sandbox, CancellationToken ct)
    {
        IReadOnlySet<string> names;
        await using (var cn = await db.OpenAsync(ct)) names = await PlanService.LoadRolesAsync(cn, null);
        await using var target = await sandbox.GetRequiredService<Db>().OpenAsync(ct);
        foreach (var n in names)
            await target.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM meta.Role WHERE Name = @n)
                    INSERT meta.Role (Name, Description, IsSystem, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit)
                    VALUES (@n, N'Copia para ERP AI Studio (sin permisos).', 0, 0, 0, 0, 0)
                """, new { n });
    }

    private static async Task<UserContext> AdminAsync(IServiceProvider sandbox, CancellationToken ct)
    {
        await using var cn = await sandbox.GetRequiredService<Db>().OpenAsync(ct);
        var (id, stamp) = await cn.QuerySingleAsync<(int, Guid)>("SELECT Id, SecurityStamp FROM meta.[User] WHERE Username = N'admin'");
        return await sandbox.GetRequiredService<UserContextLoader>().LoadAsync(id, stamp, ct)
               ?? throw new InvalidOperationException("La copia aislada no tiene administrador.");
    }

    /// <summary>El modelo activo del entorno, llevado a la copia por el Plan de Cambios (el mismo cálculo que un paquete, 009).</summary>
    private async Task CopyModelAsync(IServiceProvider sandbox, UserContext admin, CancellationToken ct)
    {
        var model = (await store.GetAsync(ct)).Values.Where(e => !e.IsArchived).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var boards = (await store.GetDashboardsAsync(ct)).Values.Where(b => !b.IsArchived).ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var apps = (await store.GetAppsAsync(ct)).Values.Where(a => !a.IsArchived).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var target = sandbox.GetRequiredService<MetadataStore>();
        var diff = ModelDiff.Compute(model, boards, await target.GetAsync(ct), await target.GetDashboardsAsync(ct),
            "ERP AI Studio: copia del modelo", null, apps, await target.GetAppsAsync(ct));
        if (diff.Plan.Operations.Count == 0) return;
        // Spec 035: un plan admite PlanCompiler.MaxOperations. Con más catálogos la copia va por tramos: el prefijo más largo
        // (hasta el máximo) que compila solo contra lo que ya tiene la copia (un documento cita su catálogo de partidas, que
        // puede crearse después en el mismo plan: un corte arbitrario no compila). El compilador es puro: probar es barato.
        var options = sandbox.GetRequiredService<System.Text.Json.JsonSerializerOptions>();
        var ops = diff.Plan.Operations;
        for (var start = 0; start < ops.Count;)
        {
            target.Invalidate();
            var current = await target.GetAsync(ct);
            var currentBoards = await target.GetDashboardsAsync(ct);
            var currentApps = await target.GetAppsAsync(ct);
            IReadOnlySet<string> roles;
            await using (var cn = await sandbox.GetRequiredService<Db>().OpenAsync(ct)) roles = await PlanService.LoadRolesAsync(cn, null);
            var take = Math.Min(PlanCompiler.MaxOperations, ops.Count - start);
            while (take > 1 && !PlanCompiler.Compile(diff.Plan with { Operations = ops.Skip(start).Take(take).ToList() }, current, roles, currentBoards, currentApps).Preview.Valid)
                take--;
            var part = diff.Plan with { Operations = ops.Skip(start).Take(take).ToList() };
            var error = await ApplyAsync(sandbox, admin, System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(part, options))!, ct);
            if (error is not null)
            {
                log.LogWarning("No se pudo copiar el modelo al entorno aislado: {Error}", error);
                throw new SandboxUnavailable("No se pudo copiar el modelo al entorno aislado: " + error);
            }
            start += take;
        }
    }

    /// <summary>
    /// Partición de los límites del despachador para la copia del modelo al preparar la copia (no es de ninguna persona ni
    /// del administrador real, que tiene el mismo id que el de la copia: revisión adversarial 034 F-02).
    /// </summary>
    public const int PreparationCaller = 0;

    /// <summary>
    /// Vista previa y aplicar en la copia, como el modelador (huella incluida). Devuelve el error o null. Spec 034: con la
    /// credencial y los límites de la persona que lo pide (<paramref name="caller"/>).
    /// </summary>
    public async Task<string?> ApplyAsync(IServiceProvider sandbox, UserContext admin, System.Text.Json.Nodes.JsonNode plan, CancellationToken ct,
        BearerCredential? credential = null, int caller = PreparationCaller)
    {
        var preview = await routes.SendAsync(sandbox, admin, new RouteCall("POST", "/api/model/plan/preview", Body: plan), allowWrites: true, ct, credential, caller);
        if (!preview.Ok) return preview.Error;
        if (preview.Body?["valid"]?.GetValue<bool>() != true)
            return string.Join(" · ", (preview.Body?["errors"] as System.Text.Json.Nodes.JsonArray ?? []).Take(10).Select(e => e?["message"]?.ToString()));
        var body = plan.DeepClone().AsObject();
        body["baseHash"] = preview.Body!["baseHash"]?.DeepClone();
        var applied = await routes.SendAsync(sandbox, admin, new RouteCall("POST", "/api/model/plan/apply", Body: body), allowWrites: true, ct, credential, caller);
        return applied.Ok ? null : applied.Error;
    }

    public async Task DropAsync(string name)
    {
        Validate(name, RealDb);
        var cs = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = name }.ConnectionString;
        using (var pooled = new SqlConnection(cs)) SqlConnection.ClearPool(pooled);
        var master = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        try
        {
            await using var cn = new SqlConnection(master);
            await cn.OpenAsync();
            var quoted = Identifiers.Quote(name);
            await cn.ExecuteAsync($"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}; END",
                new { name }, commandTimeout: 120);
        }
        catch (SqlException ex) { log.LogWarning("No se pudo borrar la base aislada {Db}: {Message}", name, ex.Message); }
    }

    /// <summary>Borra las bases aisladas de este entorno cuya sesión ya no corre (<paramref name="live"/>).</summary>
    public async Task<int> SweepAsync(Func<Task<IReadOnlySet<int>>> live, CancellationToken ct)
    {
        var prefix = Prefix(RealDb);
        var master = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        List<string> names;
        await using (var cn = new SqlConnection(master))
        {
            await cn.OpenAsync(ct);
            names = (await cn.QueryAsync<string>("SELECT name FROM sys.databases WHERE LEFT(name, LEN(@prefix)) = @prefix", new { prefix })).ToList();
        }
        // F-11: las sesiones vivas se leen después de las bases: una copia creada en medio ya tiene su sesión en curso.
        var running = await live();
        var dropped = 0;
        foreach (var n in names)
            if (SessionOf(n, RealDb) is { } id && !running.Contains(id) && SandboxName().IsMatch(n))
            {
                await DropAsync(n);
                dropped++;
            }
        return dropped;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,58}_SBX$")]
    private static partial Regex SandboxName();

    [GeneratedRegex(@"_S(\d{1,9})_SBX$")]
    private static partial Regex SessionId();
}
