using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

public class IdentifierTests
{
    [Theory]
    [InlineData("Empaque")]
    [InlineData("Cliente_2")]
    [InlineData("ab")]
    public void Accepts_safe_identifiers(string name) => Assert.True(Identifiers.IsValid(name, out _));

    [Theory]
    [InlineData("")]
    [InlineData("a")]                          // muy corto
    [InlineData("1Cliente")]                   // empieza con número
    [InlineData("Cliente]; DROP TABLE x;--")]  // intento de inyección
    [InlineData("Cli ente")]
    [InlineData("Empaqué")]                    // acentos fuera de lista blanca
    [InlineData("a__label")]                   // reservado por el motor
    [InlineData("SELECT")]
    [InlineData("Nombre'")]
    public void Rejects_unsafe_identifiers(string name) => Assert.False(Identifiers.IsValid(name, out _));

    [Fact]
    public void Rejects_names_longer_than_limit() =>
        Assert.False(Identifiers.IsValid(new string('a', Identifiers.MaxLength + 1), out _));

    [Theory]
    [InlineData("Id")]
    [InlineData("rowver")]
    [InlineData("CreatedBy")]
    public void Rejects_system_columns_as_field_names(string name) => Assert.False(Identifiers.IsValidFieldName(name, out _));

    [Fact]
    public void Quote_throws_on_invalid_input_as_defense_in_depth() =>
        Assert.Throws<InvalidOperationException>(() => Identifiers.Quote("x]; DROP TABLE y"));
}

public class DdlGeneratorTests
{
    [Fact]
    public void Create_table_includes_system_columns_and_quoted_names()
    {
        var sql = string.Join("\n", DdlGenerator.CreateTable("Empaque",
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, MaxLength = 100, Unique = true },
            new FieldDef { Name = "Capacidad", Label = "Capacidad", Type = FieldTypes.Decimal, Precision = 10, Scale = 3 },
        ]));

        Assert.Contains("CREATE TABLE [app].[Empaque]", sql);
        Assert.Contains("[Nombre] nvarchar(100) NULL", sql);
        Assert.Contains("[Capacidad] decimal(10,3) NULL", sql);
        Assert.Contains("[RowVer] rowversion NOT NULL", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_Empaque_Nombre]", sql);
    }

    [Fact]
    public void Reference_creates_foreign_key_and_index()
    {
        var sql = string.Join("\n", DdlGenerator.AddColumn("Pedido",
            new FieldDef { Name = "Cliente", Label = "Cliente", Type = FieldTypes.Reference, ReferenceEntity = "Cliente" }));
        Assert.Contains("REFERENCES [app].[Cliente] ([Id])", sql);
        Assert.Contains("CREATE INDEX [IX_Pedido_Cliente]", sql);
    }
}

public class PlanCompilerTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Administrador", "Usuario" };
    private static readonly Dictionary<string, EntityDef> Empty = new(StringComparer.OrdinalIgnoreCase);

    private static CreateEntityOp Empaque(params FieldDef[] fields) => new()
    {
        Name = "Empaque", Label = "Empaque", LabelPlural = "Empaques", Icon = "package",
        Fields = fields.Length > 0 ? fields : [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }],
    };

    [Fact]
    public void Valid_plan_produces_sql_and_resulting_model()
    {
        var result = PlanCompiler.Compile(new ChangePlan { Operations = [Empaque()] }, Empty, Roles);
        Assert.True(result.Preview.Valid);
        Assert.Single(result.Sql, s => s.StartsWith("CREATE TABLE [app].[Empaque]"));
        Assert.True(result.ResultingModel.ContainsKey("Empaque"));
    }

    [Fact]
    public void Injection_in_field_name_is_rejected_and_no_sql_generated()
    {
        var plan = new ChangePlan { Operations = [Empaque(new FieldDef { Name = "x] nvarchar(1)); DROP TABLE meta.[User];--", Label = "x", Type = FieldTypes.Text })] };
        var result = PlanCompiler.Compile(plan, Empty, Roles);
        Assert.False(result.Preview.Valid);
        Assert.Empty(result.Sql);
    }

    [Fact]
    public void Unknown_type_and_role_are_rejected()
    {
        var plan = new ChangePlan { Operations = [Empaque(new FieldDef
        {
            Name = "Nombre", Label = "Nombre", Type = "varchar(max)", RequiredForRoles = ["Hacker"],
        })] };
        Assert.False(PlanCompiler.Compile(plan, Empty, Roles).Preview.Valid);
    }

    [Fact]
    public void Later_operations_see_entities_created_earlier_in_the_same_plan()
    {
        var plan = new ChangePlan { Operations =
        [
            Empaque(),
            new AddFieldOp { Entity = "Empaque", Field = new FieldDef { Name = "Material", Label = "Material", Type = FieldTypes.Choice, Options = ["Cartón", "Plástico"] } },
        ] };
        var result = PlanCompiler.Compile(plan, Empty, Roles);
        Assert.True(result.Preview.Valid, string.Join("; ", result.Preview.Errors.Select(e => e.Message)));
        Assert.Equal(2, result.ResultingModel["Empaque"].Fields.Count);
    }

    [Fact]
    public void Required_for_role_rule_is_metadata_only()
    {
        var model = PlanCompiler.Compile(new ChangePlan { Operations = [Empaque()] }, Empty, Roles).ResultingModel;
        var plan = new ChangePlan { Operations = [new UpdateFieldOp { Entity = "Empaque", Field = "Nombre", RequiredForRoles = ["Administrador"] }] };
        var result = PlanCompiler.Compile(plan, model, Roles);
        Assert.True(result.Preview.Valid);
        Assert.Empty(result.Sql);
        Assert.Equal(["Administrador"], result.ResultingModel["Empaque"].FindField("Nombre")!.RequiredForRoles!);
    }

    [Fact]
    public void Cannot_archive_entity_still_referenced()
    {
        var plan = new ChangePlan { Operations =
        [
            Empaque(),
            new CreateEntityOp { Name = "Producto", Label = "Producto", LabelPlural = "Productos", Fields =
                [new FieldDef { Name = "Empaque", Label = "Empaque", Type = FieldTypes.Reference, ReferenceEntity = "Empaque" }] },
            new ArchiveEntityOp { Entity = "Empaque" },
        ] };
        var result = PlanCompiler.Compile(plan, Empty, Roles);
        Assert.False(result.Preview.Valid);
        Assert.Contains(result.Preview.Errors, e => e.Operation == 2);
    }

    [Fact]
    public void Adding_a_field_with_the_name_of_an_archived_one_is_rejected()
    {
        var model = PlanCompiler.Compile(new ChangePlan { Operations =
        [
            Empaque(new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }, new FieldDef { Name = "Viejo", Label = "Viejo", Type = FieldTypes.Text }),
            new ArchiveFieldOp { Entity = "Empaque", Field = "Viejo" },
        ] }, Empty, Roles).ResultingModel;
        var plan = new ChangePlan { Operations = [new AddFieldOp { Entity = "Empaque", Field = new FieldDef { Name = "Viejo", Label = "Viejo", Type = FieldTypes.Text } }] };
        Assert.False(PlanCompiler.Compile(plan, model, Roles).Preview.Valid);
    }

    [Fact]
    public void Plan_json_is_deserialized_polymorphically_like_an_llm_would_send_it()
    {
        const string json = """
            { "description": "Catálogo de empaques", "operations": [
              { "name": "Empaque", "op": "createEntity", "label": "Empaque", "labelPlural": "Empaques",
                "fields": [ { "name": "Nombre", "label": "Nombre", "type": "text", "requiredForRoles": ["Administrador"] } ] } ] }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };
        var plan = JsonSerializer.Deserialize<ChangePlan>(json, options)!;
        Assert.IsType<CreateEntityOp>(Assert.Single(plan.Operations));
        Assert.True(PlanCompiler.Compile(plan, Empty, Roles).Preview.Valid);
    }
}

public class ValueConversionTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Choice_rejects_values_outside_options()
    {
        var f = new FieldDef { Name = "Material", Label = "Material", Type = FieldTypes.Choice, Options = ["Cartón"] };
        Assert.Equal("Cartón", RecordService.ConvertValue(f, J("\"Cartón\"")));
        Assert.ThrowsAny<Exception>(() => RecordService.ConvertValue(f, J("\"Acero\"")));
    }

    [Fact]
    public void Text_respects_max_length()
    {
        var f = new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, MaxLength = 5 };
        Assert.ThrowsAny<Exception>(() => RecordService.ConvertValue(f, J("\"123456\"")));
    }

    [Fact]
    public void Decimal_rejects_too_many_decimals()
    {
        var f = new FieldDef { Name = "Precio", Label = "Precio", Type = FieldTypes.Decimal, Precision = 10, Scale = 2 };
        Assert.Equal(12.5m, RecordService.ConvertValue(f, J("12.5")));
        Assert.ThrowsAny<Exception>(() => RecordService.ConvertValue(f, J("1.234")));
        Assert.ThrowsAny<Exception>(() => RecordService.ConvertValue(f, J("123456789")));
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("corta")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public void Weak_passwords_are_rejected(string pwd) => Assert.False(PasswordService.MeetsPolicy(pwd, out _));

    [Fact]
    public void Password_cannot_contain_username() =>
        Assert.False(PasswordService.MeetsPolicy("soyjuanperez2026!", out _, "juanperez"));

    [Fact]
    public void Hash_roundtrip()
    {
        var svc = new PasswordService();
        var hash = svc.Hash("una frase larga y segura");
        Assert.True(svc.Verify(hash, "una frase larga y segura", out _));
        Assert.False(svc.Verify(hash, "otra frase", out _));
        Assert.False(svc.Verify(null, "cualquier cosa", out _));
    }
}
