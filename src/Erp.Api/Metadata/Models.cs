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

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Text, LongText, Integer, Decimal, Boolean, Date, DateTime, Choice, Reference,
    };

    public static readonly IReadOnlySet<string> Searchable = new HashSet<string> { Text, LongText, Choice };
    public static readonly IReadOnlySet<string> Uniqueable = new HashSet<string> { Text, Integer, Date, Choice };

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
}

public sealed record EntityDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string LabelPlural { get; init; }
    public string Icon { get; init; } = "database";
    public string? Description { get; init; }
    public string? DisplayField { get; init; }
    public bool IsArchived { get; init; }
    public IReadOnlyList<FieldDef> Fields { get; init; } = [];

    public IEnumerable<FieldDef> ActiveFields => Fields.Where(f => !f.IsArchived).OrderBy(f => f.DisplayOrder);

    public FieldDef? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Campo que representa al registro en referencias y búsquedas.</summary>
    public string? EffectiveDisplayField =>
        DisplayField ?? ActiveFields.FirstOrDefault(f => f.Type is FieldTypes.Text or FieldTypes.Choice)?.Name;
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
public abstract record Operation;

public sealed record CreateEntityOp : Operation
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string LabelPlural { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public string? DisplayField { get; init; }
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
}

public sealed record ArchiveFieldOp : Operation
{
    public required string Entity { get; init; }
    public required string Field { get; init; }
}

public sealed record PlanError(int Operation, string Message);
public sealed record PlanStep(int Operation, string Description, string? Sql);
public sealed record PlanPreview(bool Valid, IReadOnlyList<PlanError> Errors, IReadOnlyList<PlanStep> Steps);
