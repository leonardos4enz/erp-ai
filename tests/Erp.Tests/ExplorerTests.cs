using Erp.Api.Explorer;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 025: explorador del modelo y de la base de datos (catálogo de SQL, deriva, DDL, usos, flujo, seguridad).</summary>
public class ExplorerTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, AppDef> NoApps = new Dictionary<string, AppDef>(Ci);
    private static readonly IReadOnlyDictionary<string, DashboardDef> NoBoards = new Dictionary<string, DashboardDef>(Ci);

    private static FieldDef F(string name, string type = "text", string? reference = null, bool archived = false) =>
        new() { Name = name, Label = name, Type = type, ReferenceEntity = reference, IsArchived = archived };

    private static EntityDef E(string name, string? group = null, bool archived = false, params FieldDef[] fields) =>
        new() { Name = name, Label = name, LabelPlural = name + "s", Group = group, IsArchived = archived, Fields = fields };

    private static IReadOnlyDictionary<string, EntityDef> Model(params EntityDef[] es) => es.ToDictionary(e => e.Name, Ci);

    private static CatColumn Col(string name, string type, bool nullable = true, bool identity = false, string? dfName = null, string? df = null) =>
        new(name, type, nullable, identity, dfName, df, null);

    /// <summary>Columnas de sistema que crea <see cref="DdlGenerator.CreateTable"/>.</summary>
    private static CatColumn[] SystemCols(string table, bool rowVer = true) =>
    [
        Col("Id", "bigint", false, true),
        Col("CreatedAt", "datetime2(3)", false, dfName: $"DF_{table}_CreatedAt", df: "(sysutcdatetime())"),
        Col("CreatedBy", "int", false), Col("UpdatedAt", "datetime2(3)"), Col("UpdatedBy", "int"),
        Col("IsDeleted", "bit", false, dfName: $"DF_{table}_IsDeleted", df: "((0))"),
        .. rowVer ? new[] { Col("RowVer", "rowversion", false) } : [],
    ];

    private static CatIndex Pk(string table) => new($"PK_{table}", true, true, false, null, ["Id"], []);

    // ---------------- H1: tipos y lista blanca ----------------

    [Theory]
    [InlineData("nvarchar", 400, 0, 0, "nvarchar(200)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 30, 0, 0, "varchar(30)")]
    [InlineData("nchar", 20, 0, 0, "nchar(10)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("numeric", 9, 10, 4, "numeric(10,4)")]
    [InlineData("datetime2", 8, 27, 3, "datetime2(3)")]
    [InlineData("datetime2", 6, 19, 0, "datetime2(0)")]
    [InlineData("timestamp", 8, 0, 0, "rowversion")]
    [InlineData("BIGINT", 8, 19, 0, "bigint")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    public void H1_CA1_tipo_sql_exacto(string type, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlTypes.Format(type, maxLength, precision, scale));

    [Fact]
    public void H1_CA1_los_tipos_formateados_coinciden_con_los_que_genera_el_motor()
    {
        // Lo que el catálogo de SQL describe para cada tipo de campo debe ser igual a DdlGenerator.ColumnType (si no, todo sería «deriva»).
        Assert.Equal(DdlGenerator.ColumnType(F("A")), SqlTypes.Format("nvarchar", 400, 0, 0));
        Assert.Equal(DdlGenerator.ColumnType(F("A", "longtext")), SqlTypes.Format("nvarchar", -1, 0, 0));
        Assert.Equal(DdlGenerator.ColumnType(F("A", "decimal")), SqlTypes.Format("decimal", 9, 18, 2));
        Assert.Equal(DdlGenerator.ColumnType(F("A", "datetime")), SqlTypes.Format("datetime2", 6, 19, 0));
        Assert.Equal(DdlGenerator.ColumnType(F("A", "choice")), SqlTypes.Format("nvarchar", 200, 0, 0));
        Assert.Equal(DdlGenerator.ColumnType(F("A", "autonumber")), SqlTypes.Format("nvarchar", 60, 0, 0));
    }

    [Fact]
    public void H7_CA2_el_esquema_sale_de_la_lista_blanca_y_meta_solo_para_el_superusuario()
    {
        Assert.Equal(DbCatalog.App, DbCatalog.Allowed(null, superuser: false));
        Assert.Equal(DbCatalog.App, DbCatalog.Allowed("APP", superuser: false));
        Assert.Null(DbCatalog.Allowed("meta", superuser: false));
        Assert.Equal(DbCatalog.Meta, DbCatalog.Allowed("META", superuser: true));
        // Cualquier otro texto (otro esquema, inyección) se rechaza aunque sea superusuario.
        Assert.Null(DbCatalog.Allowed("dbo", superuser: true));
        Assert.Null(DbCatalog.Allowed("app'; DROP TABLE x;--", superuser: true));
        Assert.Throws<ArgumentException>(() => DbCatalog.ReadAsync(null!, "dbo").GetAwaiter().GetResult());
    }

    // ---------------- H1 CA4: deriva ----------------

    private static (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyList<CatTable> Catalog) Drifted()
    {
        var model = Model(
            E("Cliente", "Ventas", false, F("Nombre"), F("Saldo", "decimal"), F("Viejo", archived: true), F("Proveedor", "reference", "Proveedor"),
                F("Partidas", "lines") with { ChildEntity = "ClientePartida" }),
            E("Proveedor", "Compras", false, F("Nombre")),
            E("Pasado", null, true, F("Nombre")),
            E("Nuevo", "Ventas", false, F("Nombre")));
        var catalog = new List<CatTable>
        {
            new("app", "Cliente",
            [
                .. SystemCols("Cliente", rowVer: false), Col("Nombre", "nvarchar(200)"), Col("Saldo", "decimal(18,4)"),
                Col("Viejo", "nvarchar(200)"), Col("Extra", "int"),
            ],
            [Pk("Cliente"), new("UX_Cliente_Nombre", false, true, false, "([Nombre] IS NOT NULL AND [IsDeleted]=(0))", ["Nombre"], [])],
            [new("FK_Cliente_Proveedor", ["Proveedor"], "app", "Proveedor", ["Id"], "NO_ACTION")]),
            new("app", "Proveedor", [.. SystemCols("Proveedor"), Col("Nombre", "nvarchar(200)")], [Pk("Proveedor")], []),
            new("app", "Pasado", [.. SystemCols("Pasado"), Col("Nombre", "nvarchar(200)")], [Pk("Pasado")], []),
            new("app", "Huerfana", [Col("Id", "bigint", false, true)], [Pk("Huerfana")], []),
        };
        return (model, catalog);
    }

    [Fact]
    public void H1_CA4_deriva_entre_la_base_y_el_modelo()
    {
        var (model, catalog) = Drifted();
        var db = PhysicalModel.Build(DbCatalog.App, catalog, model, NoApps);
        string? Kind(string table, string? col) => db.Drift.FirstOrDefault(d => d.Table == table && d.Column == col)?.Kind;

        Assert.Equal(DriftKinds.OrphanTable, Kind("Huerfana", null));
        Assert.Equal(DriftKinds.ArchivedTable, Kind("Pasado", null));
        Assert.Equal(DriftKinds.MissingTable, Kind("Nuevo", null));
        Assert.Equal(DriftKinds.ArchivedColumn, Kind("Cliente", "Viejo"));
        Assert.Equal(DriftKinds.OrphanColumn, Kind("Cliente", "Extra"));
        Assert.Equal(DriftKinds.TypeMismatch, Kind("Cliente", "Saldo"));
        Assert.Equal(DriftKinds.MissingColumn, Kind("Cliente", "Proveedor"));
        Assert.Equal(DriftKinds.MissingColumn, Kind("Cliente", "RowVer"));
        // Las partidas no tienen columna: no son deriva. CompanyId solo se espera en catálogos por empresa.
        Assert.Null(Kind("Cliente", "Partidas"));
        Assert.Null(Kind("Cliente", "CompanyId"));
        // Una tabla de un catálogo archivado conserva su liga y no reporta sus columnas como huérfanas.
        Assert.Single(db.Drift, d => d.Table == "Pasado");

        var cliente = db.Tables.Single(t => t.Name == "Cliente");
        Assert.Equal("Ventas", cliente.Entity!.App);
        Assert.Equal(5, cliente.DriftCount);
        var cols = cliente.Columns.ToDictionary(c => c.Name);
        Assert.Equal(("system", true), (cols["CreatedAt"].Status, cols["CreatedAt"].System));
        Assert.Equal("((0))", cols["IsDeleted"].Default!.Definition);
        Assert.Equal(("mismatch", "decimal(18,2)"), (cols["Saldo"].Status, cols["Saldo"].ExpectedType));
        Assert.Equal("archived", cols["Viejo"].Status);
        Assert.Equal("orphan", cols["Extra"].Status);
        Assert.Equal("ok", cols["Nombre"].Status);
        Assert.Equal(["Id"], cliente.PrimaryKey!.Columns);
    }

    [Fact]
    public void H1_CA3_cardinalidad_por_nulabilidad_y_unicidad_de_la_llave_foranea()
    {
        var (model, catalog) = Drifted();
        var withCol = catalog.Select(t => t.Name != "Cliente" ? t : t with
        {
            Columns = [.. t.Columns, Col("Proveedor", "bigint")],
        }).ToList();
        var fk = PhysicalModel.Build(DbCatalog.App, withCol, model, NoApps).Tables.Single(t => t.Name == "Cliente").ForeignKeys.Single();
        Assert.True(fk.Nullable);   // del lado de Proveedor: cero o uno
        Assert.False(fk.Unique);    // del lado de Cliente: cero o muchos

        var unique = withCol.Select(t => t.Name != "Cliente" ? t : t with
        {
            Columns = [.. t.Columns.Where(c => c.Name != "Proveedor"), Col("Proveedor", "bigint", nullable: false)],
            Indexes = [.. t.Indexes, new("UX_Cliente_Proveedor", false, true, false, null, ["Proveedor"], [])],
        }).ToList();
        fk = PhysicalModel.Build(DbCatalog.App, unique, model, NoApps).Tables.Single(t => t.Name == "Cliente").ForeignKeys.Single();
        Assert.False(fk.Nullable);
        Assert.True(fk.Unique);
    }

    [Fact]
    public void H1_CA8_filtros_por_modulo_y_texto_en_el_servidor_con_tablas_externas()
    {
        var (model, catalog) = Drifted();
        var ventas = PhysicalModel.Build(DbCatalog.App, catalog, model, NoApps, app: "ventas");
        Assert.Equal(["Cliente"], ventas.Tables.Select(t => t.Name));
        // La tabla referida fuera del filtro se nombra como externa; la deriva es solo la del filtro.
        Assert.Equal([new DbExternal("app", "Proveedor")], ventas.External);
        Assert.Contains(ventas.Drift, d => d.Table == "Nuevo");
        Assert.DoesNotContain(ventas.Drift, d => d.Table == "Huerfana");

        // Texto en la columna, sin distinguir acentos ni mayúsculas.
        Assert.Equal(["Cliente"], PhysicalModel.Build(DbCatalog.App, catalog, model, NoApps, q: "EXTRA").Tables.Select(t => t.Name));
        Assert.Equal(["Proveedor"], PhysicalModel.Build(DbCatalog.App, catalog, model, NoApps, q: "próveedor").Tables
            .Where(t => t.Name == "Proveedor").Select(t => t.Name));
        Assert.Empty(PhysicalModel.Build(DbCatalog.App, catalog, model, NoApps, q: "zzz").Tables);
    }

    [Fact]
    public void H1_CA6_el_esquema_meta_no_se_liga_ni_reporta_deriva()
    {
        var (model, _) = Drifted();
        var db = PhysicalModel.Build(DbCatalog.Meta, [new("meta", "Cliente", [Col("Id", "int", false, true), Col("Raro", "int")], [], [])], model, NoApps);
        Assert.Null(db.Tables.Single().Entity);
        Assert.Empty(db.Drift);
        Assert.All(db.Tables.Single().Columns, c => Assert.Equal("ok", c.Status));
    }

    // ---------------- H1 CA7: DDL ----------------

    [Fact]
    public void H1_CA7_ddl_de_estructura_con_llaves_valores_por_defecto_e_indices()
    {
        var (model, catalog) = Drifted();
        var t = PhysicalModel.Build(DbCatalog.App, catalog, model, NoApps).Tables.Where(x => x.Name is "Cliente" or "Proveedor").ToList();
        var withInclude = t.Select(x => x.Name != "Proveedor" ? x : x with
        {
            Indexes = [.. x.Indexes, new DbIndex("IX_Proveedor_Nombre", false, false, false, null, ["Nombre"], ["CreatedAt"])],
        }).ToList();
        var sql = DdlScript.For("app", withInclude, new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
        Assert.Contains("CREATE TABLE [app].[Cliente] (", sql);
        Assert.Contains("[Id] bigint IDENTITY(1,1) NOT NULL", sql);
        Assert.Contains("[IsDeleted] bit NOT NULL CONSTRAINT [DF_Cliente_IsDeleted] DEFAULT ((0))", sql);
        Assert.Contains("[Saldo] decimal(18,4) NULL", sql);
        Assert.Contains("CONSTRAINT [PK_Cliente] PRIMARY KEY ([Id])", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_Cliente_Nombre] ON [app].[Cliente] ([Nombre]) WHERE ([Nombre] IS NOT NULL AND [IsDeleted]=(0));", sql);
        Assert.Contains("CREATE INDEX [IX_Proveedor_Nombre] ON [app].[Proveedor] ([Nombre]) INCLUDE ([CreatedAt]);", sql);
        // Las llaves foráneas al final, después de crear todas las tablas.
        Assert.True(sql.IndexOf("ADD CONSTRAINT [FK_Cliente_Proveedor] FOREIGN KEY ([Proveedor]) REFERENCES [app].[Proveedor] ([Id]);", StringComparison.Ordinal)
                    > sql.IndexOf("CREATE TABLE [app].[Proveedor]", StringComparison.Ordinal));
        Assert.Contains("2026-10-24 12:00 UTC", sql);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void H7_CA2_ddl_escapa_los_nombres_como_quotename()
    {
        Assert.Equal("[a]]b]", DdlScript.Name("a]b"));
        var odd = new DbTable("meta", "Raro]; DROP TABLE x;--", null, [new DbColumn("c]", "int", true, false, null, null, false, null, "ok", null)],
            null, [], [], 0);
        var sql = DdlScript.For("meta", [odd], DateTime.UtcNow);
        Assert.Contains("CREATE TABLE [meta].[Raro]]; DROP TABLE x;--] (", sql);
        Assert.Contains("[c]]] int NULL", sql);
    }

    // ---------------- H4: ¿dónde se usa? ----------------

    private static (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, DashboardDef> Boards) Modules() =>
        EngineCapabilitiesTests.Installed("compras.json", "inventario.json", "contabilidad.json", "costeo.json") is var (m, b, _) ? (m, b) : default;

    [Fact]
    public void H4_CA2_ruta_de_tres_niveles_menciona_un_campo_por_salto()
    {
        var (model, boards) = Modules();
        var index = UsageIndex.For(model, boards);
        // Inventario 1.7.0: RecepcionPartida.CostoUnitario usa Recepcion.OrdenCompra.Moneda.Clave.
        var clave = index.Of("Moneda", "Clave");
        Assert.Contains(clave, u => u.Entity == "RecepcionPartida" && u.Kind == UsageKinds.Formula && u.Expression!.Contains("Recepcion.OrdenCompra.Moneda.Clave"));
        Assert.Contains(index.Of("OrdenCompra", "Moneda"), u => u.Entity == "RecepcionPartida" && u.Kind == UsageKinds.Formula);
        Assert.Contains(index.Of("Recepcion", "OrdenCompra"), u => u.Entity == "RecepcionPartida" && u.Kind == UsageKinds.Formula);
        Assert.Contains(index.Of("RecepcionPartida", "Recepcion"), u => u.Kind == UsageKinds.Formula);
        // Sin campo: todo lo que menciona el catálogo o alguno de sus campos.
        Assert.Contains(index.Of("Moneda"), u => u.Target.Field == "Clave");
    }

    [Fact]
    public void H4_CA1_sum_unitcost_automatizaciones_tableros_y_referencias()
    {
        var (model, boards) = Modules();
        var index = UsageIndex.For(model, boards);
        // SUM(Partidas.Importe) en la orden menciona la partida y su campo.
        var lines = model["OrdenCompra"].ActiveFields.First(f => f.Type == FieldTypes.Lines);
        Assert.Contains(index.Of(lines.ChildEntity!), u => u.Entity == "OrdenCompra" && u.Kind == UsageKinds.Formula && u.Expression!.Contains("SUM("));
        // Una automatización que actúa sobre otro catálogo lo menciona (destino y campos asignados).
        Assert.Contains(index.Of("Existencia"), u => u.Kind == UsageKinds.Automation && u.Entity != "Existencia");
        // Las referencias que apuntan al catálogo.
        Assert.Contains(index.Of("Proveedor"), u => u.Entity == "OrdenCompra" && u.Kind == UsageKinds.Reference);
        // Los tableros con tarjetas del catálogo.
        foreach (var w in boards.Values.Where(d => !d.IsArchived).SelectMany(d => d.Widgets))
            Assert.Contains(index.Of(w.Entity), u => u.Kind == UsageKinds.Dashboard);
        // El índice se arma una vez por versión del modelo.
        Assert.Same(index, UsageIndex.For(model, boards));
    }

    [Fact]
    public void H4_CA1_todos_los_sitios_de_un_catalogo_en_su_ambito()
    {
        var cliente = E("Cliente", null, false, F("Nombre"), F("Credito", "decimal"), F("Vendedor", "user"), F("Zona"), F("Activo", "boolean"));
        var pedido = E("Pedido", null, false,
            F("Cliente", "reference", "Cliente") with { LookupFilter = new LookupFilter("Zona", "Zona") },
            F("Zona"), F("Total", "decimal") with { DefaultValue = "Cliente.Credito" },
            F("Fecha", "date"), F("CostoRef", "formula") with { ResultType = "decimal", Formula = "UNITCOST('Cliente', Total, Zona)" }) with
        {
            DisplayField = "Zona",
            Rules = [new BusinessRule { Name = "Limite", Message = "m", Assert = "Total <= Cliente.Credito", When = "Cliente.Activo", Field = "Total", Severity = "warning" }],
            Workflow = new Workflow
            {
                Initial = "Borrador",
                States = [new WorkflowState { Name = "Borrador", Label = "Borrador" }, new WorkflowState { Name = "Listo", Label = "Listo" }],
                Transitions = [new WorkflowTransition { Name = "Cerrar", Label = "Cerrar", From = ["Borrador"], To = "Listo",
                    When = "NOT ISBLANK(Cliente.Nombre)", RequiredFields = ["Fecha"], Set = [new("Zona", "Cliente.Zona")] }],
            },
            Layout = new Layout { Sections = [new LayoutSection { Name = "S", Label = "Datos", VisibleWhen = "Cliente.Activo",
                Fields = [new LayoutItem { Field = "Cliente" }, new LayoutItem { Field = "Total", VisibleWhen = "Zona = 'Norte'" }] }] },
            Security = new SecurityPolicy
            {
                Owner = null,
                Fields = [new FieldSecurity { Field = "Total", Read = ["Ventas"] }],
                Records = [new RecordGrant { Roles = ["Ventas"], Where = "Cliente.Vendedor = CURRENTUSER()" }],
            },
            Views = [new ViewDef { Name = "V", Label = "Por zona", Columns = ["Zona"], Sort = "Fecha", Filters = [new ViewFilter { Field = "Cliente", Op = "blank" }] }],
            Reports = [new ReportDef { Name = "R", Label = "Totales", GroupBy = [new ReportGroup { Field = "Cliente" }],
                Measures = [new ReportMeasure { Fn = "sum", Field = "Total" }], Filter = "Total > 0" }],
            Webhooks = [new WebhookDef { Name = "W", Label = "Aviso", Role = "Ventas", When = "Cliente.Activo", Fields = ["Total"] }],
            Schedules = [new ScheduleDef { Name = "T", Label = "Diaria", Every = "day", Role = "Ventas", Where = "Cliente.Activo",
                Actions = [new AutomationAction { Type = "update", Target = "Cliente", Set = [new("Credito", "Total")] }] }],
        };
        var model = Model(cliente, pedido);
        var index = UsageIndex.For(model, NoBoards);
        var kinds = index.Of("Cliente").Select(u => u.Kind).ToHashSet();
        Assert.Superset(new HashSet<string> { UsageKinds.Reference, UsageKinds.Default, UsageKinds.Rule, UsageKinds.Transition, UsageKinds.Layout,
            UsageKinds.Access, UsageKinds.Webhook, UsageKinds.Schedule, UsageKinds.Lookup }, kinds);
        Assert.Contains(index.Of("Pedido", "Zona"), u => u.Kind == UsageKinds.Display);
        // UNITCOST('Cliente', …) menciona el catálogo del saldo valuado.
        Assert.Contains(index.Of("Cliente"), u => u.Kind == UsageKinds.Formula && u.Target.Field is null && u.Expression!.StartsWith("UNITCOST"));
        Assert.Contains(index.Of("Pedido", "Zona"), u => u.Kind == UsageKinds.View);
        Assert.Contains(index.Of("Pedido", "Fecha"), u => u.Kind == UsageKinds.Transition && u.Location.Contains("obligatorio") && u.Tab == "proceso");
        Assert.Contains(index.Of("Pedido", "Fecha"), u => u.Kind == UsageKinds.View);
        Assert.Contains(index.Of("Pedido", "Total"), u => u.Kind == UsageKinds.Report && u.Location.Contains("medida"));
        Assert.Contains(index.Of("Pedido", "Total"), u => u.Kind == UsageKinds.Access && u.Location == "Seguridad por campo");
        Assert.Contains(index.Of("Pedido", "Total"), u => u.Kind == UsageKinds.Layout && u.Tab == "formulario");
        Assert.Contains(index.Of("Cliente", "Vendedor"), u => u.Kind == UsageKinds.Access && u.Expression == "Cliente.Vendedor = CURRENTUSER()");
        // El filtro del buscador: el campo del catálogo referido y el origen en el propio registro.
        Assert.Contains(index.Of("Cliente", "Zona"), u => u.Kind == UsageKinds.Lookup);
        Assert.Contains(index.Of("Pedido", "Zona"), u => u.Kind == UsageKinds.Lookup);
        // La tarea asigna en el destino (Cliente.Credito) con un valor del propio registro (Pedido.Total).
        Assert.Contains(index.Of("Cliente", "Credito"), u => u.Kind == UsageKinds.Schedule && u.Location.Contains("acción 1"));
        Assert.Contains(index.Of("Pedido", "Total"), u => u.Kind == UsageKinds.Schedule);
        Assert.Contains(index.Of("Cliente", "Credito"), u => u.Kind == UsageKinds.Rule && u.Location.StartsWith("Aviso"));
    }

    [Fact]
    public void H4_CA2_una_expresion_invalida_no_rompe_el_indice()
    {
        var model = Model(E("A", null, false, F("X") with { Formula = "1 +", Type = "formula", ResultType = "decimal" }, F("Y") with { DefaultValue = "X" }));
        var index = UsageIndex.For(model, NoBoards);
        Assert.Single(index.Of("A", "X"));
        Assert.Equal(UsageKinds.Default, index.Of("A", "X")[0].Kind);
    }

    [Fact]
    public void H4_CA2_partidas_capture_y_libro_en_su_ambito()
    {
        var linea = E("PolizaLinea", null, false, F("Poliza", "parent", "Poliza"), F("Cargo", "decimal"), F("Abono", "decimal"), F("Producto"));
        var periodo = E("Periodo", null, false, F("Inicio", "date"), F("Fin", "date"), F("Cerrado", "boolean"));
        var poliza = E("Poliza", null, false, F("Fecha", "date"), F("Partidas", "lines") with { ChildEntity = "PolizaLinea" }, F("Total", "decimal")) with
        {
            Ledger = new LedgerDef { Lines = "Partidas", Debit = "Cargo", Credit = "Abono", Date = "Fecha",
                Period = new LedgerPeriod { Entity = "Periodo", Start = "Inicio", End = "Fin", Open = "NOT Cerrado" } },
            Automations = [new Automation { Name = "A", Label = "Copia", On = "create",
                Actions = [new AutomationAction { Type = "create", Lines = "Partidas", Entity = "Periodo", Set = [new("Inicio", "Poliza.Fecha")],
                    Capture = [new("Producto", "Fin")] }] }],
        };
        var index = UsageIndex.For(Model(linea, periodo, poliza), NoBoards);
        Assert.Contains(index.Of("PolizaLinea", "Cargo"), u => u.Kind == UsageKinds.Ledger);
        Assert.Contains(index.Of("Periodo", "Cerrado"), u => u.Kind == UsageKinds.Ledger && u.Expression == "NOT Cerrado");
        Assert.Contains(index.Of("Periodo", "Inicio"), u => u.Kind == UsageKinds.Automation);
        // Con lines el ámbito es la partida: Poliza.Fecha sube al documento por el campo padre.
        Assert.Contains(index.Of("PolizaLinea", "Poliza"), u => u.Kind == UsageKinds.Automation);
        Assert.Contains(index.Of("Poliza", "Fecha"), u => u.Kind == UsageKinds.Automation);
        // capture: el campo se asigna en la partida y el valor se calcula en el registro creado.
        Assert.Contains(index.Of("PolizaLinea", "Producto"), u => u.Kind == UsageKinds.Automation);
        Assert.Contains(index.Of("Periodo", "Fin"), u => u.Kind == UsageKinds.Automation);
    }

    // ---------------- H5: flujo de automatizaciones ----------------

    [Fact]
    public void H5_CA1_CA2_CA3_flujo_con_modos_y_ciclos()
    {
        var a = E("A", null, false, F("B", "reference", "B"), F("N", "decimal")) with
        {
            Automations = [new Automation { Name = "AlCrear", Label = "Al crear A", On = "create",
                Actions = [new AutomationAction { Type = "update", Target = "B", Set = [new("N", "N")] }] }],
            Schedules = [new ScheduleDef { Name = "Diaria", Label = "Diaria", Every = "day", At = "06:00", Role = "R", Where = "N > 0",
                Actions = [new AutomationAction { Type = "update", Set = [new("N", "0")] }] }],
        };
        var b = E("B", null, false, F("N", "decimal")) with
        {
            Automations = [new Automation { Name = "AlEditar", Label = "Al editar B", On = "update", Mode = "async",
                Actions = [new AutomationAction { Type = "create", Entity = "A", Set = [new("N", "N")] }] }],
        };
        var c = E("C", null, false, F("A", "reference", "A")) with
        {
            Automations = [new Automation { Name = "X", Label = "X", On = "create", Actions = [new AutomationAction { Type = "update", Target = "A", Set = [new("N", "1")] }] }],
        };
        var flow = ModelExplorer.Flow(Model(a, b, c));
        var ab = flow.Edges.Single(e => e is { From: "A", To: "B" });
        Assert.Equal(("sync", "update", "Al crear", true, true), (ab.Mode, ab.Action, ab.Trigger, ab.Triggers, ab.Cycle));
        var ba = flow.Edges.Single(e => e is { From: "B", To: "A" });
        Assert.Equal(("async", "create", true, true), (ba.Mode, ba.Action, ba.Triggers, ba.Cycle));
        var sched = flow.Edges.Single(e => e.Mode == "schedule");
        Assert.Equal(("A", "A", "Cada día a las 06:00", false), (sched.From, sched.To, sched.Trigger, sched.Cycle));
        // C → A: A no tiene automatizaciones al editar, así que no dispara ni cierra ciclo.
        var ca = flow.Edges.Single(e => e.From == "C");
        Assert.Equal((false, false), (ca.Triggers, ca.Cycle));
        Assert.Equal(["A", "B", "C"], flow.Nodes.Select(n => n.Name));
    }

    // ---------------- H6: seguridad y navegación ----------------

    [Fact]
    public void H6_CA1_matriz_con_herencia_alcance_y_campos_ocultos()
    {
        var pedido = E("Pedido", "Ventas", false, F("Total", "decimal"), F("Costo", "decimal")) with
        {
            Security = new SecurityPolicy
            {
                Fields = [new FieldSecurity { Field = "Costo", Read = ["Gerente"] }, new FieldSecurity { Field = "Total", Edit = ["Gerente"] }],
                Records = [new RecordGrant { Roles = ["Vendedor"], Scope = "own" }, new RecordGrant { Roles = ["Gerente"], Scope = "unitTree", Access = "read" }],
            },
        };
        var roles = new List<RoleInfo> { new(1, "Vendedor", null, false), new(2, "Gerente", null, false), new(3, "Admin", null, true) };
        var perms = new List<(int, PermRow)>
        {
            (1, new PermRow("Pedido", true, true, false, false)),
            (2, new PermRow("Pedido", false, false, true, false)),
        };
        // Gerente hereda de Vendedor.
        var m = ModelExplorer.Security(Model(pedido), roles, perms, [(2, 1)]);
        var vendedor = m.Cells.Single(c => c.Role == "Vendedor");
        Assert.Equal((true, true, false, false), (vendedor.Read, vendedor.Create, vendedor.Update, vendedor.Delete));
        Assert.Equal(["Costo"], vendedor.Hidden);
        Assert.Equal(["Total"], vendedor.ReadOnly);
        Assert.Equal([new ScopeGrant("write", "own", null)], vendedor.Scopes);
        var gerente = m.Cells.Single(c => c.Role == "Gerente");
        Assert.Equal((true, true, true, false), (gerente.Read, gerente.Create, gerente.Update, gerente.Delete));
        Assert.Equal(["Vendedor"], gerente.InheritedFrom);
        Assert.Empty(gerente.Hidden);
        Assert.Equal(2, gerente.Scopes.Count); // el suyo y el que hereda
        var admin = m.Cells.Single(c => c.Role == "Admin");
        Assert.True(admin.Read && admin.Delete);
        Assert.Empty(admin.Hidden);
        Assert.Equal(["Vendedor"], m.Roles.Single(r => r.Name == "Gerente").Inherits);
    }

    [Fact]
    public void H6_CA2_arbol_de_navegacion_con_layout_y_derivado()
    {
        var pedido = E("Pedido", "Ventas", false, F("Folio", "autonumber"), F("Total", "decimal"), F("Partidas", "lines") with { ChildEntity = "PedidoLinea" }) with
        {
            Layout = new Layout { Sections = [new LayoutSection { Name = "A", Label = "Encabezado", Tab = "General", Fields = [new LayoutItem { Field = "Folio" }] }] },
        };
        var linea = E("PedidoLinea", null, false, F("Pedido", "parent", "Pedido"), F("Cantidad", "decimal"));
        var cliente = E("Cliente", null, false, F("Nombre"));
        var nav = ModelExplorer.Navigation(Model(pedido, linea, cliente), NoApps);
        var ventas = nav.Single(a => a.Name == "Ventas");
        var p = ventas.Entities.Single();
        Assert.True(p.Layout);
        Assert.Equal(("Encabezado", "General", "Folio"), (p.Sections[0].Label, p.Sections[0].Tab, p.Sections[0].Fields.Single().Name));
        Assert.Equal(["PedidoLinea"], p.Lines);
        // Sin layout, una sección con los campos en su orden. Las partidas no son una pantalla del menú.
        var general = nav.Single(a => a.Name == "General").Entities.Single();
        Assert.Equal("Cliente", general.Name);
        Assert.False(general.Layout);
        Assert.Equal(["Nombre"], general.Sections.Single().Fields.Select(f => f.Name));
    }

    // ---------------- H2 y H3 ----------------

    [Fact]
    public void H2_CA1_relaciones_con_partidas_padre_y_usuario()
    {
        var (model, _) = Modules();
        var g = ModelExplorer.Relations(model, NoApps);
        var orden = model["OrdenCompra"];
        var lines = orden.ActiveFields.First(f => f.Type == FieldTypes.Lines);
        Assert.Contains(g.Edges, e => e is { From: "OrdenCompra", Kind: "lines" } && e.To == lines.ChildEntity);
        Assert.Contains(g.Edges, e => e.From == lines.ChildEntity && e is { To: "OrdenCompra", Kind: "parent" });
        Assert.Contains(g.Edges, e => e is { From: "OrdenCompra", To: "Proveedor", Kind: "reference" });
        Assert.Equal("Compras", g.Nodes.Single(n => n.Name == lines.ChildEntity).App); // la partida va con su documento
        Assert.Contains(g.Nodes.Single(n => n.Name == "OrdenCompra").Fields, f => f.Autonumber);
        Assert.All(ModelExplorer.Relations(model, NoApps, app: "Inventario").Nodes, n => Assert.Equal("Inventario", n.App));
    }

    [Fact]
    public void H3_CA1_paquetes_que_crean_o_modifican_un_catalogo()
    {
        var packages = new[] { "compras.json", "costeo.json" }.Select(EngineCapabilitiesTests.Package).ToList();
        var refs = ModelExplorer.Packages("Producto", packages, new Dictionary<string, string>(Ci) { ["compras"] = "1.9.0" });
        Assert.Equal("compras", refs[0].Id);
        Assert.True(refs[0].Created);
        Assert.Equal("1.9.0", refs[0].InstalledVersion);
        Assert.Contains("base", refs[0].Versions);
        // Costeo modifica Producto (su método de costeo) sin crearlo.
        Assert.Contains(refs, r => r.Id == "costeo" && !r.Created && r.InstalledVersion is null);
    }
}
