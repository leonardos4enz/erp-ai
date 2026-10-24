using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.OData;

/// <summary>Spec 018: error de una consulta OData (400). Su mensaje es seguro y no distingue «oculto» de «no existe».</summary>
public sealed class ODataException(string message) : AppException(StatusCodes.Status400BadRequest, message);

public enum EdmKind { String, Int64, Int32, Decimal, Boolean, Date, DateTimeOffset }

/// <summary>Propiedad estructural publicada: su columna (sobre el alias <c>t</c>) sale del modelo, nunca de la petición.</summary>
public sealed record ODataProperty(string Name, string Column, EdmKind Kind, FieldDef? Field)
{
    /// <summary>Un texto largo (<c>nvarchar(max)</c>) no se ordena, como en la API de datos.</summary>
    public bool Sortable => Field?.Type != FieldTypes.LongText;
}

/// <summary>Navegación de un nivel: una referencia a un catálogo raíz que el rol lee, o las partidas del documento.</summary>
public sealed record ODataNavigation(string Name, FieldDef Field, EntityDef Target, bool Collection);

/// <summary>
/// Spec 018: cómo se ve el modelo por OData <b>para un usuario</b>: solo catálogos que puede leer y campos que ve (006), así
/// el documento de descubrimiento, los filtros, el orden y la expansión usan la misma lista blanca.
/// </summary>
public static class ODataModel
{
    public const string Namespace = "Erp";

    public static bool IsEntitySet(EntityDef e, UserContext u) => !e.IsArchived && !e.IsChild && u.Access(e.Name).Read;

    /// <summary>Nombre de la propiedad con el id de una referencia (un identificador del modelo no empieza con «_»).</summary>
    public static string ValueName(FieldDef f) => $"_{f.Name}_value";

    public static EdmKind? KindOf(FieldDef f) => (f.Type == FieldTypes.Formula ? f.ResultType : f.Type) switch
    {
        FieldTypes.Text or FieldTypes.LongText or FieldTypes.Choice or FieldTypes.State or FieldTypes.Autonumber => EdmKind.String,
        FieldTypes.Integer or FieldTypes.Reference => EdmKind.Int64,
        FieldTypes.User => EdmKind.Int32,
        FieldTypes.Decimal => EdmKind.Decimal,
        FieldTypes.Boolean => EdmKind.Boolean,
        FieldTypes.Date => EdmKind.Date,
        FieldTypes.DateTime => EdmKind.DateTimeOffset,
        _ => null,
    };

    public static string EdmName(EdmKind k) => "Edm." + k;

    /// <summary>Propiedades en orden: <c>Id</c>, los campos visibles y <c>CreatedAt</c>/<c>UpdatedAt</c>.</summary>
    public static IReadOnlyList<ODataProperty> Properties(EntityDef e, UserContext u)
    {
        var list = new List<ODataProperty> { new("Id", "t.[Id]", EdmKind.Int64, null) };
        foreach (var f in e.StoredFields)
        {
            if (f.Type == FieldTypes.Parent || !FieldAccess.CanSee(e, f, u) || KindOf(f) is not { } kind) continue;
            list.Add(new(f.Type == FieldTypes.Reference ? ValueName(f) : f.Name, $"t.{Quote(f.Name)}", kind, f));
        }
        list.Add(new("CreatedAt", "t.[CreatedAt]", EdmKind.DateTimeOffset, null));
        list.Add(new("UpdatedAt", "t.[UpdatedAt]", EdmKind.DateTimeOffset, null));
        return list;
    }

    /// <summary>Navegaciones de un catálogo raíz (las partidas no las tienen: la expansión es de un nivel).</summary>
    public static IReadOnlyList<ODataNavigation> Navigations(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext u)
    {
        var list = new List<ODataNavigation>();
        if (e.IsChild) return list;
        foreach (var f in e.ActiveFields)
        {
            if (!FieldAccess.CanSee(e, f, u)) continue;
            if (f.Type == FieldTypes.Reference && model.TryGetValue(f.ReferenceEntity ?? "", out var target) && IsEntitySet(target, u))
                list.Add(new(f.Name, f, target, Collection: false));
            else if (f.Type == FieldTypes.Lines && model.TryGetValue(f.ChildEntity ?? "", out var child) && !child.IsArchived && child.ParentField is not null)
                list.Add(new(f.Name, f, child, Collection: true));
        }
        return list;
    }
}
