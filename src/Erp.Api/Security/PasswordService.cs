using Microsoft.AspNetCore.Identity;

namespace Erp.Api.Security;

/// <summary>PBKDF2 (HMAC-SHA512, 100k iteraciones, sal por usuario) vía el PasswordHasher de ASP.NET Core Identity.</summary>
public sealed class PasswordService
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _hasher = new();

    // Hash fijo para comparar cuando el usuario no existe: iguala el tiempo de respuesta y evita enumerar usuarios.
    private readonly Lazy<string> _dummyHash;

    public PasswordService() => _dummyHash = new(() => _hasher.HashPassword(Subject, Guid.NewGuid().ToString()));

    public string Hash(string password) => _hasher.HashPassword(Subject, password);

    public bool Verify(string? hash, string password, out bool needsRehash)
    {
        var result = _hasher.VerifyHashedPassword(Subject, hash ?? _dummyHash.Value, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return hash is not null && result != PasswordVerificationResult.Failed;
    }

    /// <summary>Política alineada con NIST 800-63B: longitud sobre complejidad arbitraria.</summary>
    public static bool MeetsPolicy(string? password, out string error, string? username = null)
    {
        error = "";
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            error = $"La contraseña debe tener al menos {MinLength} caracteres.";
        else if (password.Length > MaxLength)
            error = $"La contraseña no puede exceder {MaxLength} caracteres.";
        else if (password.Distinct().Count() < 4)
            error = "La contraseña es demasiado repetitiva.";
        else if (username is not null && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            error = "La contraseña no puede contener el nombre de usuario.";
        return error.Length == 0;
    }
}
