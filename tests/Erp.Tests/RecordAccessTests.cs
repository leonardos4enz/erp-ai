using Erp.Api.Data;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 013: lo que el registro dice al cliente sobre lo que se puede hacer con él.</summary>
public class RecordAccessTests
{
    private static readonly EntityDef Catalogo = new() { Name = "Oportunidad", Label = "Oportunidad", LabelPlural = "Oportunidades", Fields = [] };
    private static readonly EntityDef DelSistema = Catalogo with { Security = new SecurityPolicy { SystemManaged = true } };

    [Fact]
    public void Con_rol_y_alcance_de_escritura_se_modifica_y_elimina()
    {
        Assert.Equal(new RecordService.RecordAccess(true, true, null), RecordService.AccessOf(Catalogo, EntityAccess.Full, canWrite: true));
    }

    [Fact]
    public void Fuera_del_alcance_de_escritura_solo_se_consulta_y_se_dice_por_que()
    {
        var a = RecordService.AccessOf(Catalogo, EntityAccess.Full, canWrite: false);
        Assert.False(a.Update);
        Assert.False(a.Delete);
        Assert.Equal("Puedes consultar este registro, pero tu rol no puede modificarlo.", a.Reason);
    }

    [Fact]
    public void El_permiso_del_rol_y_el_catalogo_del_sistema_tambien_cuentan()
    {
        Assert.Equal(new RecordService.RecordAccess(true, false, null),
            RecordService.AccessOf(Catalogo, new EntityAccess(true, true, true, false), canWrite: true));
        Assert.Equal(new RecordService.RecordAccess(false, false, null),
            RecordService.AccessOf(Catalogo, new EntityAccess(true, false, false, false), canWrite: true));
        Assert.Equal(new RecordService.RecordAccess(false, false, null), RecordService.AccessOf(DelSistema, EntityAccess.Full, canWrite: true));
    }
}
