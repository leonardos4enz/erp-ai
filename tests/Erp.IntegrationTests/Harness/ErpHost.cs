using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace Erp.IntegrationTests.Harness;

/// <summary>
/// Spec 030 (H1): la API real (<c>Program</c>, con la composición de <c>ErpApp</c> de la 029) sobre una base propia por clase
/// de prueba, con <see cref="WebApplicationFactory{TEntryPoint}"/> y sin navegador ni puertos. Se usa como
/// <c>IClassFixture&lt;ErpHost&gt;</c> (o una subclase que cambie la configuración); la base se crea al empezar la clase y se
/// borra al terminar. Las opciones son las de la suite E2E (playwright.config.ts): entorno con aprobación y cambios
/// directos permitidos, bandejas que revisan cada segundo y límites de frecuencia altos.
/// </summary>
public class ErpHost : IAsyncLifetime
{
    /// <summary>Servidor de SQL: el mismo que la suite E2E (<c>E2E_SQL_SERVER</c>) o LocalDB.</summary>
    public static readonly string SqlServer = Environment.GetEnvironmentVariable("E2E_SQL_SERVER") is { Length: > 0 } s ? s : @"(localdb)\MSSQLLocalDB";

    public string Database { get; }
    public string AdminPassword { get; } = "It-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', 'a').Replace('/', 'b');
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "erp-it-keys", Guid.NewGuid().ToString("N"));
    private readonly List<Factory> _factories = [];
    private Factory? _main;

    public ErpHost()
    {
        var prefix = Regex.Replace(GetType().Name, "[^A-Za-z0-9]", "");
        Database = ValidateDatabase($"ErpAi_{(prefix.Length > 40 ? prefix[..40] : prefix)}_{RandomNumberGenerator.GetHexString(8, lowercase: true)}_IT");
    }

    /// <summary>
    /// Solo bases desechables: letras, números y «_», terminadas en <c>_IT</c> o <c>_E2E</c>. El nombre se interpola en
    /// <c>DROP DATABASE</c> y así nunca puede ser la base del dueño (<c>ErpAi</c>) ni llevar comillas o corchetes.
    /// </summary>
    public static string ValidateDatabase(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{1,100}$") || !(name.EndsWith("_IT", StringComparison.Ordinal) || name.EndsWith("_E2E", StringComparison.Ordinal))
            || string.Equals(name, "ErpAi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Por seguridad solo se usan bases que terminan en _IT o _E2E (recibido: {name}).");
        return name;
    }

    public string ConnectionString => new SqlConnectionStringBuilder
    {
        DataSource = SqlServer, InitialCatalog = Database, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true,
    }.ConnectionString;

    /// <summary>Opciones de la instancia principal; una subclase las cambia (p. ej. sin cambios directos).</summary>
    protected virtual void Configure(IDictionary<string, string?> settings) { }

    public virtual async Task InitializeAsync()
    {
        await DropAsync();
        _main = Start(s => Configure(s), autoCreate: true);
        await _main.StartAsync();
    }

    /// <summary>
    /// Otra instancia sobre la misma base (como dos servidores detrás de un balanceador, o la instancia «producción» de la
    /// 012): mismas llaves y contraseña, opciones propias. Se detiene con el anfitrión.
    /// </summary>
    public async Task<Factory> SiblingAsync(Action<IDictionary<string, string?>> configure)
    {
        var f = Start(s => { Configure(s); configure(s); }, autoCreate: false);
        await f.StartAsync();
        return f;
    }

    private Factory Start(Action<IDictionary<string, string?>> configure, bool autoCreate)
    {
        var root = RepoRoot();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Erp"] = ConnectionString,
            ["Database:AutoCreate"] = autoCreate ? "true" : "false",
            ["Bootstrap:AdminPassword"] = AdminPassword,
            ["DataProtection:KeysPath"] = _keys,
            ["Modules:Path"] = Path.Combine(root, "modules"),
            ["Spa:Path"] = Path.Combine(_keys, "sin-spa"), // la integración no sirve la SPA
            ["Security:LoginAttemptsPerMinute"] = "1000",
            ["Security:RequestsPerMinute"] = "100000",
            ["Security:PrintsPerMinute"] = "1000",
            ["Security:BulkPerMinute"] = "1000",
            ["Security:SearchesPerMinute"] = "1000",
            ["Webhooks:PollSeconds"] = "1",
            ["Webhooks:RetryDelaysSeconds"] = "1,1",
            ["Automations:PollSeconds"] = "1",
            ["Automations:RetryDelaysSeconds"] = "1,1",
            ["Environments:Name"] = "Integración",
            ["Environments:RequireApproval"] = "true",
            ["Environments:SigningKey"] = _signingKey,
            ["Environments:AllowDirectChanges"] = "true",
            ["Assistant:BaseUrl"] = "http://127.0.0.1:9", // ninguna prueba de integración habla con un proveedor
        };
        configure(settings);
        var f = new Factory(Path.Combine(root, "src", "Erp.Api"), settings);
        _factories.Add(f);
        return f;
    }

    /// <summary>Sesión de admin en la instancia principal (una nueva en cada llamada).</summary>
    public Task<Client> AdminAsync() => LoginAsync("admin", AdminPassword);

    public Task<Client> LoginAsync(string username, string password) => (_main ?? throw new InvalidOperationException("El anfitrión no arrancó.")).LoginAsync(username, password);

    public Client Anonymous(bool csrfHeader = true) => _main!.Anonymous(csrfHeader);

    /// <summary>SQL directo sobre la base de la clase (preparación de datos e inyección de fallas, nunca lo que se prueba).</summary>
    /// Síncrono dentro de <c>Task.Run</c>: con canalizaciones con nombre (LocalDB) una E/S asíncrona de SqlClient se anula si
    /// el hilo que la inició termina («La operación de E/S se anuló por una salida de subproceso»), y las pruebas de
    /// concurrencia crean y retiran hilos del pool.
    public Task<T?> ScalarAsync<T>(string sql) => Task.Run(() =>
    {
        using var cn = new SqlConnection(ConnectionString);
        cn.Open();
        using var cmd = new SqlCommand("SET NOCOUNT ON; " + sql, cn) { CommandTimeout = 120 };
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? default : (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    });

    public async Task ExecAsync(string sql) => await ScalarAsync<object>(sql);

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
        await DropAsync();
        try { Directory.Delete(_keys, recursive: true); } catch (IOException) { /* llaves en uso: quedan en %TEMP% */ }
    }

    private async Task DropAsync()
    {
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var cn = new SqlConnection(master);
        await cn.OpenAsync();
        var db = ValidateDatabase(Database);
        await using var cmd = new SqlCommand(
            $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", cn)
            { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Erp.Api"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    /// <summary>Una instancia de la API en memoria (TestServer).</summary>
    public sealed class Factory(string contentRoot, IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(contentRoot);
            // UseSetting llega a WebApplication.CreateBuilder: AddErp lee varias opciones al registrar los servicios.
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
        }

        internal Task StartAsync()
        {
            _ = Server; // arranca el anfitrión (Bootstrap incluido) y propaga su error aquí
            return Task.CompletedTask;
        }

        public Client Anonymous(bool csrfHeader = true)
        {
            var http = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
            if (csrfHeader) http.DefaultRequestHeaders.Add("X-ERP-Request", "1");
            return new Client(http);
        }

        public async Task<Client> LoginAsync(string username, string password)
        {
            var api = Anonymous();
            var res = await api.PostAsync("/api/auth/login", new { username, password });
            Assert.True(res.Status == 200, $"login de {username}: {res.Status} {res.Text}");
            return api;
        }
    }
}
