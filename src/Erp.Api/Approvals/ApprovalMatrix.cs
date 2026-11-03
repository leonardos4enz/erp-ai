using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Api.Expressions;
using Erp.Api.Metadata;

namespace Erp.Api.Approvals;

/// <summary>Spec 053: un perfil de la matriz (sus personas se leen aparte, al momento).</summary>
public sealed record ApprovalProfileDef(int Id, string Name, decimal? MinAmount, decimal? MaxAmount, bool IsActive);

/// <summary>
/// Spec 053: una regla de la matriz. <c>Conditions</c>: por campo de condición, los valores admitidos en forma canónica
/// (<see cref="ApprovalMatrix.Canon"/>); vacío o ausente = cualquiera. <c>Levels</c>: ids de perfil en orden.
/// </summary>
public sealed record ApprovalRuleDef(int Id, string Name, int Priority, DateTime? ValidFrom, DateTime? ValidTo, decimal? MinAmount,
    decimal? MaxAmount, IReadOnlyDictionary<string, IReadOnlyList<string>> Conditions, IReadOnlyList<int> Levels)
{
    /// <summary>Cuántas cosas fija: gana la más específica cuando empatan en prioridad.</summary>
    public int Specificity => Conditions.Count(c => c.Value.Count > 0) + (MinAmount is null ? 0 : 1) + (MaxAmount is null ? 0 : 1);
}

/// <summary>Un nivel de la regla congelada al empezar. <c>Skipped</c>: «range» si el monto no cae en el rango del perfil.</summary>
public sealed record FrozenLevel(int Level, int ProfileId, string Profile, decimal? MinAmount, decimal? MaxAmount, string? Skipped);

public sealed record FrozenRule(int RuleId, string Name, IReadOnlyList<FrozenLevel> Levels);

/// <summary>
/// Spec 053: la parte pura de la matriz (sin base): qué regla aplica, qué niveles pide y la huella de lo que la eligió. La usa el
/// motor (<c>RecordService.Approval</c>) y se prueba sola.
/// </summary>
public static class ApprovalMatrix
{
    public const int MaxProfiles = 200;
    public const int MaxRules = 200;
    public const int MaxMembers = 100;
    public const int MaxLevels = 10;
    public const int MaxValues = 50;
    public const int MaxName = 100;
    public const int MaxComment = 1000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Valor de un campo de condición como texto comparable (ids sin decimales, sí/no en minúsculas, opción tal cual).</summary>
    public static string? Canon(object? value) => ExprEvaluator.Normalize(value) switch
    {
        null => null,
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.############", CultureInfo.InvariantCulture),
        string s when s.Length == 0 => null,
        string s => s,
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };

    /// <summary>¿El monto cae en [min, max]? Sin rango, siempre; con rango y sin monto, no (desconocido no entra).</summary>
    public static bool InRange(decimal? amount, decimal? min, decimal? max) =>
        (min is null && max is null) || amount is { } a && (min is null || a >= min) && (max is null || a <= max);

    /// <summary>
    /// La regla que aplica: vigente <paramref name="today"/> (inclusiva), con el monto en su rango y cada condición cumplida. Gana
    /// la de menor número de prioridad, luego la más específica, luego la más antigua. Null si ninguna.
    /// </summary>
    public static ApprovalRuleDef? Pick(IEnumerable<ApprovalRuleDef> rules, IReadOnlyDictionary<string, string?> values, decimal? amount, DateTime today) =>
        rules.Where(r => (r.ValidFrom is null || r.ValidFrom.Value.Date <= today.Date) && (r.ValidTo is null || r.ValidTo.Value.Date >= today.Date)
                         && InRange(amount, r.MinAmount, r.MaxAmount)
                         && r.Conditions.All(c => c.Value.Count == 0
                             || values.GetValueOrDefault(c.Key) is { } v && c.Value.Contains(v, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(r => r.Priority).ThenByDescending(r => r.Specificity).ThenBy(r => r.Id)
            .FirstOrDefault();

    /// <summary>Los niveles de la regla tal como quedan al empezar: el perfil, su rango y si se salta por el monto.</summary>
    public static FrozenRule Freeze(ApprovalRuleDef rule, IReadOnlyDictionary<int, ApprovalProfileDef> profiles, decimal? amount, bool hasAmount) =>
        new(rule.Id, rule.Name, rule.Levels.Select((pid, i) =>
        {
            var p = profiles.GetValueOrDefault(pid);
            // Revisión adversarial 053 (F-10): sin monto no se sabe si cae en el rango: el nivel no se salta (falla cerrado).
            var skipped = hasAmount && amount is not null && p is not null && !InRange(amount, p.MinAmount, p.MaxAmount) ? "range" : null;
            return new FrozenLevel(i + 1, pid, p?.Name ?? $"#{pid}", p?.MinAmount, p?.MaxAmount, skipped);
        }).ToList());

    /// <summary>Los valores de condición del documento (canónicos) en el orden de la declaración.</summary>
    public static Dictionary<string, string?> ConditionValues(WorkflowApproval a, IReadOnlyDictionary<string, object?> header) =>
        (a.Conditions ?? []).ToDictionary(c => c, c => Canon(header.GetValueOrDefault(c)), StringComparer.OrdinalIgnoreCase);

    public static decimal? AmountOf(WorkflowApproval a, IReadOnlyDictionary<string, object?> header) =>
        a.Amount is null ? null : ExprEvaluator.Normalize(header.GetValueOrDefault(a.Amount)) as decimal?;

    /// <summary>Huella de lo que eligió la regla: si cambia mientras espera, la autorización se reinicia.</summary>
    public static byte[] ConditionsHash(WorkflowApproval a, IReadOnlyDictionary<string, object?> header)
    {
        var parts = new List<string?> { AmountOf(a, header)?.ToString("0.############", CultureInfo.InvariantCulture) };
        parts.AddRange(ConditionValues(a, header).Values);
        return SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts, Json)));
    }
}
