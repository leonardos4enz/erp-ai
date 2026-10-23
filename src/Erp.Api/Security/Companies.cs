using System.Text.RegularExpressions;

namespace Erp.Api.Security;

/// <summary>Spec 010: empresa del entorno tal como la ve una petición (dato del entorno, no metadato).</summary>
public sealed record CompanyInfo(int Id, string Code, string Name, string BaseCurrency);

public static partial class Companies
{
    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>Código de moneda ISO 4217 (tres letras mayúsculas).</summary>
    public static bool IsCurrency(string? code) => code is not null && CurrencyPattern().IsMatch(code);

    /// <summary>Clave de empresa: 2 a 20 letras mayúsculas, números, '_' o '-'.</summary>
    public static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code);
}
