// Spec 014 · Pendientes menores y riesgos residuales. Spec 030: aquí queda la prueba que usa la página; las de API (H1,
// H2, H3 y H6 CA1) están en tests/Erp.IntegrationTests/PendingTests.cs, que instala Compras e Inventario en su propia base.
import { ADMIN, apiAs, expect, test } from './helpers';

test('H6 CA2: con aprobación y cambios directos a la vez, Entornos lo advierte', async ({ page }) => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const info = await (await admin.get('/api/admin/environments')).json();
  expect(info).toMatchObject({ requireApproval: true, directChangesOverride: true }); // así corre la suite
  await admin.dispose();
  await page.goto('/admin/environments');
  await expect(page.getByText('Este entorno exige aprobación, pero permite cambios directos')).toBeVisible();
  await expect(page.getByText('Environments:AllowDirectChanges')).toBeVisible();
});
