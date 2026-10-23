using Microsoft.Data.SqlClient;

namespace Erp.Api.Infrastructure;

public sealed class Db(IConfiguration config)
{
    private readonly string _cs = config.GetConnectionString("Erp")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:Erp (usa user-secrets o variable de entorno ConnectionStrings__Erp).");

    public string ConnectionString => _cs;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        return cn;
    }
}
