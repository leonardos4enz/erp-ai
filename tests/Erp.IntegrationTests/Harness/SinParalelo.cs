namespace Erp.IntegrationTests.Harness;

/// <summary>
/// Spec 030 (revisión adversarial F-G): las clases que miden tiempos corren solas, sin las demás clases (cada una con su
/// anfitrión y sus bandejas) compitiendo por el mismo SQL Server.
/// </summary>
[CollectionDefinition(Nombre, DisableParallelization = true)]
public sealed class SinParalelo
{
    public const string Nombre = "Sin paralelo (mide tiempos)";
}
