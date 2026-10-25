using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erp.Api.Metadata;

public static class FieldTypes
{
    public const string Text = "text";
    public const string LongText = "longtext";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Choice = "choice";
    public const string Reference = "reference";
    // Spec 002
    public const string Autonumber = "autonumber";
    public const string Formula = "formula";
    public const string Lines = "lines";
    public const string Parent = "parent";
    public const string State = "state";
    public const string User = "user";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Text, LongText, Integer, Decimal, Boolean, Date, DateTime, Choice, Reference,
        Autonumber, Formula, Lines, Parent, User,
        // State no está aquí: solo lo crea setWorkflow.
    };

    public static readonly IReadOnlySet<string> Searchable = new HashSet<string> { Text, LongText, Choice, Autonumber };
    public static readonly IReadOnlySet<string> Uniqueable = new HashSet<string> { Text, Integer, Date, Choice };

    /// <summary>Tipos cuyo valor calcula el motor: no se aceptan del cliente.</summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string> { Autonumber, Formula, State, Parent };

    /// <summary>Tipos válidos como resultado de una fórmula.</summary>
    public static readonly IReadOnlySet<string> FormulaResults = new HashSet<string> { Decimal, Integer, Text, Boolean, Date };

    /// <summary>Tipos que no tienen columna en la tabla.</summary>
    public static bool IsVirtual(string type) => type == Lines;

    public const int DefaultTextLength = 200;
    public const int MaxTextLength = 4000;
    public const int MaxChoiceLength = 100;
}

public sealed record FieldDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public bool Unique { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public string? ReferenceEntity { get; init; }
    public bool ShowInList { get; init; } = true;
    public IReadOnlyList<string>? RequiredForRoles { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsArchived { get; init; }
    // Spec 002
    public string? DefaultValue { get; init; }
    public string? Formula { get; init; }
    public string? ResultType { get; init; }
    public string? Prefix { get; init; }
    public int? Padding { get; init; }
    public string? ChildEntity { get; init; }
    /// <summary>Spec 004: false = lo asigna el sistema (valor por defecto al crear o transiciones), nunca el usuario.</summary>
    public bool Editable { get; init; } = true;
    /// <summary>Spec 005: el buscador de esta referencia solo ofrece registros cuyo <c>Field</c> = valor de <c>Source</c>.</summary>
    public LookupFilter? LookupFilter { get; init; }
    /// <summary>Spec 006, solo salida: acceso del usuario actual por rol (<c>hidden</c>, <c>read</c> o <c>edit</c>).</summary>
    public string? Access { get; init; }
    /// <summary>Spec 006, solo salida: estados en que se captura (de la política del catálogo).</summary>
    public IReadOnlyList<string>? EditableIn { get; init; }
    /// <summary>Spec 015, solo salida: el campo dueño acepta equipos (<see cref="SecurityPolicy.TeamOwners"/>).</summary>
    public bool? AcceptsTeams { get; init; }

    public bool ReadOnly => FieldTypes.ReadOnly.Contains(Type);

    /// <summary>El cliente no puede escribirlo: calculado por el motor o asignado por el sistema.</summary>
    public bool ClientLocked => ReadOnly || !Editable;
}

/// <summary>Spec 005: <c>Field</c> es un campo del catálogo referenciado; <c>Source</c>, un campo del mismo registro o <c>CampoPadre.Campo</c>.</summary>
public sealed record LookupFilter(string Field, string Source);

public sealed record WorkflowState
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string Color { get; init; } = "neutral";
    public bool Editable { get; init; } = true;
}

public sealed record FieldAssignment(string Field, string Value);

public sealed record WorkflowTransition
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<string> From { get; init; } = [];
    public required string To { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
    public string? When { get; init; }
    public string? WhenMessage { get; init; }
    public bool RequireComment { get; init; }
    public IReadOnlyList<string>? RequiredFields { get; init; }
    public IReadOnlyList<FieldAssignment>? Set { get; init; }
    public string? Icon { get; init; }
    public string? Variant { get; init; }
    /// <summary>Spec 005: solo la ejecuta una automatización; no se ofrece ni se acepta de personas.</summary>
    public bool System { get; init; }
    /// <summary>Spec 023: qué hacer si al ejecutarla quedan avisos (<see cref="WarningPolicies"/>). Omitido = <c>confirm</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OnWarning { get; init; }
}

public sealed record Workflow
{
    public const string StateField = "Estado";
    public required string Initial { get; init; }
    public IReadOnlyList<WorkflowState> States { get; init; } = [];
    public IReadOnlyList<WorkflowTransition> Transitions { get; init; } = [];

    public WorkflowState? FindState(string? name) =>
        States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record BusinessRule
{
    public required string Name { get; init; }
    public required string Message { get; init; }
    public required string Assert { get; init; }
    public string? When { get; init; }
    public string? Field { get; init; }
    /// <summary>Spec 023: <c>error</c> (omitido) bloquea; <c>warning</c> avisa sin impedir guardar (<see cref="RuleSeverities"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; init; }

    [JsonIgnore]
    public bool IsWarning => Severity == RuleSeverities.Warning;
}

/// <summary>Spec 023: gravedad de una regla de negocio.</summary>
public static class RuleSeverities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Error, Warning };
}

/// <summary>Spec 023: qué hace una transición si al ejecutarla quedan avisos.</summary>
public static class WarningPolicies
{
    /// <summary>Una persona los confirma explícitamente (una automatización sigue).</summary>
    public const string Confirm = "confirm";
    /// <summary>No se ejecuta, como con un error.</summary>
    public const string Block = "block";
    /// <summary>Se ejecuta sin preguntar.</summary>
    public const string Allow = "allow";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Confirm, Block, Allow };
}

// ---------- Spec 005: automatizaciones entre registros ----------

public static class AutomationEvents
{
    public const string Create = "create";
    public const string Update = "update";
    public const string EnterState = "enterState";
    public const string Transition = "transition";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Create, Update, EnterState, Transition };
}

/// <summary>Spec 016: cuándo corren las acciones (en la transacción del evento o después, desde la bandeja).</summary>
public static class AutomationModes
{
    public const string Sync = "sync";
    public const string Async = "async";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Sync, Async };
}

public static class AutomationActions
{
    public const string Update = "update";
    public const string Transition = "transition";
    public const string Create = "create";
    public const string Rollup = "rollup";
    /// <summary>Spec 020: valúa el registro con capas de costo (promedio o FIFO).</summary>
    public const string Cost = "cost";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Update, Transition, Create, Rollup, Cost };
}

/// <summary>Spec 020: métodos de costeo que acepta la expresión <c>method</c> de una acción <c>cost</c>.</summary>
public static class CostMethods
{
    public const string Average = "average";
    public const string Fifo = "fifo";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Average, Fifo };
}

/// <summary>
/// Spec 022: políticas de valuación de una acción <c>cost</c>. Omitidas, el motor se comporta como en la 020; los valores
/// recomendados los declaran los módulos.
/// </summary>
public static class CostPolicies
{
    /// <summary>Entrada sin costo en una llave sin capas: a cero (020).</summary>
    public const string Average = "average";
    /// <summary>... al costo de respaldo; sin respaldo se rechaza.</summary>
    public const string Fallback = "fallback";
    /// <summary>... se rechaza.</summary>
    public const string Require = "require";
    public static readonly IReadOnlySet<string> UncostedEntry = new HashSet<string> { Average, Fallback, Require };

    /// <summary>Promedio con varias capas abiertas (p. ej. después de FIFO): fundirlas antes de valuar.</summary>
    public const string Merge = "merge";
    /// <summary>... conservarlas y consumirlas en orden hasta la siguiente entrada (020).</summary>
    public const string Keep = "keep";
    public static readonly IReadOnlySet<string> MethodChange = new HashSet<string> { Merge, Keep };
}

/// <summary>Spec 020: partida que una acción <c>create</c> agrega al documento nuevo.</summary>
public sealed record LineTemplate
{
    public required string Field { get; init; }
    public string? When { get; init; }
    public IReadOnlyList<FieldAssignment> Set { get; init; } = [];
}

public sealed record MatchPair(string Field, string Source);

public sealed record AutomationAction
{
    public required string Type { get; init; }
    /// <summary>Repite la acción por cada partida de este campo; el ámbito es la partida.</summary>
    public string? Lines { get; init; }
    public string? When { get; init; }
    /// <summary>Campo referencia del ámbito que apunta al registro destino.</summary>
    public string? Target { get; init; }
    public IReadOnlyList<FieldAssignment>? Set { get; init; }
    public string? Transition { get; init; }
    public string? Entity { get; init; }
    public IReadOnlyList<MatchPair>? Match { get; init; }
    public bool CreateMissing { get; init; }
    public string? Field { get; init; }
    public string? Sum { get; init; }
    public string? Filter { get; init; }
    // Spec 020: create con partidas (y Transition: la que se ejecuta sobre el registro nuevo).
    public IReadOnlyList<LineTemplate>? CreateLines { get; init; }
    // Spec 020: cost (Entity + Match = la llave y el saldo; Field = valor restante de las capas en el saldo).
    public string? Quantity { get; init; }
    public string? Method { get; init; }
    public string? UnitCost { get; init; }
    public string? FallbackCost { get; init; }
    public string? CostField { get; init; }
    public string? UnitCostField { get; init; }
    // Spec 022: políticas de cost (ver CostPolicies), costo total exacto de una entrada y cantidad costeada en el saldo.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UncostedEntry { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodChange { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TotalCost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuantityField { get; init; }
    /// <summary>Spec 022 (create): asigna al registro que disparó la acción valores calculados en el registro creado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FieldAssignment>? Capture { get; init; }
}

public sealed record Automation
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string On { get; init; }
    public string? State { get; init; }
    public string? Transition { get; init; }
    public string? When { get; init; }
    public IReadOnlyList<AutomationAction> Actions { get; init; } = [];
    /// <summary>Spec 016: <c>async</c> = las acciones corren después, desde la bandeja. Omitido = <c>sync</c> (005).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonIgnore]
    public bool IsAsync => Mode == AutomationModes.Async;
}

// ---------- Spec 016: tareas programadas ----------

public static class ScheduleFrequencies
{
    public const string Minutes = "minutes";
    public const string Hour = "hour";
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Minutes, Hour, Day, Week, Month };
}

/// <summary>
/// Qué hace la tarea y con qué permisos (<c>Role</c>: rol de servicio). Si corre o no es decisión de cada entorno
/// (<c>meta.ScheduleState</c>), como el destino de un webhook. Ver plan 016.
/// </summary>
public sealed record ScheduleDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required string Every { get; init; }
    public int? Interval { get; init; }
    public int? Minute { get; init; }
    public string? At { get; init; }
    public IReadOnlyList<int>? Weekdays { get; init; }
    public int? Day { get; init; }
    /// <summary>Consulta: qué registros procesa cada ocurrencia (traducible a SQL).</summary>
    public required string Where { get; init; }
    public required string Role { get; init; }
    public int? BatchSize { get; init; }
    public IReadOnlyList<AutomationAction> Actions { get; init; } = [];

    public const int DefaultBatchSize = 100;
    public const int MaxBatchSize = 1000;
    [JsonIgnore]
    public int EffectiveBatchSize => BatchSize ?? DefaultBatchSize;
}

// ---------- Spec 020: libro de partida doble ----------

/// <summary>Periodos del libro: el documento contabilizado debe caer en uno cuya condición <c>Open</c> se cumpla.</summary>
public sealed record LedgerPeriod
{
    public required string Entity { get; init; }
    public required string Start { get; init; }
    public required string End { get; init; }
    public required string Open { get; init; }
}

/// <summary>Ver plan 020 §1. <c>States</c> nulo = todos los estados están contabilizados.</summary>
public sealed record LedgerDef
{
    public required string Lines { get; init; }
    public required string Debit { get; init; }
    public required string Credit { get; init; }
    public string? Date { get; init; }
    public IReadOnlyList<string>? States { get; init; }
    public LedgerPeriod? Period { get; init; }

    public bool IsPosted(string? state) => States is null || States.Contains(state ?? "", StringComparer.OrdinalIgnoreCase);
}

// ---------- Spec 006: seguridad por campo y por registro ----------

public static class FieldAccessLevels
{
    public const string Hidden = "hidden";
    public const string Read = "read";
    public const string Edit = "edit";
}

public static class RecordScopes
{
    public const string All = "all";
    public const string Unit = "unit";
    public const string Own = "own";
    /// <summary>Spec 015: el dueño pertenece a mi unidad o a una que cuelga de ella (a cualquier profundidad).</summary>
    public const string UnitTree = "unitTree";
    public static readonly IReadOnlySet<string> Valid = new HashSet<string> { All, Unit, UnitTree, Own };
}

/// <summary><c>Read</c>/<c>Edit</c>: roles que ven / capturan (omitido = todos). <c>EditableIn</c>: estados en que se captura.</summary>
public sealed record FieldSecurity
{
    public required string Field { get; init; }
    public IReadOnlyList<string>? Read { get; init; }
    public IReadOnlyList<string>? Edit { get; init; }
    public IReadOnlyList<string>? EditableIn { get; init; }
}

/// <summary>Qué registros ve (o modifica, con <c>Access = write</c>) un conjunto de roles.</summary>
public sealed record RecordGrant
{
    public IReadOnlyList<string>? Roles { get; init; }
    public string? Scope { get; init; }
    public string? Where { get; init; }
    public string? Access { get; init; }

    public string EffectiveScope => Scope ?? RecordScopes.All;
    public bool Writes => !string.Equals(Access, "read", StringComparison.OrdinalIgnoreCase);
}

public sealed record SecurityPolicy
{
    public IReadOnlyList<FieldSecurity>? Fields { get; init; }
    /// <summary>Campo tipo usuario que identifica al dueño; omitido = quien lo creó.</summary>
    public string? Owner { get; init; }
    public IReadOnlyList<RecordGrant>? Records { get; init; }
    /// <summary>Las personas solo consultan: altas, cambios y bajas solo por automatizaciones.</summary>
    public bool SystemManaged { get; init; }
    /// <summary>
    /// Spec 015: el campo dueño (<see cref="Owner"/>) acepta un equipo además de una persona. Se omite en falso para que la
    /// forma canónica de las políticas (paquetes de la 009) no cambie en catálogos que no lo usan.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TeamOwners { get; init; }

    /// <summary>Spec 015: ¿este campo es el dueño y admite equipos?</summary>
    public bool AcceptsTeams(string field) => TeamOwners && string.Equals(Owner, field, StringComparison.OrdinalIgnoreCase);

    public FieldSecurity? For(string field) =>
        Fields?.FirstOrDefault(f => string.Equals(f.Field, field, StringComparison.OrdinalIgnoreCase));
}

// ---------- Spec 007: metadatos de interfaz ----------

/// <summary>Campo dentro de una sección. En JSON se acepta el nombre solo (<c>"Proveedor"</c>) o el objeto.</summary>
[JsonConverter(typeof(LayoutItemConverter))]
public sealed record LayoutItem
{
    public required string Field { get; init; }
    public string? VisibleWhen { get; init; }
    /// <summary>Ocupa el ancho completo de la sección.</summary>
    public bool Wide { get; init; }
}

public sealed class LayoutItemConverter : JsonConverter<LayoutItem>
{
    public override LayoutItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new LayoutItem { Field = reader.GetString()! };
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Cada campo del layout es un nombre o un objeto { field }.");
        string? field = null, when = null;
        var wide = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name?.ToLowerInvariant())
            {
                case "field": field = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                case "visiblewhen": when = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                case "wide": wide = reader.TokenType == JsonTokenType.True; break;
                default: reader.Skip(); break;
            }
        }
        return new LayoutItem { Field = field ?? "", VisibleWhen = when, Wide = wide };
    }

    public override void Write(Utf8JsonWriter writer, LayoutItem value, JsonSerializerOptions options)
    {
        if (value.VisibleWhen is null && !value.Wide) { writer.WriteStringValue(value.Field); return; }
        writer.WriteStartObject();
        writer.WriteString("field", value.Field);
        if (value.VisibleWhen is not null) writer.WriteString("visibleWhen", value.VisibleWhen);
        if (value.Wide) writer.WriteBoolean("wide", true);
        writer.WriteEndObject();
    }
}

public sealed record LayoutSection
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    /// <summary>Etiqueta de la pestaña; las secciones con la misma etiqueta van juntas.</summary>
    public string? Tab { get; init; }
    public int? Columns { get; init; }
    public string? VisibleWhen { get; init; }
    public IReadOnlyList<LayoutItem> Fields { get; init; } = [];
}

public sealed record Layout
{
    public IReadOnlyList<LayoutSection> Sections { get; init; } = [];
}

public static class FilterOps
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>
        { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "in", "blank", "notBlank" };
    public static readonly IReadOnlySet<string> NoValue = new HashSet<string> { "blank", "notBlank" };
}

/// <summary>Condición sobre un campo del catálogo. <c>Value</c> admite <c>$me</c> y <c>$today</c>.</summary>
public sealed record ViewFilter
{
    public required string Field { get; init; }
    public required string Op { get; init; }
    public JsonElement? Value { get; init; }
    /// <summary>Solo presentación: etiqueta del valor (p. ej. el nombre del proveedor elegido).</summary>
    public string? Label { get; init; }
}

public sealed record ViewDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
    public IReadOnlyList<string>? Columns { get; init; }
    public IReadOnlyList<ViewFilter>? Filters { get; init; }
    public string? Sort { get; init; }
    public string? Dir { get; init; }
    public string? State { get; init; }
    public bool Default { get; init; }
}

public sealed record ReportGroup
{
    public required string Field { get; init; }
    public string? Period { get; init; }
    public string? Label { get; init; }
}

public sealed record ReportMeasure
{
    public required string Fn { get; init; }
    public string? Field { get; init; }
    public string? Label { get; init; }
}

public sealed record ReportDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
    public IReadOnlyList<ReportGroup>? GroupBy { get; init; }
    public IReadOnlyList<ReportMeasure> Measures { get; init; } = [];
    public string? Filter { get; init; }
    public string? OrderBy { get; init; }
    public string? Dir { get; init; }
    public int? Limit { get; init; }
    public string? Chart { get; init; }
}

public sealed record DashboardWidget
{
    public required string Type { get; init; }
    public string? Label { get; init; }
    public required string Entity { get; init; }
    public required string Report { get; init; }
    public int? Measure { get; init; }
    public bool Wide { get; init; }
}

public sealed record DashboardDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
    public IReadOnlyList<DashboardWidget> Widgets { get; init; } = [];
    public bool IsArchived { get; init; }
}

/// <summary>
/// Spec 024: módulo de navegación. Sus catálogos son los que tienen <c>Group</c> = <c>Name</c> (sin distinguir mayúsculas);
/// un grupo sin declaración es un módulo implícito y los catálogos sin grupo forman «General».
/// </summary>
public sealed record AppDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public int? Order { get; init; }
    public bool IsArchived { get; init; }
}

// ---------- Spec 008: integraciones ----------

/// <summary>Evento que dispara un webhook: los mismos de las automatizaciones (spec 005).</summary>
public sealed record WebhookEvent
{
    public required string On { get; init; }
    public string? State { get; init; }
    public string? Transition { get; init; }
}

/// <summary>
/// Qué se avisa y con qué permisos (<c>Role</c>: rol de servicio, spec 006). El destino y el secreto no son metadatos:
/// se configuran por entorno (ver plan 008).
/// </summary>
public sealed record WebhookDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<WebhookEvent> Events { get; init; } = [];
    public string? When { get; init; }
    public required string Role { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }
}

public sealed record EntityDef
{
    /// <summary>Spec 021 (H2): versión de la definición, solo en las respuestas al modelador (no se guarda).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string LabelPlural { get; init; }
    public string Icon { get; init; } = "database";
    public string? Description { get; init; }
    public string? DisplayField { get; init; }
    /// <summary>Sección del menú (p. ej. "Compras"). Nulo: sección general de catálogos.</summary>
    public string? Group { get; init; }
    public bool IsArchived { get; init; }
    public IReadOnlyList<FieldDef> Fields { get; init; } = [];
    public Workflow? Workflow { get; init; }
    public IReadOnlyList<BusinessRule>? Rules { get; init; }
    public IReadOnlyList<Automation>? Automations { get; init; }
    public SecurityPolicy? Security { get; init; }
    /// <summary>Spec 007: secciones y pestañas del formulario.</summary>
    public Layout? Layout { get; init; }
    /// <summary>Spec 007: vistas compartidas del listado (solo se envían a modeladores).</summary>
    public IReadOnlyList<ViewDef>? Views { get; init; }
    /// <summary>Spec 007: reportes (solo se envían a modeladores; los usuarios los consultan por /api/reports).</summary>
    public IReadOnlyList<ReportDef>? Reports { get; init; }
    /// <summary>Spec 008: webhooks salientes (solo se envían a modeladores; el destino se configura por entorno).</summary>
    public IReadOnlyList<WebhookDef>? Webhooks { get; init; }
    /// <summary>Spec 016: tareas programadas (solo se envían a modeladores; si corren se decide por entorno).</summary>
    public IReadOnlyList<ScheduleDef>? Schedules { get; init; }
    /// <summary>Spec 020: el documento es un libro de partida doble (cuadre y periodos los garantiza el motor).</summary>
    public LedgerDef? Ledger { get; init; }
    /// <summary>
    /// Spec 027: plantillas de impresión (solo se envían a modeladores; los usuarios eligen entre las que les aplican). Se
    /// omite en nulo para que la forma canónica de los catálogos sin plantillas (paquetes de la 009) no cambie.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PrintTemplate>? PrintTemplates { get; init; }
    /// <summary>Spec 010: cada registro pertenece a una empresa (columna de sistema <c>CompanyId</c>). Las partidas siguen a su documento.</summary>
    public bool PerCompany { get; init; }

    public bool SystemManaged => Security?.SystemManaged == true;

    public IEnumerable<FieldDef> ActiveFields => Fields.Where(f => !f.IsArchived).OrderBy(f => f.DisplayOrder);

    /// <summary>Campos con columna física (excluye partidas, que viven en la entidad hija).</summary>
    public IEnumerable<FieldDef> StoredFields => ActiveFields.Where(f => !FieldTypes.IsVirtual(f.Type));

    public FieldDef? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public FieldDef? ParentField => ActiveFields.FirstOrDefault(f => f.Type == FieldTypes.Parent);
    public bool IsChild => ParentField is not null;
    public string? ParentEntity => ParentField?.ReferenceEntity;

    /// <summary>Campo que representa al registro en referencias y búsquedas.</summary>
    public string? EffectiveDisplayField =>
        DisplayField ?? ActiveFields.FirstOrDefault(f => f.Type is FieldTypes.Autonumber or FieldTypes.Text or FieldTypes.Choice)?.Name;
}

// ---------- Plan de cambios: la única vía para modificar el modelo (constitución I.3) ----------

public sealed record ChangePlan
{
    public string Description { get; init; } = "";
    public IReadOnlyList<Operation> Operations { get; init; } = [];
    /// <summary>Spec 021 (H2 CA1): versión de cada catálogo con la que el editor lo abrió. No se guarda con el plan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? BaseVersions { get; init; }
    /// <summary>Spec 021 (H2 CA2): huella de la vista previa que se revisó (<see cref="PlanPreview.BaseHash"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseHash { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(CreateEntityOp), "createEntity")]
[JsonDerivedType(typeof(UpdateEntityOp), "updateEntity")]
[JsonDerivedType(typeof(ArchiveEntityOp), "archiveEntity")]
[JsonDerivedType(typeof(RestoreEntityOp), "restoreEntity")]
[JsonDerivedType(typeof(AddFieldOp), "addField")]
[JsonDerivedType(typeof(UpdateFieldOp), "updateField")]
[JsonDerivedType(typeof(ArchiveFieldOp), "archiveField")]
[JsonDerivedType(typeof(RestoreFieldOp), "restoreField")]
[JsonDerivedType(typeof(SetWorkflowOp), "setWorkflow")]
[JsonDerivedType(typeof(SetRulesOp), "setRules")]
[JsonDerivedType(typeof(SetAutomationsOp), "setAutomations")]
[JsonDerivedType(typeof(SetSecurityOp), "setSecurity")]
[JsonDerivedType(typeof(SetLayoutOp), "setLayout")]
[JsonDerivedType(typeof(SetViewsOp), "setViews")]
[JsonDerivedType(typeof(SetReportsOp), "setReports")]
[JsonDerivedType(typeof(SetDashboardOp), "setDashboard")]
[JsonDerivedType(typeof(ArchiveDashboardOp), "archiveDashboard")]
[JsonDerivedType(typeof(SetWebhooksOp), "setWebhooks")]
[JsonDerivedType(typeof(SetSchedulesOp), "setSchedules")]
[JsonDerivedType(typeof(SetLedgerOp), "setLedger")]
[JsonDerivedType(typeof(SetAppOp), "setApp")]
[JsonDerivedType(typeof(ArchiveAppOp), "archiveApp")]
[JsonDerivedType(typeof(SetPrintTemplatesOp), "setPrintTemplates")]
public abstract record Operation;

/// <summary>Spec 024. Crea o reemplaza la declaración de un módulo de navegación.</summary>
public sealed record SetAppOp : Operation
{
    public required AppDef App { get; init; }
}

/// <summary>Spec 024. La declaración se conserva archivada; sus catálogos quedan en un módulo implícito con su nombre.</summary>
public sealed record ArchiveAppOp : Operation
{
    public required string Name { get; init; }
}

/// <summary>Spec 020. Declara el libro del catálogo; <c>null</c> lo quita.</summary>
public sealed record SetLedgerOp : Operation
{
    public required string Entity { get; init; }
    public LedgerDef? Ledger { get; init; }
}

/// <summary>Spec 016. Reemplaza las tareas programadas del catálogo; spec 023: con <c>Merge</c> agrega o reemplaza por nombre.</summary>
public sealed record SetSchedulesOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<ScheduleDef> Schedules { get; init; } = [];
    /// <summary>Spec 023. Se omite en falso: la forma canónica de los paquetes ya firmados (009) no cambia.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Merge { get; init; }
}

/// <summary>Spec 008. Reemplaza los webhooks del catálogo; spec 023: con <c>Merge</c> agrega o reemplaza por nombre.</summary>
public sealed record SetWebhooksOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<WebhookDef> Webhooks { get; init; } = [];
    /// <summary>Spec 023. Se omite en falso: la forma canónica de los paquetes ya firmados (009) no cambia.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Merge { get; init; }
}

/// <summary>Spec 007. <c>null</c> elimina el layout (formulario derivado del orden de los campos).</summary>
public sealed record SetLayoutOp : Operation
{
    public required string Entity { get; init; }
    public Layout? Layout { get; init; }
}

/// <summary>Spec 007. Reemplaza las vistas compartidas del catálogo; spec 023: con <c>Merge</c> agrega o reemplaza por nombre.</summary>
public sealed record SetViewsOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<ViewDef> Views { get; init; } = [];
    /// <summary>Spec 023. Se omite en falso: la forma canónica de los paquetes ya firmados (009) no cambia.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Merge { get; init; }
}

/// <summary>Spec 007. Reemplaza los reportes del catálogo; spec 023: con <c>Merge</c> agrega o reemplaza por nombre.</summary>
public sealed record SetReportsOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<ReportDef> Reports { get; init; } = [];
    /// <summary>Spec 023. Se omite en falso: la forma canónica de los paquetes ya firmados (009) no cambia.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Merge { get; init; }
}

/// <summary>Spec 007. Crea o reemplaza un tablero.</summary>
public sealed record SetDashboardOp : Operation
{
    public required DashboardDef Dashboard { get; init; }
}

/// <summary>Spec 007. El tablero deja de ofrecerse; la definición se conserva.</summary>
public sealed record ArchiveDashboardOp : Operation
{
    public required string Name { get; init; }
}

/// <summary>Spec 006. Define la política completa del catálogo; <c>null</c> la elimina.</summary>
public sealed record SetSecurityOp : Operation
{
    public required string Entity { get; init; }
    public SecurityPolicy? Security { get; init; }
}

/// <summary>Spec 005. Sin <c>Merge</c> reemplaza la lista; con <c>Merge</c> agrega o reemplaza por nombre.</summary>
public sealed record SetAutomationsOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<Automation> Automations { get; init; } = [];
    public bool Merge { get; init; }
}

public sealed record SetWorkflowOp : Operation
{
    public required string Entity { get; init; }
    public required Workflow Workflow { get; init; }
}

public sealed record SetRulesOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<BusinessRule> Rules { get; init; } = [];
    /// <summary>Spec 022: agrega o reemplaza por nombre y conserva las demás (p. ej. la política de otro módulo).</summary>
    public bool Merge { get; init; }
}

public sealed record CreateEntityOp : Operation
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string LabelPlural { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public string? DisplayField { get; init; }
    public string? Group { get; init; }
    /// <summary>Spec 010: un registro por empresa.</summary>
    public bool PerCompany { get; init; }
    public IReadOnlyList<FieldDef> Fields { get; init; } = [];
}

public sealed record UpdateEntityOp : Operation
{
    public required string Entity { get; init; }
    public string? Label { get; init; }
    public string? LabelPlural { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public string? DisplayField { get; init; }
    /// <summary>Cadena vacía regresa el catálogo a la sección general.</summary>
    public string? Group { get; init; }
    /// <summary>Spec 010: solo <c>true</c> (pasar a un registro por empresa); volver a compartido se rechaza.</summary>
    public bool? PerCompany { get; init; }
}

public sealed record ArchiveEntityOp : Operation
{
    public required string Entity { get; init; }
}

public sealed record AddFieldOp : Operation
{
    public required string Entity { get; init; }
    public required FieldDef Field { get; init; }
}

public sealed record UpdateFieldOp : Operation
{
    public required string Entity { get; init; }
    public required string Field { get; init; }
    public string? Label { get; init; }
    public bool? Required { get; init; }
    public bool? ShowInList { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public IReadOnlyList<string>? RequiredForRoles { get; init; }
    public int? DisplayOrder { get; init; }
    /// <summary>Cadena vacía elimina el valor por defecto.</summary>
    public string? DefaultValue { get; init; }
    public string? Formula { get; init; }
    public bool? Editable { get; init; }
    /// <summary>Spec 005: un filtro con <c>Field</c> vacío lo elimina.</summary>
    public LookupFilter? LookupFilter { get; init; }
}

public sealed record ArchiveFieldOp : Operation
{
    public required string Entity { get; init; }
    public required string Field { get; init; }
}

/// <summary>Spec 017. Reactiva un catálogo archivado: su tabla y sus registros se conservaron.</summary>
public sealed record RestoreEntityOp : Operation
{
    public required string Entity { get; init; }
}

/// <summary>Spec 017. Reactiva un campo archivado: su columna y sus datos se conservaron.</summary>
public sealed record RestoreFieldOp : Operation
{
    public required string Entity { get; init; }
    public required string Field { get; init; }
}

public sealed record PlanError(int Operation, string Message);
public sealed record PlanStep(int Operation, string Description, string? Sql);
/// <param name="BaseHash">Spec 021 (H2 CA2): huella de lo que el plan toca tal como estaba al revisar; aplicar la exige.</param>
public sealed record PlanPreview(bool Valid, IReadOnlyList<PlanError> Errors, IReadOnlyList<PlanStep> Steps, string? BaseHash = null);
