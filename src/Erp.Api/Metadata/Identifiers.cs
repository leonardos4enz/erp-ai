using System.Text.RegularExpressions;

namespace Erp.Api.Metadata;

/// <summary>
/// Única puerta de entrada para nombres de tablas y columnas que terminan en SQL.
/// Constitución II.1: los identificadores solo provienen de metadatos validados aquí.
/// </summary>
public static partial class Identifiers
{
    public const int MaxLength = 50; // deja espacio para prefijos de constraints (máx. 128 en SQL Server)

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Columnas que el motor agrega a toda tabla dinámica.</summary>
    public static readonly IReadOnlySet<string> SystemColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "IsDeleted", "RowVer",
    };

    // Palabras reservadas de T-SQL más comunes; se rechazan aunque se citen, para evitar confusión.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD","ALL","ALTER","AND","ANY","AS","ASC","BACKUP","BEGIN","BETWEEN","BY","CASCADE","CASE",
        "CHECK","COLUMN","COMMIT","CONSTRAINT","CREATE","CROSS","CURRENT","CURSOR","DATABASE","DEFAULT",
        "DELETE","DENY","DESC","DISTINCT","DROP","ELSE","END","EXEC","EXECUTE","EXISTS","FOREIGN","FROM",
        "FULL","FUNCTION","GRANT","GROUP","HAVING","IDENTITY","IF","IN","INDEX","INNER","INSERT","INTO",
        "IS","JOIN","KEY","LEFT","LIKE","MERGE","NOT","NULL","OF","ON","OR","ORDER","OUTER","PRIMARY",
        "PROCEDURE","REFERENCES","REVOKE","RIGHT","ROLLBACK","SCHEMA","SELECT","SET","TABLE","THEN","TO",
        "TOP","TRAN","TRANSACTION","TRIGGER","TRUNCATE","UNION","UNIQUE","UPDATE","USER","VALUES","VIEW",
        "WHEN","WHERE","WITH","SYS","APP",
    };

    public static bool IsValid(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "El nombre es obligatorio.";
            return false;
        }
        if (!Pattern().IsMatch(name))
        {
            error = $"'{name}' no es válido: usa de 2 a {MaxLength} caracteres, empieza con letra y solo letras sin acento, números o '_'.";
            return false;
        }
        if (name.Contains("__", StringComparison.Ordinal))
        {
            error = $"'{name}' no puede contener '__' (reservado por el motor).";
            return false;
        }
        if (Reserved.Contains(name))
        {
            error = $"'{name}' es una palabra reservada.";
            return false;
        }
        error = "";
        return true;
    }

    public static bool IsValidFieldName(string? name, out string error)
    {
        if (!IsValid(name, out error)) return false;
        if (SystemColumns.Contains(name!))
        {
            error = $"'{name}' es una columna del sistema.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Cita un identificador ya validado. Lanza si no es válido: defensa en profundidad
    /// por si algún camino de código olvidara validar.
    /// </summary>
    public static string Quote(string name)
    {
        if (!Pattern().IsMatch(name))
            throw new InvalidOperationException("Identificador no válido rechazado al generar SQL.");
        return "[" + name + "]";
    }

    public static string AppTable(string entityName) => "[app]." + Quote(entityName);

    /// <summary>Nombre de constraint/índice: prefijo fijo del motor + identificadores validados.</summary>
    public static string Constraint(string prefix, string entity, string? field = null)
    {
        Quote(entity);
        if (field is not null) Quote(field);
        return "[" + prefix + "_" + entity + (field is null ? "" : "_" + field) + "]";
    }
}
