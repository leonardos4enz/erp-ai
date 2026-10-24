using System.Security.Cryptography;
using System.Text;
using Erp.Api.Metadata;

namespace Erp.Api.Assistant;

/// <summary>
/// Spec 012 (H5 CA2): huella de lo que la persona revisó antes de aplicar una propuesta: el plan, los pasos con su SQL y
/// las advertencias, compilados contra el modelo de ese momento. Si el modelo cambia y cualquiera de ellos cambia, la
/// huella también, y aplicar se rechaza hasta que se revise de nuevo.
/// </summary>
public static class ReviewHash
{
    public static string Of(string planJson, PlanPreview preview, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        // Separadores de control: un texto no puede imitar el límite entre dos partes.
        sb.Append(planJson).Append('\u001e');
        foreach (var s in preview.Steps) sb.Append(s.Operation).Append('\u001f').Append(s.Description).Append('\u001f').Append(s.Sql).Append('\u001e');
        sb.Append('\u001d');
        foreach (var w in warnings) sb.Append(w).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
