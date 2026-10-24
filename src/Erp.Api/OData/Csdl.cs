using System.Text;
using System.Xml;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.OData;

/// <summary>
/// Spec 018 (H3): documento de descubrimiento CSDL 4.0 generado del modelo <b>para quien lo pide</b>: catálogos que su rol
/// lee, campos que ve (006) y navegaciones a catálogos que también lee. Un documento global revelaría campos ocultos.
/// </summary>
public static class Csdl
{
    private const string EdmxNs = "http://docs.oasis-open.org/odata/ns/edmx";
    private const string EdmNs = "http://docs.oasis-open.org/odata/ns/edm";

    public static IReadOnlyList<EntityDef> EntitySets(IReadOnlyDictionary<string, EntityDef> model, UserContext user) =>
        model.Values.Where(e => ODataModel.IsEntitySet(e, user)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

    public static string Write(IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        var sets = EntitySets(model, user);
        // Tipos: los conjuntos y las partidas que se pueden expandir desde ellos.
        var types = new List<EntityDef>(sets);
        foreach (var e in sets)
            foreach (var n in ODataModel.Navigations(e, model, user).Where(n => n.Collection))
                if (!types.Contains(n.Target)) types.Add(n.Target);

        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("edmx", "Edmx", EdmxNs);
            w.WriteAttributeString("Version", "4.0");
            w.WriteStartElement("edmx", "Reference", EdmxNs);
            w.WriteAttributeString("Uri", "https://oasis-tcs.github.io/odata-vocabularies/vocabularies/Org.OData.Core.V1.xml");
            w.WriteStartElement("edmx", "Include", EdmxNs);
            w.WriteAttributeString("Namespace", "Org.OData.Core.V1");
            w.WriteAttributeString("Alias", "Core");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("edmx", "DataServices", EdmxNs);
            w.WriteStartElement("Schema", EdmNs);
            w.WriteAttributeString("Namespace", ODataModel.Namespace);

            foreach (var e in types)
            {
                w.WriteStartElement("EntityType", EdmNs);
                w.WriteAttributeString("Name", e.Name);
                w.WriteStartElement("Key", EdmNs);
                w.WriteStartElement("PropertyRef", EdmNs);
                w.WriteAttributeString("Name", "Id");
                w.WriteEndElement();
                w.WriteEndElement();
                foreach (var p in ODataModel.Properties(e, user))
                {
                    w.WriteStartElement("Property", EdmNs);
                    w.WriteAttributeString("Name", p.Name);
                    w.WriteAttributeString("Type", ODataModel.EdmName(p.Kind));
                    // Solo Id y CreatedAt nunca son nulos: un campo obligatorio agregado después puede estar vacío en registros viejos.
                    if (p.Name is "Id" or "CreatedAt") w.WriteAttributeString("Nullable", "false");
                    if (p.Field is { Type: FieldTypes.Text } t) w.WriteAttributeString("MaxLength", (t.MaxLength ?? FieldTypes.DefaultTextLength).ToString());
                    if (p.Field is { Type: FieldTypes.Decimal } d)
                    {
                        w.WriteAttributeString("Precision", (d.Precision ?? 18).ToString());
                        w.WriteAttributeString("Scale", (d.Scale ?? 2).ToString());
                    }
                    if (p.Field is { } f) Description(w, f.Type == FieldTypes.Reference ? $"{f.Label} (id)" : f.Label);
                    w.WriteEndElement();
                }
                foreach (var n in ODataModel.Navigations(e, model, user))
                {
                    w.WriteStartElement("NavigationProperty", EdmNs);
                    w.WriteAttributeString("Name", n.Name);
                    w.WriteAttributeString("Type", n.Collection ? $"Collection({ODataModel.Namespace}.{n.Target.Name})" : $"{ODataModel.Namespace}.{n.Target.Name}");
                    if (n.Collection) w.WriteAttributeString("ContainsTarget", "true");
                    Description(w, n.Field.Label);
                    w.WriteEndElement();
                }
                Description(w, e.Label);
                w.WriteEndElement();
            }

            w.WriteStartElement("EntityContainer", EdmNs);
            w.WriteAttributeString("Name", "Default");
            foreach (var e in sets)
            {
                w.WriteStartElement("EntitySet", EdmNs);
                w.WriteAttributeString("Name", e.Name);
                w.WriteAttributeString("EntityType", $"{ODataModel.Namespace}.{e.Name}");
                foreach (var n in ODataModel.Navigations(e, model, user).Where(n => !n.Collection))
                {
                    w.WriteStartElement("NavigationPropertyBinding", EdmNs);
                    w.WriteAttributeString("Path", n.Name);
                    w.WriteAttributeString("Target", n.Target.Name);
                    w.WriteEndElement();
                }
                Description(w, e.LabelPlural);
                w.WriteEndElement();
            }
            w.WriteEndElement(); // EntityContainer
            w.WriteEndElement(); // Schema
            w.WriteEndElement(); // DataServices
            w.WriteEndElement(); // Edmx
        }
        // StringBuilder es UTF-16: la declaración debe decir lo que realmente se envía.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static void Description(XmlWriter w, string text)
    {
        w.WriteStartElement("Annotation", EdmNs);
        w.WriteAttributeString("Term", "Core.Description");
        w.WriteAttributeString("String", text);
        w.WriteEndElement();
    }
}
