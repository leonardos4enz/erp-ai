using System.Text.RegularExpressions;
using Erp.Api.Data;
using Erp.Api.Metadata;
using Erp.Api.OData;

namespace Erp.Tests;

/// <summary>
/// Regresión (revisión exploratoria 022, F-01): ordenar por <c>Id</c> repetía la columna del desempate
/// (<c>ORDER BY t.[Id] DESC, t.[Id] DESC</c>) y SQL Server respondía con error (500), en la API de datos y en OData.
/// </summary>
public class SortTests
{
    private static readonly EntityDef Pedido = new()
    {
        Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos",
        Fields =
        [
            new FieldDef { Name = "Cliente", Label = "Cliente", Type = FieldTypes.Text, DisplayOrder = 1 },
            new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date, DisplayOrder = 2 },
        ],
    };

    /// <summary>Columnas del ORDER BY, sin el sentido.</summary>
    private static List<string> Columns(IEnumerable<string> parts) =>
        parts.Select(p => Regex.Replace(p.Trim(), @"\s+(ASC|DESC)$", "", RegexOptions.IgnoreCase)).ToList();

    [Theory]
    [InlineData("Id", "asc")]
    [InlineData("id", "desc")]
    [InlineData("Cliente", "asc")]
    [InlineData("createdAt", null)]
    [InlineData(null, null)]
    [InlineData("Inexistente", "asc")]
    public void La_API_de_datos_no_repite_columnas_en_el_orden(string? sort, string? dir)
    {
        var clause = RecordService.ResolveSort(Pedido, sort, dir, _ => true);
        var columns = Columns(clause.Split(','));
        Assert.Equal(columns.Count, columns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("t.[Id]", columns); // siempre termina en un orden estable
    }

    [Fact]
    public void Ordenar_por_Id_respeta_el_sentido()
    {
        Assert.Equal("t.[Id] ASC", RecordService.ResolveSort(Pedido, "Id", "asc", _ => true));
        Assert.Equal("t.[Id] DESC", RecordService.ResolveSort(Pedido, "id", "desc", _ => true));
        Assert.Equal("t.[Cliente] ASC, t.[Id] ASC", RecordService.ResolveSort(Pedido, "Cliente", "asc", _ => true));
    }

    private static readonly IReadOnlyDictionary<string, ODataProperty> ODataProps = new Dictionary<string, ODataProperty>(StringComparer.Ordinal)
    {
        ["Id"] = new("Id", "t.[Id]", EdmKind.Int64, null),
        ["Cliente"] = new("Cliente", "t.[Cliente]", EdmKind.String, Pedido.Fields[0]),
    };

    [Theory]
    [InlineData("Id")]
    [InlineData("Id desc")]
    [InlineData("Cliente,Id desc")]
    [InlineData("Cliente,Cliente desc")]
    public void OData_no_repite_columnas_en_el_orden(string orderby)
    {
        var order = orderby.Split(',').Select(x => x.Split(' ')).Select(x => new ODataOrder(x[0], x.Length > 1 && x[1] == "desc")).ToList();
        var columns = Columns(RecordService.ODataOrderClause(Pedido, order, ODataProps));
        Assert.Equal(columns.Count, columns.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("t.[Id]", columns[^1]);
    }

    [Fact]
    public void OData_por_Id_respeta_el_sentido_y_el_primer_criterio_gana()
    {
        Assert.Equal(["t.[Id] DESC"], RecordService.ODataOrderClause(Pedido, [new("Id", true)], ODataProps));
        Assert.Equal(["t.[Cliente] ASC", "t.[Id] ASC"],
            RecordService.ODataOrderClause(Pedido, [new("Cliente", false), new("Cliente", true)], ODataProps));
    }
}
