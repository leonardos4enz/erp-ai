using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Api.Metadata;

namespace Erp.Api.Assistant;

/// <summary>
/// Spec 011: lo único que el LLM sabe del entorno. Se arma solo con metadatos ya cargados (modelo, tableros y nombres de
/// roles): este tipo no recibe conexiones ni datos, así no puede llevar registros, usuarios, empresas, tipos de cambio,
/// claves ni auditoría. Los valores de los filtros de vistas se omiten porque pueden citar un registro concreto.
/// </summary>
public static class AssistantContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record Result(string Json, string Hash, int Summarized);

    public static Result Build(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> dashboards,
        IEnumerable<string> roles, int maxChars)
    {
        var entities = model.Values.Where(e => !e.IsArchived).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(Full).ToList();
        var boards = dashboards.Values.Where(d => !d.IsArchived).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => d with { IsArchived = false }).ToList();
        var roleNames = roles.Order(StringComparer.OrdinalIgnoreCase).ToList();

        // < y > solo pueden aparecer dentro de cadenas: se escapan para que un texto de los metadatos no cierre el
        // bloque <modelo> en el que viaja el contexto (inyección indirecta).
        string Serialize() => JsonSerializer.Serialize(new { entities, dashboards = boards, roles = roleNames }, Json)
            .Replace("<", "\\u003c").Replace(">", "\\u003e");
        var json = Serialize();
        var summarized = 0;
        if (json.Length > maxChars)
        {
            // Los catálogos más grandes se resumen primero hasta que el contexto cabe en el presupuesto.
            var bySize = entities.Select((e, i) => (i, JsonSerializer.Serialize(e, Json).Length)).OrderByDescending(x => x.Length).ToList();
            foreach (var (i, _) in bySize)
            {
                entities[i] = Summary(model[entities[i].Name]);
                summarized++;
                json = Serialize();
                if (json.Length <= maxChars) break;
            }
        }
        return new Result(json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), summarized);
    }

    private static Entity Full(EntityDef e) => new()
    {
        Name = e.Name, Label = e.Label, LabelPlural = e.LabelPlural, Icon = e.Icon, Description = e.Description, Group = e.Group,
        DisplayField = e.DisplayField, PerCompany = e.PerCompany ? true : null, ParentEntity = e.ParentEntity,
        Fields = e.ActiveFields.Select(ToField).ToList(),
        Workflow = e.Workflow, Rules = e.Rules, Automations = e.Automations, Security = e.Security, Layout = e.Layout,
        Views = e.Views?.Select(v => v with { Filters = v.Filters?.Select(StripValue).ToList() }).ToList(),
        Reports = e.Reports, Webhooks = e.Webhooks,
    };

    private static Entity Summary(EntityDef e) => new()
    {
        Name = e.Name, Label = e.Label, LabelPlural = e.LabelPlural, Group = e.Group, PerCompany = e.PerCompany ? true : null,
        ParentEntity = e.ParentEntity, Summarized = true,
        FieldSummary = e.ActiveFields.Select(f => f.ReferenceEntity is null ? $"{f.Name}:{f.Type}" : $"{f.Name}:{f.Type}>{f.ReferenceEntity}").ToList(),
    };

    /// <summary>Solo se conservan los marcadores del lenguaje de vistas (<c>$me</c>, <c>$today</c>), que no son datos.</summary>
    private static ViewFilter StripValue(ViewFilter f) => f with
    {
        Label = null,
        Value = f.Value is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s && s.StartsWith('$') ? v : null,
    };

    private static Field ToField(FieldDef f) => new()
    {
        Name = f.Name, Label = f.Label, Type = f.Type,
        Required = f.Required ? true : null, Unique = f.Unique ? true : null,
        MaxLength = f.MaxLength, Precision = f.Precision, Scale = f.Scale, Options = f.Options,
        ReferenceEntity = f.ReferenceEntity, ChildEntity = f.ChildEntity, DefaultValue = f.DefaultValue, Formula = f.Formula,
        ResultType = f.ResultType, Prefix = f.Prefix, Padding = f.Padding,
        Editable = f.Editable ? null : false, ShowInList = f.ShowInList ? null : false,
        RequiredForRoles = f.RequiredForRoles, LookupFilter = f.LookupFilter,
    };

    private sealed record Entity
    {
        public required string Name { get; init; }
        public required string Label { get; init; }
        public required string LabelPlural { get; init; }
        public string? Icon { get; init; }
        public string? Description { get; init; }
        public string? Group { get; init; }
        public string? DisplayField { get; init; }
        public bool? PerCompany { get; init; }
        public string? ParentEntity { get; init; }
        public bool? Summarized { get; init; }
        public IReadOnlyList<Field>? Fields { get; init; }
        public IReadOnlyList<string>? FieldSummary { get; init; }
        public Workflow? Workflow { get; init; }
        public IReadOnlyList<BusinessRule>? Rules { get; init; }
        public IReadOnlyList<Automation>? Automations { get; init; }
        public SecurityPolicy? Security { get; init; }
        public Layout? Layout { get; init; }
        public IReadOnlyList<ViewDef>? Views { get; init; }
        public IReadOnlyList<ReportDef>? Reports { get; init; }
        public IReadOnlyList<WebhookDef>? Webhooks { get; init; }
    }

    private sealed record Field
    {
        public required string Name { get; init; }
        public required string Label { get; init; }
        public required string Type { get; init; }
        public bool? Required { get; init; }
        public bool? Unique { get; init; }
        public int? MaxLength { get; init; }
        public int? Precision { get; init; }
        public int? Scale { get; init; }
        public IReadOnlyList<string>? Options { get; init; }
        public string? ReferenceEntity { get; init; }
        public string? ChildEntity { get; init; }
        public string? DefaultValue { get; init; }
        public string? Formula { get; init; }
        public string? ResultType { get; init; }
        public string? Prefix { get; init; }
        public int? Padding { get; init; }
        public bool? Editable { get; init; }
        public bool? ShowInList { get; init; }
        public IReadOnlyList<string>? RequiredForRoles { get; init; }
        public LookupFilter? LookupFilter { get; init; }
    }
}
