// Spec 050 · campos de varios valores: declararlo en el modelador (H1 CA4), capturarlo con el selector múltiple (H2 CA1),
// verlo en la lista y en el registro (H2 CA4) y filtrar por él (H5 CA1). Autocontenido: crea sus catálogos (grupo
// «Pruebas 050») y los archiva al terminar.
import type { Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 050';

test.beforeAll(async () => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  if ((await api.get('/api/model/entities/VvProveedor')).ok()) return;
  await applyPlan(api, {
    description: 'E2E 050: campos de varios valores',
    operations: [
      { op: 'createEntity', group: GROUP, name: 'VvCategoria', label: 'Rubro 050', labelPlural: 'Rubros 050', displayField: 'Nombre',
        fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
      { op: 'createEntity', group: GROUP, name: 'VvProveedor', label: 'Proveedor 050', labelPlural: 'Proveedores 050', displayField: 'Nombre',
        fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
    ],
  });
  for (const Nombre of ['Papelería', 'Limpieza', 'Cafetería'])
    expect((await api.post('/api/data/VvCategoria', { data: { Nombre } })).status()).toBe(201);
});

test.afterAll(async () => {
  if (process.env.E2E_INDEPENDENT) return;
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await api.get('/api/model/entities')).json()) as { name: string }[];
  const operations = ['VvProveedor', 'VvCategoria'].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(api, { description: 'E2E 050: limpieza', operations });
});

/**
 * Agrega un valor desde el buscador del campo, como una persona: la opción debe ser la primera y verse completa (lección de
 * `pick()` en compras.spec.ts, F-20).
 */
async function add(page: Page, field: string, search: string, option: string, viaKeyboard = false) {
  const box = page.getByRole('combobox', { name: field });
  await box.fill(search);
  const first = page.getByRole('listbox').getByRole('option').first();
  await expect(first).toHaveAccessibleName(option);
  const visibleToHuman = await first.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.bottom <= window.innerHeight && (hit === el || el.contains(hit));
  });
  expect(visibleToHuman, `la opción ${option} debe verse completa`).toBe(true);
  if (viaKeyboard) await box.press('Enter');
  else await first.click();
}

test('H1 CA4: el modelador agrega un campo de varios registros y la vista previa muestra su tabla de unión', async ({ page }) => {
  await page.goto('/admin/model');
  await page.getByRole('row', { name: /Proveedores 050/ }).getByRole('button', { name: 'Editar' }).click();
  await page.getByRole('button', { name: 'Agregar campo' }).click();
  await page.getByLabel('Etiqueta del campo').last().fill('Rubros');
  await page.getByLabel('Tipo de campo').last().selectOption({ label: 'Varios registros' });
  await page.getByLabel('Catálogo referenciado').last().selectOption({ label: 'Rubros 050' });
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText(/Agregar campo 'Rubros' \(multireference\)/)).toBeVisible();
  await dialog.getByText('Ver SQL').click();
  await expect(dialog.getByText('CREATE TABLE [app].[VvProveedor__Rubros]')).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
});

test('H2 CA1: el selector múltiple agrega con clic y con teclado, no repite lo elegido y quita con su botón', async ({ page }) => {
  await page.goto('/c/VvProveedor/new');
  await page.getByLabel('Nombre').fill('Oficinas del Centro');
  await add(page, 'Rubros', 'Limp', 'Limpieza');
  await add(page, 'Rubros', 'Pap', 'Papelería', true);
  const chosen = page.getByRole('list', { name: 'Rubros: 2 elegidos' });
  await expect(chosen.getByRole('listitem')).toHaveText(['Limpieza', 'Papelería']);

  // Lo ya elegido no se ofrece otra vez.
  await page.getByRole('combobox', { name: 'Rubros' }).fill('a');
  await expect(page.getByRole('listbox').getByRole('option')).toHaveText(['Cafetería']);
  await page.getByRole('combobox', { name: 'Rubros' }).press('Escape');

  // Quitar con el botón deja el foco en el buscador (nunca en body) y se anuncia.
  await chosen.getByRole('button', { name: 'Quitar Limpieza' }).click();
  await expect(page.getByRole('combobox', { name: 'Rubros' })).toBeFocused();
  await expect(page.getByText('Limpieza quitado de Rubros.')).toBeAttached();
  await add(page, 'Rubros', 'Caf', 'Cafetería');
  await expectAccessible(page);

  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await page.reload();
  await expect(page.getByRole('list', { name: 'Rubros: 2 elegidos' }).getByRole('listitem')).toHaveText(['Cafetería', 'Papelería']);
});

test('H2 CA4 · H5 CA1: la lista muestra las etiquetas y se filtra por «contiene»', async ({ page }) => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const rubros = (await (await api.get('/api/data/VvCategoria', { params: { pageSize: 50 } })).json()).items as { id: number; Nombre: string }[];
  const limpieza = rubros.find((r) => r.Nombre === 'Limpieza')!.id;
  const res = await api.post('/api/data/VvProveedor', { data: { Nombre: 'Aseo Integral', Rubros: [limpieza] } });
  expect(res.status(), await res.text()).toBe(201);

  await page.goto('/c/VvProveedor');
  await expect(page.getByRole('row').filter({ hasText: 'Oficinas del Centro' }).getByRole('cell', { name: 'Cafetería, Papelería' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'Aseo Integral' }).getByRole('cell', { name: 'Limpieza' })).toBeVisible();

  await page.getByRole('button', { name: /^Filtros/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Filtros' });
  await dialog.getByLabel('Campo 1', { exact: true }).selectOption('Rubros');
  await expect(dialog.getByLabel('Condición 1', { exact: true })).toHaveValue('contains');
  await dialog.getByRole('combobox', { name: 'Valor 1' }).fill('Limp');
  await expect(page.getByRole('listbox').getByRole('option').first()).toHaveAccessibleName('Limpieza');
  await page.getByRole('listbox').getByRole('option').first().click();
  await dialog.getByRole('button', { name: 'Aplicar' }).click();
  await expect(page.getByRole('list', { name: 'Filtros aplicados' })).toContainText('Rubros contiene Limpieza');
  await expect(page.getByRole('row').filter({ hasText: 'Aseo Integral' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'Oficinas del Centro' })).toBeHidden();
  await expectAccessible(page);
});
