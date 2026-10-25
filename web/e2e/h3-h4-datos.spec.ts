// Spec 001 · H3 (reglas por rol) y H4 (captura: buscar, editar, conflicto, eliminar).
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

test.beforeAll(async () => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  await applyPlan(api, {
    description: 'Catálogo de empaques (UI)',
    operations: [{
      op: 'createEntity', name: 'Empaque', label: 'Empaque', labelPlural: 'Empaques', icon: 'package',
      fields: [
        { name: 'Clave', label: 'Clave', type: 'text', maxLength: 20, unique: true, required: true },
        { name: 'Nombre', label: 'Nombre', type: 'text', requiredForRoles: ['Administrador'] },
        { name: 'Material', label: 'Material', type: 'choice', options: ['Cartón', 'Plástico'] },
        { name: 'Activo', label: 'Activo', type: 'boolean' },
      ],
    }],
  });
});

test('H3: "Nombre" es obligatorio para el rol Administrador', async ({ page }) => {
  await page.goto('/c/Empaque/new');
  await expect(page.getByText('Obligatorio para tu rol')).toBeVisible();
  await page.getByLabel('Clave').fill('CJ-01');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Este campo es obligatorio')).toBeVisible();
  await expect(page.getByLabel('Nombre')).toBeFocused();
  await expectAccessible(page);
});

test('H4: crear, buscar y editar registros', async ({ page }) => {
  await page.goto('/c/Empaque/new');
  await page.getByLabel('Clave').fill('CJ-01');
  await page.getByLabel('Nombre').fill('Caja chica');
  await page.getByLabel('Material').selectOption('Cartón');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();

  await page.goto('/c/Empaque/new');
  await page.getByLabel('Clave').fill('CJ-02');
  await page.getByLabel('Nombre').fill('Bolsa grande');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();

  await page.goto('/c/Empaque');
  await page.getByLabel('Buscar').fill('bolsa');
  await expect(page.getByRole('cell', { name: 'Bolsa grande' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Caja chica' })).toBeHidden();
  await expectAccessible(page);

  await page.getByRole('cell', { name: 'Bolsa grande' }).click();
  await page.getByLabel('Nombre').fill('Bolsa grande reforzada');
  await page.getByRole('button', { name: 'Guardar cambios' }).click();
  await expect(page.getByText('Cambios guardados')).toBeVisible();
});

test('H4: clave duplicada muestra el error junto al campo', async ({ page }) => {
  await page.goto('/c/Empaque/new');
  await page.getByLabel('Clave').fill('CJ-01');
  await page.getByLabel('Nombre').fill('Duplicada');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText(/Ya existe un registro con ese valor de Clave/)).toBeVisible();
});

test('H4: dos personas editando el mismo registro ven un aviso de conflicto', async ({ page, browser }) => {
  await page.goto('/c/Empaque');
  await page.getByRole('cell', { name: 'Caja chica' }).click();
  const url = page.url();

  const other = await browser.newPage({ storageState: 'e2e/.auth/admin.json' });
  await other.goto(url);
  await other.getByLabel('Nombre').fill('Caja chica (otra persona)');
  await other.getByRole('button', { name: 'Guardar cambios' }).click();
  await expect(other.getByText('Cambios guardados')).toBeVisible();
  await other.close();

  await page.getByLabel('Nombre').fill('Caja chica (yo)');
  await page.getByRole('button', { name: 'Guardar cambios' }).click();
  await expect(page.getByText('Otro usuario modificó este registro')).toBeVisible();
  await page.getByRole('button', { name: 'Recargar' }).click();
  await expect(page.getByLabel('Nombre')).toHaveValue('Caja chica (otra persona)');
});

test('H4: eliminar pide confirmación y el registro desaparece del listado', async ({ page }) => {
  await page.goto('/c/Empaque');
  await page.getByRole('cell', { name: 'Bolsa grande reforzada' }).click();
  await page.getByRole('button', { name: 'Eliminar' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/queda registrada en la auditoría/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Eliminar' }).click();
  await expect(page.getByText('Registro eliminado')).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Bolsa grande reforzada' })).toBeHidden();
});
