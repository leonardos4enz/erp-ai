using System.Text.RegularExpressions;

namespace Erp.Api.Metadata;

/// <summary>Resultado de compilar un plan: vista previa + modelo resultante + SQL a ejecutar.</summary>
public sealed record CompiledPlan(
    PlanPreview Preview,
    IReadOnlyDictionary<string, EntityDef> ResultingModel,
    IReadOnlyList<string> Sql,
    IReadOnlySet<string> TouchedEntities,
    IReadOnlyDictionary<string, DashboardDef>? Dashboards = null,
    IReadOnlySet<string>? TouchedDashboards = null);

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
    public const int MaxGroup = 60;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();

    public static CompiledPlan Compile(
        ChangePlan plan,
        IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlySet<string> knownRoles,
        IReadOnlyDictionary<string, DashboardDef>? dashboards = null)
    {
        var working = new Dictionary<string, EntityDef>(model, StringComparer.OrdinalIgnoreCase);
        var boards = new Dictionary<string, DashboardDef>(dashboards ?? new Dictionary<string, DashboardDef>(), StringComparer.OrdinalIgnoreCase);
        var touchedBoards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastBoardOp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<PlanError>();
        var steps = new List<PlanStep>();
        var sql = new List<string>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastOp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
            // Spec 007: los tableros no pertenecen a un catálogo.
            if (plan.Operations[i] is SetDashboardOp or ArchiveDashboardOp)
            {
                if (DashboardOp(ctx, plan.Operations[i], boards) is { } board && errors.Count == before)
                {
                    boards[board.Def.Name] = board.Def;
                    touchedBoards.Add(board.Def.Name);
                    lastBoardOp[board.Def.Name] = i;
                    steps.Add(new(i, board.Description, null));
                }
                continue;
            }
            var result = plan.Operations[i] switch
            {
                CreateEntityOp op => CreateEntity(ctx, op),
                UpdateEntityOp op => UpdateEntity(ctx, op),
                ArchiveEntityOp op => ArchiveEntity(ctx, op),
                AddFieldOp op => AddField(ctx, op),
                UpdateFieldOp op => UpdateField(ctx, op),
                ArchiveFieldOp op => ArchiveField(ctx, op),
                SetWorkflowOp op => SetWorkflow(ctx, op),
                SetRulesOp op => SetRules(ctx, op),
                SetAutomationsOp op => SetAutomations(ctx, op),
                SetSecurityOp op => SetSecurity(ctx, op),
                SetLayoutOp op => SetLayout(ctx, op),
                SetViewsOp op => SetViews(ctx, op),
                SetReportsOp op => SetReports(ctx, op),
                SetWebhooksOp op => SetWebhooks(ctx, op),
                SetSchedulesOp op => SetSchedules(ctx, op),
                _ => ctx.Fail("Operación desconocida."),
            };
            // Solo se registra el efecto si la operación fue válida; así las siguientes ven un modelo coherente.
            if (result is not null && errors.Count == before)
            {
                working[result.Entity.Name] = result.Entity;
                touched.Add(result.Entity.Name);
                lastOp[result.Entity.Name] = i;
                steps.Add(new(i, result.Description, result.Sql.Count > 0 ? string.Join("\n", result.Sql) : null));
                sql.AddRange(result.Sql);
            }
        }

        // Pasada final: expresiones y relaciones se validan contra el modelo completo, porque un plan
        // puede crear la partida después del documento y luego el total que la suma.
        foreach (var name in touched)
            foreach (var message in ValidateFinal(working[name], working, knownRoles))
                errors.Add(new(lastOp[name], message));

        // Spec 005: las automatizaciones dependen de otros catálogos (campos, procesos), así que se revisan
        // todas aunque el plan no toque su catálogo: un cambio en el destino no puede romperlas en silencio.
        if (touched.Count > 0)
        {
            foreach (var e in working.Values.Where(e => !e.IsArchived && e.Automations is { Count: > 0 }))
                foreach (var message in AutomationRules.Validate(e, working, knownRoles))
                    errors.Add(new(lastOp.GetValueOrDefault(e.Name, -1), message));
            if (AutomationRules.FindCycle(working) is { } cycle)
                errors.Add(new(-1, $"Ciclo de automatizaciones: {string.Join(" → ", cycle)}. Una automatización no puede terminar disparándose a sí misma."));

            // Spec 006: un campo archivado o un estado eliminado (también en el documento de una partida)
            // no puede dejar una política de seguridad rota en silencio.
            foreach (var e in working.Values.Where(e => !e.IsArchived && e.Security is not null))
                foreach (var message in SecurityRules.Validate(e, e.Security!, working, knownRoles))
                    errors.Add(new(lastOp.GetValueOrDefault(e.Name, -1), $"{e.Label} › seguridad: {message}"));
        }

        // Spec 007: layouts, vistas y reportes usan campos de otros catálogos (Relacion.Campo) y los tableros, reportes:
        // se revisan todos para que un cambio en otro lado no los rompa en silencio.
        if (touched.Count > 0 || touchedBoards.Count > 0)
        {
            foreach (var e in working.Values.Where(e => !e.IsArchived))
            {
                var at = lastOp.GetValueOrDefault(e.Name, -1);
                if (e.Layout is { } layout)
                    foreach (var m in InterfaceRules.ValidateLayout(e, layout, working, knownRoles)) errors.Add(new(at, $"{e.Label} › layout: {m}"));
                if (e.Views is { } views)
                    foreach (var m in InterfaceRules.ValidateViews(e, views, knownRoles)) errors.Add(new(at, $"{e.Label} › {m}"));
                if (e.Reports is { } reports)
                    foreach (var m in InterfaceRules.ValidateReports(e, reports, working, knownRoles)) errors.Add(new(at, $"{e.Label} › {m}"));
                // Spec 008: un campo archivado, un estado eliminado o un cambio de seguridad no puede romper un webhook en silencio.
                if (e.Webhooks is { } hooks)
                    foreach (var m in WebhookRules.Validate(e, hooks, working, knownRoles)) errors.Add(new(at, $"{e.Label} › {m}"));
                // Spec 016: igual con las tareas programadas (consulta, rol y acciones dependen de otros catálogos).
                if (e.Schedules is { } schedules)
                    foreach (var m in ScheduleRules.Validate(e, schedules, working, knownRoles)) errors.Add(new(at, $"{e.Label} › {m}"));
            }
            foreach (var d in boards.Values.Where(d => !d.IsArchived))
                foreach (var m in InterfaceRules.ValidateDashboard(d, working, knownRoles))
                    errors.Add(new(lastBoardOp.GetValueOrDefault(d.Name, -1), $"Tablero '{d.Label}': {m}"));
        }

        return new CompiledPlan(new PlanPreview(errors.Count == 0, errors, steps), working, sql, touched, boards, touchedBoards);
    }

    private static IEnumerable<string> ValidateFinal(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        if (e.IsArchived) yield break;
        var msgs = new List<string>();
        void Check(string? expr, string where)
        {
            if (string.IsNullOrWhiteSpace(expr)) return;
            foreach (var err in Expressions.ExprAnalyzer.Analyze(expr, e, model, roles).Errors)
                msgs.Add($"{e.Name} › {where}: {err}");
        }

        var parents = e.ActiveFields.Count(f => f.Type == FieldTypes.Parent);
        if (parents > 1) msgs.Add($"{e.Name} solo puede tener un campo de tipo documento padre.");
        if (e.IsChild && e.Workflow is not null) msgs.Add($"{e.Name} es una partida: el proceso se define en su documento.");
        if (e.IsChild && e.PerCompany) msgs.Add($"{e.Name} es una partida: sigue la empresa de su documento y no se declara por empresa.");
        // Spec 010: en un catálogo compartido la moneda base o la empresa dependerían de quién guarda el registro.
        var shared = !(e.IsChild ? model.TryGetValue(e.ParentEntity ?? "", out var doc) && doc.PerCompany : e.PerCompany);

        foreach (var f in e.ActiveFields)
        {
            Check(f.DefaultValue, $"valor por defecto de {f.Name}");
            Check(f.Formula, $"fórmula de {f.Name}");
            if (shared && f.Formula is not null && Expressions.ExprAnalyzer.UsedFunctions(f.Formula).FirstOrDefault(Expressions.ExprAnalyzer.CompanyFunctions.Contains) is { } fn)
                msgs.Add($"{e.Name} › fórmula de {f.Name}: {fn}() solo se usa en fórmulas de catálogos por empresa (en uno compartido el resultado dependería de quién guarda).");
            if (f.LookupFilter is { } lf) msgs.AddRange(ValidateLookupFilter(e, f, lf, model));
            if (f.Type == FieldTypes.Lines)
            {
                if (!model.TryGetValue(f.ChildEntity ?? "", out var child) || child.IsArchived)
                    msgs.Add($"{e.Name}.{f.Name}: el catálogo de partidas '{f.ChildEntity}' no existe.");
                else if (!string.Equals(child.ParentEntity, e.Name, StringComparison.OrdinalIgnoreCase))
                    msgs.Add($"{e.Name}.{f.Name}: '{child.Name}' necesita un campo de tipo documento padre que apunte a {e.Name}.");
            }
        }
        try { Expressions.FormulaGraph.Order(e); }
        catch (Expressions.ExprException ex) { msgs.Add($"{e.Name}: {ex.Message}"); }

        foreach (var r in e.Rules ?? [])
        {
            Check(r.When, $"regla {r.Name} (cuándo)");
            Check(r.Assert, $"regla {r.Name}");
        }
        foreach (var t in e.Workflow?.Transitions ?? [])
        {
            Check(t.When, $"transición {t.Name}");
            foreach (var s in t.Set ?? []) Check(s.Value, $"transición {t.Name} › {s.Field}");
        }
        foreach (var m in msgs) yield return m;
    }

    /// <summary>El filtro del buscador: campo del catálogo referenciado = campo propio o del documento padre.</summary>
    private static IEnumerable<string> ValidateLookupFilter(EntityDef e, FieldDef f, LookupFilter lf, IReadOnlyDictionary<string, EntityDef> model)
    {
        var where = $"{e.Name}.{f.Name} (filtro del buscador)";
        if (f.Type != FieldTypes.Reference) { yield return $"{where}: solo las referencias admiten filtro."; yield break; }
        if (!model.TryGetValue(f.ReferenceEntity ?? "", out var target)) yield break; // se reporta en la validación del campo
        var tf = target.FindField(lf.Field ?? "");
        if (tf is null || tf.IsArchived || FieldTypes.IsVirtual(tf.Type))
            yield return $"{where}: '{lf.Field}' no es un campo de {target.Name}.";
        var parts = (lf.Source ?? "").Split('.');
        var head = e.FindField(parts[0]);
        if (parts.Length > 2 || head is null || head.IsArchived || FieldTypes.IsVirtual(head.Type))
            yield return $"{where}: '{lf.Source}' no es un campo de {e.Name}.";
        else if (parts.Length == 2 && (head.Type != FieldTypes.Parent || !model.TryGetValue(head.ReferenceEntity ?? "", out var parent) ||
                                       parent.FindField(parts[1]) is not { IsArchived: false } pf || FieldTypes.IsVirtual(pf.Type)))
            yield return $"{where}: '{lf.Source}' debe ser un campo del documento padre (p. ej. {(e.ParentField?.Name ?? "Documento")}.Campo).";
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

        if (op.Group?.Trim().Length > MaxGroup) { ctx.Error($"El grupo del menú admite máximo {MaxGroup} caracteres."); return null; }
        var entity = new EntityDef
        {
            Name = op.Name, Label = op.Label.Trim(), LabelPlural = op.LabelPlural.Trim(),
            Icon = op.Icon ?? "database", Description = op.Description?.Trim(), Fields = fields,
            Group = string.IsNullOrWhiteSpace(op.Group) ? null : op.Group.Trim(),
            PerCompany = op.PerCompany,
        };
        if (!ValidateDisplayField(ctx, entity, op.DisplayField)) return null;
        entity = entity with { DisplayField = op.DisplayField };

        return new(entity, $"Crear catálogo '{entity.Label}' con {fields.Count} campo(s){(op.PerCompany ? ", un registro por empresa" : "")}",
            DdlGenerator.CreateTable(entity.Name, fields, op.PerCompany));
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
        if (op.Group?.Trim().Length > MaxGroup) { ctx.Error($"El grupo del menú admite máximo {MaxGroup} caracteres."); return null; }
        // Spec 010: pasar a un registro por empresa no se puede deshacer (los registros ya tienen dueña).
        var toCompany = op.PerCompany == true && !e.PerCompany;
        if (op.PerCompany == false && e.PerCompany)
            return ctx.Fail($"'{e.Label}' es por empresa: un catálogo por empresa no puede volver a ser compartido.");
        if (toCompany && e.IsChild)
            return ctx.Fail($"'{e.Label}' es una partida: sigue la empresa de su documento.");

        var updated = e with
        {
            Label = op.Label?.Trim() ?? e.Label,
            LabelPlural = op.LabelPlural?.Trim() ?? e.LabelPlural,
            Icon = op.Icon ?? e.Icon,
            Description = op.Description?.Trim() ?? e.Description,
            DisplayField = op.DisplayField ?? e.DisplayField,
            Group = op.Group is null ? e.Group : op.Group.Trim() is { Length: > 0 } g ? g : null,
            PerCompany = e.PerCompany || toCompany,
        };
        if (toCompany)
            return new(updated, $"'{updated.Label}' pasa a un registro por empresa (los registros existentes quedan en la empresa predeterminada)",
                DdlGenerator.MakePerCompany(e.Name, e.Fields));
        return new(updated, $"Actualizar propiedades del catálogo '{updated.Label}'", []);
    }

    private static OpResult? ArchiveEntity(Ctx ctx, ArchiveEntityOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var referencedBy = ctx.Model.Values
            .Where(o => !o.IsArchived && !string.Equals(o.Name, e.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(o => o.ActiveFields.Where(f => string.Equals(f.ReferenceEntity, e.Name, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals(f.ChildEntity, e.Name, StringComparison.OrdinalIgnoreCase))
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
            DdlGenerator.AddColumn(e.Name, field, e.PerCompany));
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
        if (op.DefaultValue is not null && (f.ReadOnly || f.Type == FieldTypes.Lines))
            ctx.Error($"'{f.Name}' es calculado por el motor y no admite valor por defecto.");
        if (op.Formula is not null && f.Type != FieldTypes.Formula)
            ctx.Error($"'{f.Name}' no es un campo de fórmula.");
        if (op.Formula is not null && string.IsNullOrWhiteSpace(op.Formula))
            ctx.Error($"La fórmula de '{f.Name}' no puede quedar vacía.");
        if (op.LookupFilter is not null && f.Type != FieldTypes.Reference)
            ctx.Error($"'{f.Name}' no es una referencia: no admite filtro del buscador.");
        if (ctx.ErrorCount > start) return null;

        var updated = f with
        {
            Label = op.Label?.Trim() ?? f.Label,
            Required = op.Required ?? f.Required,
            ShowInList = op.ShowInList ?? f.ShowInList,
            Options = op.Options?.Select(o => o.Trim()).ToList() ?? f.Options,
            RequiredForRoles = op.RequiredForRoles ?? f.RequiredForRoles,
            DisplayOrder = op.DisplayOrder ?? f.DisplayOrder,
            DefaultValue = op.DefaultValue is null ? f.DefaultValue : op.DefaultValue.Trim() is { Length: > 0 } d ? d : null,
            Formula = op.Formula?.Trim() ?? f.Formula,
            Editable = f.ReadOnly || f.Type == FieldTypes.Lines || (op.Editable ?? f.Editable),
            LookupFilter = op.LookupFilter is null ? f.LookupFilter : string.IsNullOrWhiteSpace(op.LookupFilter.Field) ? null : op.LookupFilter,
        };
        ValidateLock(ctx, updated);
        if (ctx.ErrorCount > start) return null;
        var detail = updated.Editable != f.Editable
            ? updated.Editable ? " (vuelve a ser editable por el usuario)" : " (ahora lo asigna el sistema)"
            : "";
        return new(ReplaceField(e, updated), $"Actualizar campo '{updated.Label}' de '{e.Label}'{detail}", []);
    }

    private static OpResult? ArchiveField(Ctx ctx, ArchiveFieldOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var f = e.FindField(op.Field);
        if (f is null || f.IsArchived) return ctx.Fail($"El campo '{op.Field}' no existe en '{e.Name}'.");
        if (e.ActiveFields.Count() == 1) return ctx.Fail("No se puede archivar el último campo activo.");
        if (f.Type is FieldTypes.State or FieldTypes.Parent)
            return ctx.Fail($"'{f.Name}' lo administra el motor y no se puede archivar.");

        var updated = ReplaceField(e, f with { IsArchived = true });
        if (string.Equals(updated.DisplayField, f.Name, StringComparison.OrdinalIgnoreCase))
            updated = updated with { DisplayField = null };
        updated = WithoutField(updated, f.Name);
        return new(updated, $"Archivar campo '{f.Label}' de '{e.Label}' (la columna y sus datos se conservan)", []);
    }

    private static readonly HashSet<string> StateColors = ["neutral", "accent", "success", "warning", "danger"];
    private static readonly HashSet<string> Variants = ["primary", "secondary", "danger"];
    public const int MaxRules = 50;
    public const int MaxStates = 30;
    public const int MaxTransitions = 60;

    private static OpResult? SetWorkflow(Ctx ctx, SetWorkflowOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var start = ctx.ErrorCount;
        var w = op.Workflow;
        var existing = e.FindField(Workflow.StateField);
        if (existing is not null && existing.Type != FieldTypes.State)
            ctx.Error($"'{e.Name}' ya tiene un campo '{Workflow.StateField}' que no es de proceso.");
        if (w.States.Count is 0 or > MaxStates) ctx.Error($"El proceso necesita de 1 a {MaxStates} estados.");
        if (w.Transitions.Count > MaxTransitions) ctx.Error($"Máximo {MaxTransitions} transiciones.");

        var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in w.States)
        {
            if (!Identifiers.IsValid(s.Name, out var err)) ctx.Error($"Estado: {err}");
            else if (!states.Add(s.Name)) ctx.Error($"El estado '{s.Name}' está repetido.");
            ValidateLabel(ctx, s.Label, $"La etiqueta del estado '{s.Name}'");
            if (!StateColors.Contains(s.Color)) ctx.Error($"Color '{s.Color}' no válido en '{s.Name}' ({string.Join(", ", StateColors)}).");
        }
        if (!states.Contains(w.Initial ?? "")) ctx.Error($"El estado inicial '{w.Initial}' no está definido.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in w.Transitions)
        {
            var where = $"Transición '{t.Name}'";
            if (!Identifiers.IsValid(t.Name, out var err)) ctx.Error($"{where}: {err}");
            else if (!names.Add(t.Name)) ctx.Error($"{where} está repetida.");
            ValidateLabel(ctx, t.Label, $"La etiqueta de {where}");
            if (t.From.Count == 0) ctx.Error($"{where}: indica desde qué estados aplica.");
            foreach (var f in t.From.Where(f => !states.Contains(f))) ctx.Error($"{where}: el estado '{f}' no existe.");
            if (!states.Contains(t.To)) ctx.Error($"{where}: el estado destino '{t.To}' no existe.");
            if (t.Roles is not null) ValidateRoles(ctx, t.Roles);
            if (t.WhenMessage?.Length > 300) ctx.Error($"{where}: el mensaje es demasiado largo.");
            if (t.Icon is not null) ValidateIcon(ctx, t.Icon);
            if (t.Variant is not null && !Variants.Contains(t.Variant)) ctx.Error($"{where}: variante '{t.Variant}' no válida.");
            foreach (var rf in t.RequiredFields ?? [])
                if (e.FindField(rf) is not { IsArchived: false } fd || fd.ClientLocked || fd.Type == FieldTypes.Lines)
                    ctx.Error($"{where}: '{rf}' no es un campo capturable de {e.Name}.");
            foreach (var s in t.Set ?? [])
                if (e.FindField(s.Field) is not { IsArchived: false } fd || fd.ReadOnly || fd.Type == FieldTypes.Lines)
                    ctx.Error($"{where}: no se puede asignar '{s.Field}'.");
        }
        if (ctx.ErrorCount > start) return null;

        var stateField = new FieldDef
        {
            Name = Workflow.StateField, Label = "Estado", Type = FieldTypes.State, ShowInList = true,
            Options = w.States.Select(s => s.Name).ToList(), DisplayOrder = existing?.DisplayOrder ?? -1,
        };
        var updated = existing is null
            ? e with { Workflow = w, Fields = [.. e.Fields, stateField] }
            : ReplaceField(e with { Workflow = w }, stateField);
        return new(updated,
            $"{(existing is null ? "Definir" : "Actualizar")} proceso de '{e.Label}': {w.States.Count} estados, {w.Transitions.Count} transiciones",
            existing is null ? DdlGenerator.AddStateColumn(e.Name, w.Initial!) : []);
    }

    private static OpResult? SetRules(Ctx ctx, SetRulesOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var start = ctx.ErrorCount;
        if (op.Rules.Count > MaxRules) ctx.Error($"Máximo {MaxRules} reglas por catálogo.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in op.Rules)
        {
            if (!Identifiers.IsValid(r.Name, out var err)) ctx.Error($"Regla: {err}");
            else if (!names.Add(r.Name)) ctx.Error($"La regla '{r.Name}' está repetida.");
            if (string.IsNullOrWhiteSpace(r.Message) || r.Message.Length > 300) ctx.Error($"La regla '{r.Name}' necesita un mensaje (máx. 300).");
            if (string.IsNullOrWhiteSpace(r.Assert)) ctx.Error($"La regla '{r.Name}' necesita una condición.");
            if (r.Field is not null && e.FindField(r.Field) is not { IsArchived: false })
                ctx.Error($"La regla '{r.Name}' apunta al campo '{r.Field}', que no existe.");
        }
        if (ctx.ErrorCount > start) return null;
        return new(e with { Rules = op.Rules }, $"Definir {op.Rules.Count} regla(s) de negocio en '{e.Label}'", []);
    }

    /// <summary>Forma básica aquí; campos, destinos y expresiones en la pasada final (<see cref="AutomationRules"/>).</summary>
    /// <summary>Spec 006. La política se valida completa en la pasada final (puede usar estados definidos después).</summary>
    private static OpResult? SetSecurity(Ctx ctx, SetSecurityOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var s = op.Security;
        if (s is not null && s.Fields is null && s.Records is null && s.Owner is null && !s.SystemManaged) s = null;
        if (s is null)
            return new(e with { Security = null }, $"Quitar la seguridad por campo y por registro de '{e.Label}'", []);
        var parts = new List<string>();
        if (s.Fields is { Count: > 0 } fs) parts.Add($"{fs.Count} regla(s) de campo");
        if (s.Records is { Count: > 0 } rs) parts.Add($"{rs.Count} permiso(s) por registro");
        if (s.SystemManaged) parts.Add("lo administra el sistema");
        return new(e with { Security = s }, $"Definir la seguridad de '{e.Label}': {(parts.Count == 0 ? "dueño del registro" : string.Join(", ", parts))}", []);
    }

    private static OpResult? SetAutomations(Ctx ctx, SetAutomationsOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var start = ctx.ErrorCount;
        if (e.IsChild) ctx.Error($"{e.Name} es una partida: las automatizaciones se declaran en su documento (usa \"lines\").");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in op.Automations)
        {
            if (!Identifiers.IsValid(a.Name, out var err)) ctx.Error($"Automatización: {err}");
            else if (!names.Add(a.Name)) ctx.Error($"La automatización '{a.Name}' está repetida.");
            ValidateLabel(ctx, a.Label, $"La etiqueta de la automatización '{a.Name}'");
        }
        if (ctx.ErrorCount > start) return null;

        IReadOnlyList<Automation> list = op.Automations;
        if (op.Merge)
        {
            var incoming = op.Automations.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
            var kept = (e.Automations ?? []).Select(a => incoming.Remove(a.Name, out var repl) ? repl : a).ToList();
            list = [.. kept, .. op.Automations.Where(a => incoming.ContainsKey(a.Name))];
        }
        if (list.Count > AutomationRules.MaxAutomations) return ctx.Fail($"Máximo {AutomationRules.MaxAutomations} automatizaciones por catálogo.");
        var verb = op.Merge ? "Agregar o actualizar" : "Definir";
        return new(e with { Automations = list.Count == 0 ? null : list },
            $"{verb} {op.Automations.Count} automatización(es) en '{e.Label}': {string.Join(", ", op.Automations.Select(a => a.Label))}", []);
    }

    // ---------------- Spec 007: metadatos de interfaz ----------------
    // La forma completa se valida en la pasada final: un plan puede agregar los campos después del layout o del reporte.

    private static OpResult? SetLayout(Ctx ctx, SetLayoutOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        if (op.Layout is null) return new(e with { Layout = null }, $"Quitar el layout del formulario de '{e.Label}'", []);
        var tabs = op.Layout.Sections.Select(s => s.Tab).OfType<string>().Distinct().Count();
        var detail = tabs > 0 ? $" en {tabs} pestaña(s)" : "";
        return new(e with { Layout = op.Layout }, $"Definir el formulario de '{e.Label}': {op.Layout.Sections.Count} sección(es){detail}", []);
    }

    private static OpResult? SetViews(Ctx ctx, SetViewsOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var text = op.Views.Count == 0 ? $"Quitar las vistas compartidas de '{e.Label}'"
            : $"Definir {op.Views.Count} vista(s) compartida(s) en '{e.Label}': {string.Join(", ", op.Views.Select(v => v.Label))}";
        return new(e with { Views = op.Views.Count == 0 ? null : op.Views }, text, []);
    }

    private static OpResult? SetReports(Ctx ctx, SetReportsOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var text = op.Reports.Count == 0 ? $"Quitar los reportes de '{e.Label}'"
            : $"Definir {op.Reports.Count} reporte(s) en '{e.Label}': {string.Join(", ", op.Reports.Select(r => r.Label))}";
        return new(e with { Reports = op.Reports.Count == 0 ? null : op.Reports }, text, []);
    }

    // ---------------- Spec 016: tareas programadas ----------------
    // La forma completa se valida en la pasada final (ScheduleRules): las acciones pueden usar catálogos creados después.

    private static OpResult? SetSchedules(Ctx ctx, SetSchedulesOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var text = op.Schedules.Count == 0 ? $"Quitar las tareas programadas de '{e.Label}'"
            : $"Definir {op.Schedules.Count} tarea(s) programada(s) en '{e.Label}': {string.Join(", ", op.Schedules.Select(s => s.Label))}";
        return new(e with { Schedules = op.Schedules.Count == 0 ? null : op.Schedules }, text, []);
    }

    // ---------------- Spec 008: webhooks ----------------

    private static OpResult? SetWebhooks(Ctx ctx, SetWebhooksOp op)
    {
        var e = ctx.Active(op.Entity);
        if (e is null) return null;
        var text = op.Webhooks.Count == 0 ? $"Quitar los webhooks de '{e.Label}'"
            : $"Definir {op.Webhooks.Count} webhook(s) en '{e.Label}': {string.Join(", ", op.Webhooks.Select(w => w.Label))} " +
              "(el destino se configura en Integraciones)";
        return new(e with { Webhooks = op.Webhooks.Count == 0 ? null : op.Webhooks }, text, []);
    }

    private sealed record BoardResult(DashboardDef Def, string Description);

    private static BoardResult? DashboardOp(Ctx ctx, Operation op, Dictionary<string, DashboardDef> boards)
    {
        if (op is ArchiveDashboardOp a)
        {
            if (!boards.TryGetValue(a.Name ?? "", out var existing) || existing.IsArchived) { ctx.Error($"El tablero '{a.Name}' no existe."); return null; }
            return new(existing with { IsArchived = true }, $"Archivar el tablero '{existing.Label}' (la definición se conserva)");
        }
        var d = ((SetDashboardOp)op).Dashboard;
        if (d is null) { ctx.Error("Indica el tablero."); return null; }
        if (!Identifiers.IsValid(d.Name, out var err)) { ctx.Error($"Tablero: {err}"); return null; }
        ValidateIcon(ctx, d.Icon);
        var exists = boards.TryGetValue(d.Name, out var prev) && !prev.IsArchived;
        // El nombre guardado conserva la capitalización original para que el enlace no cambie.
        var name = prev?.Name ?? d.Name;
        return new(d with { Name = name, IsArchived = false, Icon = d.Icon ?? "layout-dashboard" },
            $"{(exists ? "Actualizar" : "Definir")} el tablero '{d.Label}' con {d.Widgets.Count} tarjeta(s)");
    }

    /// <summary>Al archivar un campo se retira del layout y de las columnas de las vistas (no hay nada que mostrar).</summary>
    private static EntityDef WithoutField(EntityDef e, string field)
    {
        bool Other(string n) => !string.Equals(n, field, StringComparison.OrdinalIgnoreCase);
        var layout = e.Layout is null ? null : e.Layout with
        {
            Sections = e.Layout.Sections.Select(s => s with { Fields = s.Fields.Where(i => Other(i.Field)).ToList() })
                .Where(s => s.Fields.Count > 0).ToList(),
        };
        if (layout is { Sections.Count: 0 }) layout = null;
        var views = e.Views?.Select(v => v.Columns is null ? v : v with { Columns = v.Columns.Where(Other).ToList() }).ToList();
        return e with { Layout = layout, Views = views };
    }

    // ---------------- Validaciones ----------------

    private static FieldDef Normalize(FieldDef f, int order)
    {
        var type = f.Type?.Trim().ToLowerInvariant() ?? "";
        var resultType = type == FieldTypes.Formula ? f.ResultType?.Trim().ToLowerInvariant() ?? FieldTypes.Decimal : null;
        var isDecimal = type == FieldTypes.Decimal || resultType == FieldTypes.Decimal;
        var computed = FieldTypes.ReadOnly.Contains(type) || type == FieldTypes.Lines;
        return f with
        {
            Label = f.Label?.Trim() ?? "",
            Type = type,
            MaxLength = type == FieldTypes.Text ? f.MaxLength ?? FieldTypes.DefaultTextLength : null,
            Precision = isDecimal ? f.Precision ?? 18 : null,
            Scale = isDecimal ? f.Scale ?? 2 : null,
            Options = type == FieldTypes.Choice ? f.Options?.Select(o => o.Trim()).ToList() : null,
            ReferenceEntity = type is FieldTypes.Reference or FieldTypes.Parent ? f.ReferenceEntity : null,
            DisplayOrder = f.DisplayOrder != 0 ? f.DisplayOrder : order,
            IsArchived = false,
            // Solo salida (spec 006): la seguridad por campo se declara con setSecurity.
            Access = null,
            EditableIn = null,
            // El motor calcula estos tipos: nunca son obligatorios ni únicos por captura.
            Required = !computed && f.Required,
            Unique = !computed && f.Unique,
            DefaultValue = computed || string.IsNullOrWhiteSpace(f.DefaultValue) ? null : f.DefaultValue.Trim(),
            Formula = type == FieldTypes.Formula ? f.Formula?.Trim() : null,
            ResultType = resultType,
            Prefix = type == FieldTypes.Autonumber ? f.Prefix ?? "" : null,
            Padding = type == FieldTypes.Autonumber ? f.Padding ?? 6 : null,
            ChildEntity = type == FieldTypes.Lines ? f.ChildEntity : null,
            ShowInList = type != FieldTypes.Lines && f.ShowInList,
            Editable = computed || f.Editable,
            LookupFilter = type == FieldTypes.Reference ? f.LookupFilter : null,
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
        if (f.Type is FieldTypes.Reference or FieldTypes.Parent)
        {
            var target = f.ReferenceEntity;
            var isSelf = string.Equals(target, selfEntity, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(target)) ctx.Error($"El campo '{f.Name}' debe indicar el catálogo al que hace referencia.");
            else if (f.Type == FieldTypes.Parent && isSelf) ctx.Error($"'{f.Name}': una partida no puede ser su propio documento.");
            else if (!isSelf && (!ctx.Model.TryGetValue(target, out var t) || t.IsArchived))
                ctx.Error($"El catálogo referenciado '{target}' no existe.");
        }
        if (f.Type == FieldTypes.Autonumber)
        {
            if (f.Prefix is { Length: > 10 } || f.Prefix?.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/')) == true)
                ctx.Error($"El prefijo de '{f.Name}' admite hasta 10 letras, números, '-', '_' o '/'.");
            if (f.Padding is < 1 or > 10) ctx.Error($"Los dígitos del folio '{f.Name}' deben ser de 1 a 10.");
        }
        if (f.Type == FieldTypes.Formula)
        {
            if (string.IsNullOrWhiteSpace(f.Formula)) ctx.Error($"El campo '{f.Name}' necesita una fórmula.");
            if (!FieldTypes.FormulaResults.Contains(f.ResultType ?? "")) ctx.Error($"Tipo de resultado '{f.ResultType}' no soportado en '{f.Name}'.");
            if (f.ResultType == FieldTypes.Decimal && (f.Precision is < 1 or > 38 || f.Scale < 0 || f.Scale > f.Precision))
                ctx.Error($"Precisión/escala inválidas en '{f.Name}'.");
        }
        if (f.Type == FieldTypes.Lines && string.IsNullOrWhiteSpace(f.ChildEntity))
            ctx.Error($"El campo de partidas '{f.Name}' debe indicar su catálogo de partidas.");
        if (f.Unique && !FieldTypes.Uniqueable.Contains(f.Type))
            ctx.Error($"El tipo '{f.Type}' no admite valores únicos ('{f.Name}').");
        if (f.RequiredForRoles is not null) ValidateRoles(ctx, f.RequiredForRoles);
        ValidateLock(ctx, f);
    }

    /// <summary>Un campo asignado por el sistema y obligatorio necesita de dónde tomar su valor.</summary>
    private static void ValidateLock(Ctx ctx, FieldDef f)
    {
        if (!f.Editable && f.Required && f.DefaultValue is null)
            ctx.Error($"'{f.Name}' no es editable y es obligatorio: necesita un valor por defecto (p. ej. TODAY() o CURRENTUSER()).");
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
        // Spec 005: también una fórmula de texto (p. ej. «clave · descripción · pendiente» de una partida).
        var textFormula = f is { Type: FieldTypes.Formula, ResultType: FieldTypes.Text };
        if (f is null || f.IsArchived || !(textFormula || f.Type is FieldTypes.Text or FieldTypes.Choice or FieldTypes.Autonumber))
        {
            ctx.Error($"El campo para mostrar '{displayField}' debe ser un campo de texto, lista, folio o fórmula de texto activo.");
            return false;
        }
        return true;
    }

    private static EntityDef ReplaceField(EntityDef e, FieldDef updated) => e with
    {
        Fields = e.Fields.Select(x => string.Equals(x.Name, updated.Name, StringComparison.OrdinalIgnoreCase) ? updated : x).ToList(),
    };
}
