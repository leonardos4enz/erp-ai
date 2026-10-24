using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Erp.Api.Metadata;

namespace Erp.Api.Environments;

/// <summary>Spec 009: configuración del entorno. Vive fuera del repositorio y no se cambia desde la aplicación.</summary>
public sealed record EnvironmentOptions(string Name, bool RequireApproval, string? SigningKey, bool AllowDirectChanges = false)
{
    public const int MinKeyLength = 32;

    public bool Signing => SigningKey is not null;

    /// <summary>
    /// Spec 012: ¿se puede cambiar el modelo sin paquete (modelador, módulos, asistente)? En un entorno con aprobación, no:
    /// el único camino es un paquete aprobado por otro administrador. <c>AllowDirectChanges</c> lo permite de forma
    /// explícita (suites de prueba que se comportan como producción pero preparan datos con planes).
    /// </summary>
    public bool DirectChanges => !RequireApproval || AllowDirectChanges;

    /// <summary>Spec 012: 409 si el entorno exige aprobación (H4). Se llama antes de tocar la base.</summary>
    public void RequireDirectChange(string what)
    {
        if (!DirectChanges)
            throw new Erp.Api.Infrastructure.Conflict(
                $"Este entorno exige la aprobación de otro administrador: {what} no se aplica directamente. Los cambios al modelo llegan como paquete desde Configuración › Entornos.");
    }

    public static EnvironmentOptions From(IConfiguration config) =>
        Create(config["Environments:Name"], config.GetValue("Environments:RequireApproval", false), config["Environments:SigningKey"])
            with { AllowDirectChanges = config.GetValue("Environments:AllowDirectChanges", false) };

    /// <summary>Una clave corta o un nombre largo detienen el arranque: mejor no arrancar que firmar con una clave débil.</summary>
    public static EnvironmentOptions Create(string? name, bool requireApproval, string? signingKey)
    {
        name = name?.Trim() is { Length: > 0 } n ? n : "Desarrollo";
        if (name.Length > 100) throw new InvalidOperationException("Environments:Name admite máximo 100 caracteres.");
        var key = signingKey is { Length: > 0 } k ? k : null;
        if (key is { Length: < MinKeyLength })
            throw new InvalidOperationException($"Environments:SigningKey debe tener al menos {MinKeyLength} caracteres.");
        return new(name, requireApproval, key);
    }
}

/// <summary>Opciones JSON de los paquetes: lectura tolerante, forma canónica (firma y huella) y archivo legible.</summary>
public static class PackageJson
{
    public static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true, // "op" puede venir en cualquier posición
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>Compacta y estable: la firma no depende de la sangría ni del orden de las propiedades del archivo.</summary>
    public static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    /// <summary>Para el archivo descargado: con sangría, acentos legibles y sin propiedades de solo salida.</summary>
    public static readonly JsonSerializerOptions File = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    if (ti.Type != typeof(FieldDef)) return;
                    foreach (var p in ti.Properties.Where(p => p.Name is "readOnly" or "clientLocked").ToList()) ti.Properties.Remove(p);
                },
            },
        },
    };
}

/// <summary>Contenido canónico, huella SHA-256 y firma HMAC-SHA256 de un paquete.</summary>
public static class PackageSigner
{
    public const string Prefix = "hmac-sha256:";

    public static byte[] Canonical(ModulePackage p) => JsonSerializer.SerializeToUtf8Bytes(p with { Signature = null }, PackageJson.Canonical);

    public static string Hash(byte[] canonical) => Convert.ToHexStringLower(SHA256.HashData(canonical));

    public static string Sign(byte[] canonical, string key) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), canonical));

    /// <summary>Comparación en tiempo constante; una firma ausente o con otro formato no es válida.</summary>
    public static bool Verify(byte[] canonical, string? signature, string key)
    {
        if (signature is null || !signature.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        byte[] given;
        try { given = Convert.FromHexString(signature[Prefix.Length..]); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), canonical);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}

/// <summary>Spec 009 · H5: reglas que un paquete debe cumplir antes de guardarse (además del compilador).</summary>
public static partial class PackageRules
{
    [GeneratedRegex("^[a-z][a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();

    public static bool IsVersion(string? v) => v is not null && VersionPattern().IsMatch(v);

    /// <summary>Mayor que (semver numérico). Una versión inválida nunca es mayor.</summary>
    public static bool IsNewer(string version, string? installed) =>
        IsVersion(version) && (installed is null || !IsVersion(installed) || Version.Parse(version) > Version.Parse(installed));

    /// <summary>Siguiente versión menor (1.4.2 → 1.5.0).</summary>
    public static string NextMinor(string? last) =>
        IsVersion(last) && Version.Parse(last!) is var v ? $"{v.Major}.{v.Minor + 1}.0" : "1.0.0";

    /// <summary>Forma del paquete: identificador, nombre, versión e icono (sirve también al exportar).</summary>
    public static IEnumerable<string> ValidateHeader(string? id, string? name, string? version, string? icon, string? description,
        IReadOnlySet<string> serverModules)
    {
        if (id is null || !IdPattern().IsMatch(id)) yield return "El identificador usa de 2 a 41 caracteres: minúsculas, números y '-' (empieza con letra).";
        else if (serverModules.Contains(id)) yield return $"'{id}' es un módulo del servidor: sus versiones se instalan desde Módulos. Usa otro identificador (p. ej. {id}-ajustes).";
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > PlanCompiler.MaxLabel) yield return $"El nombre es obligatorio (máx. {PlanCompiler.MaxLabel}).";
        if (!IsVersion(version)) yield return "La versión debe tener la forma x.y.z (p. ej. 1.2.0).";
        if (icon is not null && (icon.Length > 40 || !IconPattern().IsMatch(icon))) yield return $"Icono '{icon}' no válido (nombre de Lucide en kebab-case).";
        if (description?.Length > PlanCompiler.MaxDescription) yield return "La descripción es demasiado larga.";
    }

    /// <summary>Todo lo que un paquete subido debe cumplir, salvo firma y versión instalada (las revisa el servicio).</summary>
    public static IReadOnlyList<string> Validate(ModulePackage p, IReadOnlySet<string> serverModules, IReadOnlySet<string> systemRoles)
    {
        var errors = new List<string>();
        if (p.Plan is null || p.Roles is null) { errors.Add("El archivo no tiene la forma de un paquete (plan y roles)."); return errors; }
        errors.AddRange(ValidateHeader(p.Id, p.Name, p.Version, p.Icon, p.Description, serverModules));
        if (p.Source?.Environment is { Length: > 100 }) errors.Add("El nombre del entorno de origen es demasiado largo.");

        var chain = p.FullChain;
        if (chain.Any(c => c.Plan?.Operations is null)) { errors.Add("Cada plan del paquete necesita operaciones."); return errors; }
        if (chain.Any(c => c.Plan.Operations.Count > PlanCompiler.MaxOperations)) errors.Add($"Un plan del paquete excede {PlanCompiler.MaxOperations} operaciones.");
        var (entities, dashboards) = ModelDiff.Scope(p);
        if (entities.Count == 0 && dashboards.Count == 0) errors.Add("El paquete no crea ningún catálogo ni tablero.");
        if (p.Seed is { Count: > 0 }) errors.Add("Un paquete de entorno no lleva datos (seed): solo metadatos.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in p.AllRoles)
        {
            var name = r.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) { errors.Add("Cada rol necesita un nombre (máx. 100)."); continue; }
            if (!names.Add(name)) errors.Add($"El rol '{name}' está repetido.");
            if (systemRoles.Contains(name)) errors.Add($"El paquete no puede declarar el rol del sistema '{name}'.");
            if (r.CanManageModel || r.CanManageUsers)
                errors.Add($"El rol '{name}' administra el modelo o los usuarios: esas capacidades no viajan en un paquete.");
            if (r.Description?.Length > 300) errors.Add($"La descripción del rol '{name}' es demasiado larga.");
            foreach (var perm in r.Permissions ?? [])
                if (!entities.Contains(perm.Entity ?? ""))
                    errors.Add($"El rol '{name}' da permisos sobre '{perm.Entity}', que no va en el paquete.");
        }
        return errors;
    }
}
