using System.Text.RegularExpressions;

namespace Erp.Api.Metadata;

/// <summary>Resultado de compilar un plan: vista previa + modelo resultante + SQL a ejecutar.</summary>
public sealed record CompiledPlan(
    PlanPreview Preview,
    IReadOnlyDictionary<string, EntityDef> ResultingModel,
    IReadOnlyList<string> Sql,
    IReadOnlySet<string> TouchedEntities);

/// <summary>
/// Valida un <see cref="ChangePlan"/> contra el modelo actual simulando cada operación en orden.
/// Es puro (sin BD) para poder probarlo y para que la vista previa sea exactamente lo que se aplicará.
/// </summary>
public static partial class PlanCompiler
{
    public const int MaxOperations = 100;
    public const int MaxFieldsPerEntity = 200;
    public const int MaxLabel = 100;
    public const int MaxDescription = 500;
    public const int MaxOptions = 200;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();

    public static CompiledPlan Compile(
        ChangePlan plan,
        IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlySet<string> knownRoles)
    {
        var working = new Dictionary<string, EntityDef>(model, StringComparer.OrdinalIgnoreCase);
        var errors = new List<PlanError>();
        var steps = new List<PlanStep>();
        var sql = new List<string>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (plan.Operations.Count == 0)
            errors.Add(new(-1, "El plan no contiene operaciones."));
        if (plan.Operations.Count > MaxOperations)
            errors.Add(new(-1, $"El plan excede {MaxOperations} operaciones."));
        if (plan.Description.Length > MaxDescription)
            errors.Add(new(-1, "La descripción del plan es demasiado larga."));

        for (var i = 0; i < plan.Operations.Count && i < MaxOperations; i++)
        {
            var ctx = new Ctx(i, working, knownRoles, errors);
            var before = errors.Count;
            var result = plan.Operations[i] switch
            {
                CreateEntityOp op => CreateEntity(ctx, op),
                UpdateEntityOp op => UpdateEntity(ctx, op),
                ArchiveEntityOp op => ArchiveEntity(ctx, op),
                AddFieldOp op => AddField(ctx, op),
                UpdateFieldOp op => UpdateField(ctx, op),
                ArchiveFieldOp op => ArchiveField(ctx, op),
                _ => ctx.Fail("Operación desconocida."),
            };
            // Solo se registra el efecto si la operación fue válida; así las siguientes ven un modelo coherente.
            if (result is not null && errors.Count == before)
            {
                working[result.Entity.Name] = result.Entity;
                touched.Add(result.Entity.Name);
                steps.Add(new(i, result.Description, result.Sql.Count > 0 ? string.Join("\n", result.Sql) : null));
                sql.AddRange(result.Sql);
            }
        }

        return new CompiledPlan(new PlanPreview(errors.Count == 0, errors, steps), working, sql, touched);
    }

    private sealed record OpResult(EntityDef Entity, string Description, IReadOnlyList<string> Sql);

    private sealed class Ctx(int index, Dictionary<string, EntityDef> model, IReadOnlySet<string> roles, List<PlanError> errors)
    {
        public Dictionary<string, EntityDef> Model => model;
        public IReadOnlySet<string> Roles => roles;
        public OpResult? Fail(string message) { errors.Add(new(index, message)); return null; }
        public void Error(string message) => errors.Add(new(index, message));
        public int ErrorCount => errors.Count;

        public EntityDef? Active(string? name)
        {
            if (name is null || !model.TryGetValue(name, out var e)) { Error($"El catálogo '{name}' no existe."); return null; }
            if (e.IsArchived) { Error($"El catálogo '{name}' está archivado."); return null; }
            return e;
        }
    }

    // ---------------- Operaciones ----------------

    private static OpResult? CreateEntity(Ctx ctx, CreateEntityOp op)
    {
        var start = ctx.ErrorCount;
        if (!Identifiers.IsValid(op.Name, out var err)) ctx.Error(err);
        else if (ctx.Model.ContainsKey(op.Name)) ctx.Error($"Ya existe un catálogo llamado '{op.Name}'.");
        ValidateLabel(ctx, op.Label, "La etiqueta");
        ValidateLabel(ctx, op.LabelPlural, "La etiqueta en plural");
        ValidateIcon(ctx, op.Icon);
        if (op.Description?.Length > MaxDescription) ctx.Error("La descripción es demasiado larga.");
        if (op.Fields.Count == 0) ctx.Error("Un catálogo necesita al menos un campo.");
        if (op.Fields.Count > MaxFieldsPerEntity) ctx.Error($"Máximo {MaxFieldsPerEntity} campos por catálogo.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<FieldDef>();
        for (var f = 0; f < op.Fields.Count; f++)
        {
            var field = Normalize(op.Fields[f], f);
            if (!names.Add(field.Name)) ctx.Error($"El campo '{field.Name}' está repetido.");
            // Permite auto-referencia (p. ej. Categoria.Padre) validando contra el propio nombre.
            ValidateField(ctx, field, selfEntity: op.Name);
            fields.Add(field);
        }
        if (ctx.ErrorCount > start) return null;

        var entity = new EntityDef
        {
            Name = op.Name, Label = op.Label.Trim(), LabelPlural = op.LabelPlural.Trim(),
            Icon = op.Icon ?? "database", Description = op.Description?.Trim(), Fields = fields,
        };
        if (!ValidateDisplayField(ctx, entity, op.DisplayField)) return null;
        entity = entity with { DisplayField = op.DisplayField };

        return new(entity, $"Crear catálogo '{entity.Label}' con {fields.Count} campo(s)",
            DdlGenerator.CreateTable(entity.Name, fields));
    }

    private static OpResult? UpdateEntity(Ctx ctx, UpdateEntityOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        if (op.Label is not null) ValidateLabel(ctx, op.Label, "La etiqueta");
        if (op.LabelPlural is not null) ValidateLabel(ctx, op.LabelPlural, "La etiqueta en plural");
        if (op.Icon is not null) ValidateIcon(ctx, op.Icon);
        if (op.Description?.Length > MaxDescription) ctx.Error("La descripción es demasiado larga.");
        if (op.DisplayField is not null && !ValidateDisplayField(ctx, e, op.DisplayField)) return null;

        var updated = e with
        {
            Label = op.Label?.Trim() ?? e.Label,
            LabelPlural = op.LabelPlural?.Trim() ?? e.LabelPlural,
            Icon = op.Icon ?? e.Icon,
            Description = op.Description?.Trim() ?? e.Description,
            DisplayField = op.DisplayField ?? e.DisplayField,
        };
        return new(updated, $"Actualizar propiedades del catálogo '{updated.Label}'", []);
    }

    private static OpResult? ArchiveEntity(Ctx ctx, ArchiveEntityOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var referencedBy = ctx.Model.Values
            .Where(o => !o.IsArchived && !string.Equals(o.Name, e.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(o => o.ActiveFields.Where(f => string.Equals(f.ReferenceEntity, e.Name, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{o.Name}.{f.Name}"))
            .ToList();
        if (referencedBy.Count > 0)
            return ctx.Fail($"No se puede archivar '{e.Name}': lo referencian {string.Join(", ", referencedBy)}.");
        return new(e with { IsArchived = true }, $"Archivar catálogo '{e.Label}' (los datos se conservan)", []);
    }

    private static OpResult? AddField(Ctx ctx, AddFieldOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var start = ctx.ErrorCount;
        var field = Normalize(op.Field, op.Field.DisplayOrder != 0 ? op.Field.DisplayOrder : e.Fields.Count);
        // Se compara también contra campos archivados: la columna sigue existiendo en la tabla.
        if (e.FindField(field.Name) is not null) ctx.Error($"El campo '{field.Name}' ya existe en '{e.Name}'.");
        if (e.Fields.Count >= MaxFieldsPerEntity) ctx.Error($"Máximo {MaxFieldsPerEntity} campos por catálogo.");
        ValidateField(ctx, field, selfEntity: e.Name);
        if (ctx.ErrorCount > start) return null;

        return new(e with { Fields = [.. e.Fields, field] },
            $"Agregar campo '{field.Label}' ({field.Type}) a '{e.Label}'",
            DdlGenerator.AddColumn(e.Name, field));
    }

    private static OpResult? UpdateField(Ctx ctx, UpdateFieldOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var f = e.FindField(op.Field);
        if (f is null || f.IsArchived) return ctx.Fail($"El campo '{op.Field}' no existe en '{e.Name}'.");
        var start = ctx.ErrorCount;

        if (op.Label is not null) ValidateLabel(ctx, op.Label, "La etiqueta del campo");
        if (op.Options is not null)
        {
            if (f.Type != FieldTypes.Choice) ctx.Error("Solo los campos de tipo lista tienen opciones.");
            else ValidateOptions(ctx, op.Options);
        }
        if (op.RequiredForRoles is not null) ValidateRoles(ctx, op.RequiredForRoles);
        if (ctx.ErrorCount > start) return null;

        var updated = f with
        {
            Label = op.Label?.Trim() ?? f.Label,
            Required = op.Required ?? f.Required,
            ShowInList = op.ShowInList ?? f.ShowInList,
            Options = op.Options?.Select(o => o.Trim()).ToList() ?? f.Options,
            RequiredForRoles = op.RequiredForRoles ?? f.RequiredForRoles,
            DisplayOrder = op.DisplayOrder ?? f.DisplayOrder,
        };
        return new(ReplaceField(e, updated), $"Actualizar campo '{updated.Label}' de '{e.Label}'", []);
    }

    private static OpResult? ArchiveField(Ctx ctx, ArchiveFieldOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var f = e.FindField(op.Field);
        if (f is null || f.IsArchived) return ctx.Fail($"El campo '{op.Field}' no existe en '{e.Name}'.");
        if (e.ActiveFields.Count() == 1) return ctx.Fail("No se puede archivar el último campo activo.");

        var updated = ReplaceField(e, f with { IsArchived = true });
        if (string.Equals(updated.DisplayField, f.Name, StringComparison.OrdinalIgnoreCase))
            updated = updated with { DisplayField = null };
        return new(updated, $"Archivar campo '{f.Label}' de '{e.Label}' (la columna y sus datos se conservan)", []);
    }

    // ---------------- Validaciones ----------------

    private static FieldDef Normalize(FieldDef f, int order)
    {
        var type = f.Type?.Trim().ToLowerInvariant() ?? "";
        return f with
        {
            Label = f.Label?.Trim() ?? "",
            Type = type,
            MaxLength = type == FieldTypes.Text ? f.MaxLength ?? FieldTypes.DefaultTextLength : null,
            Precision = type == FieldTypes.Decimal ? f.Precision ?? 18 : null,
            Scale = type == FieldTypes.Decimal ? f.Scale ?? 2 : null,
            Options = type == FieldTypes.Choice ? f.Options?.Select(o => o.Trim()).ToList() : null,
            ReferenceEntity = type == FieldTypes.Reference ? f.ReferenceEntity : null,
            DisplayOrder = f.DisplayOrder != 0 ? f.DisplayOrder : order,
            IsArchived = false,
        };
    }

    private static void ValidateField(Ctx ctx, FieldDef f, string selfEntity)
    {
        if (!Identifiers.IsValidFieldName(f.Name, out var err)) { ctx.Error(err); return; }
        ValidateLabel(ctx, f.Label, $"La etiqueta del campo '{f.Name}'");
        if (!FieldTypes.All.Contains(f.Type)) { ctx.Error($"Tipo '{f.Type}' no soportado en '{f.Name}'."); return; }

        if (f.Type == FieldTypes.Text && f.MaxLength is < 1 or > FieldTypes.MaxTextLength)
            ctx.Error($"La longitud de '{f.Name}' debe estar entre 1 y {FieldTypes.MaxTextLength}.");
        if (f.Type == FieldTypes.Decimal && (f.Precision is < 1 or > 38 || f.Scale < 0 || f.Scale > f.Precision))
            ctx.Error($"Precisión/escala inválidas en '{f.Name}' (precisión 1-38, escala 0-precisión).");
        if (f.Type == FieldTypes.Choice)
        {
            if (f.Options is null || f.Options.Count == 0) ctx.Error($"El campo de lista '{f.Name}' necesita opciones.");
            else ValidateOptions(ctx, f.Options);
        }
        if (f.Type == FieldTypes.Reference)
        {
            var target = f.ReferenceEntity;
            var isSelf = string.Equals(target, selfEntity, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(target)) ctx.Error($"El campo '{f.Name}' debe indicar el catálogo al que hace referencia.");
            else if (!isSelf && (!ctx.Model.TryGetValue(target, out var t) || t.IsArchived))
                ctx.Error($"El catálogo referenciado '{target}' no existe.");
        }
        if (f.Unique && !FieldTypes.Uniqueable.Contains(f.Type))
            ctx.Error($"El tipo '{f.Type}' no admite valores únicos ('{f.Name}').");
        if (f.RequiredForRoles is not null) ValidateRoles(ctx, f.RequiredForRoles);
    }

    private static void ValidateOptions(Ctx ctx, IReadOnlyList<string> options)
    {
        if (options.Count > MaxOptions) ctx.Error($"Máximo {MaxOptions} opciones.");
        if (options.Any(o => string.IsNullOrWhiteSpace(o) || o.Trim().Length > FieldTypes.MaxChoiceLength))
            ctx.Error($"Cada opción debe tener entre 1 y {FieldTypes.MaxChoiceLength} caracteres.");
        if (options.Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
            ctx.Error("Hay opciones repetidas.");
    }

    private static void ValidateRoles(Ctx ctx, IReadOnlyList<string> roles)
    {
        foreach (var r in roles.Where(r => !ctx.Roles.Contains(r)))
            ctx.Error($"El rol '{r}' no existe.");
    }

    private static void ValidateLabel(Ctx ctx, string? label, string what)
    {
        if (string.IsNullOrWhiteSpace(label)) ctx.Error($"{what} es obligatoria.");
        else if (label.Trim().Length > MaxLabel) ctx.Error($"{what} excede {MaxLabel} caracteres.");
    }

    private static void ValidateIcon(Ctx ctx, string? icon)
    {
        if (icon is not null && (icon.Length > 40 || !IconPattern().IsMatch(icon)))
            ctx.Error($"Icono '{icon}' no válido (usa el nombre de Lucide en kebab-case).");
    }

    private static bool ValidateDisplayField(Ctx ctx, EntityDef e, string? displayField)
    {
        if (displayField is null) return true;
        var f = e.FindField(displayField);
        if (f is null || f.IsArchived || f.Type is not (FieldTypes.Text or FieldTypes.Choice))
        {
            ctx.Error($"El campo para mostrar '{displayField}' debe ser un campo de texto o lista activo.");
            return false;
        }
        return true;
    }

    private static EntityDef ReplaceField(EntityDef e, FieldDef updated) => e with
    {
        Fields = e.Fields.Select(x => string.Equals(x.Name, updated.Name, StringComparison.OrdinalIgnoreCase) ? updated : x).ToList(),
    };
}
