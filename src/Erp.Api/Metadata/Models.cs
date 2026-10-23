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

public static class AutomationActions
{
    public const string Update = "update";
    public const string Transition = "transition";
    public const string Create = "create";
    public const string Rollup = "rollup";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Update, Transition, Create, Rollup };
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
}

public sealed record EntityDef
{
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
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(CreateEntityOp), "createEntity")]
[JsonDerivedType(typeof(UpdateEntityOp), "updateEntity")]
[JsonDerivedType(typeof(ArchiveEntityOp), "archiveEntity")]
[JsonDerivedType(typeof(AddFieldOp), "addField")]
[JsonDerivedType(typeof(UpdateFieldOp), "updateField")]
[JsonDerivedType(typeof(ArchiveFieldOp), "archiveField")]
[JsonDerivedType(typeof(SetWorkflowOp), "setWorkflow")]
[JsonDerivedType(typeof(SetRulesOp), "setRules")]
[JsonDerivedType(typeof(SetAutomationsOp), "setAutomations")]
public abstract record Operation;

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

public sealed record PlanError(int Operation, string Message);
public sealed record PlanStep(int Operation, string Description, string? Sql);
public sealed record PlanPreview(bool Valid, IReadOnlyList<PlanError> Errors, IReadOnlyList<PlanStep> Steps);
