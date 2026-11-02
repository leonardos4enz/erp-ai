// Spec 052 · filtros de búsqueda compuestos: el editor del modelador arma «Activo es igual a Sí y Categorías contiene la
// categoría del registro» (H1 CA1, H4 CA1–CA2); el buscador del formulario pide al servidor con el borrador, dice qué falta y
// ofrece solo los que cumplen (H2 CA1–CA2); lo que el buscador no ofrecería no se guarda, por la API ni desde el formulario
// (H3 CA1). Autocontenido: crea sus catálogos (grupo «Pruebas 052») y los archiva al terminar.
import type { Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test, tras } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 052';
const ids: Record<string, number> = {};

test.beforeAll(async () => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  if (!(await api.get('/api/model/entities/FbOrden')).ok()) {
    await applyPlan(api, {
      description: 'E2E 052: filtros de búsqueda compuestos',
      operations: [
        { op: 'createEntity', group: GROUP, name: 'FbCategoria', label: 'Categoría 052', labelPlural: 'Categorías 052', displayField: 'Nombre',
          fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
        { op: 'createEntity', group: GROUP, name: 'FbProveedor', label: 'Proveedor 052', labelPlural: 'Proveedores 052', displayField: 'Nombre',
          fields: [
            { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
            { name: 'Activo', label: 'Activo', type: 'boolean' },
            { name: 'Categorias', label: 'Categorías', type: 'multireference', referenceEntity: 'FbCategoria' },
          ] },
        { op: 'createEntity', group: GROUP, name: 'FbOrden', label: 'Orden 052', labelPlural: 'Órdenes 052', displayField: 'Nota',
          fields: [
            { name: 'Nota', label: 'Nota', type: 'text' },
            { name: 'Categoria', label: 'Categoría', type: 'reference', referenceEntity: 'FbCategoria' },
            { name: 'Proveedor', label: 'Proveedor', type: 'reference', referenceEntity: 'FbProveedor' },
          ] },
      ],
    });
  }
  const find = async (entity: string, nombre: string) =>
    ((await (await api.get(`/api/data/${entity}/lookup`, { params: { search: nombre } })).json()) as { id: number; label: string }[]).find((x) => x.label === nombre)?.id;
  const ensure = async (entity: string, data: Record<string, unknown>) =>
    (await find(entity, data.Nombre as string)) ?? (await (await api.post(`/api/data/${entity}`, { data })).json()).id as number;
  ids.papel = await ensure('FbCategoria', { Nombre: 'Papelería 052' });
  ids.limpieza = await ensure('FbCategoria', { Nombre: 'Limpieza 052' });
  ids.papeles = await ensure('FbProveedor', { Nombre: 'Papeles 052', Activo: true, Categorias: [ids.papel] });
  ids.limpia = await ensure('FbProveedor', { Nombre: 'Limpia 052', Activo: true, Categorias: [ids.limpieza] });
  ids.viejo = await ensure('FbProveedor', { Nombre: 'Viejo 052', Activo: false, Categorias: [ids.papel] });
});

test.afterAll(async () => {
  if (process.env.E2E_INDEPENDENT) return;
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await api.get('/api/model/entities')).json()) as { name: string }[];
  const operations = ['FbOrden', 'FbProveedor', 'FbCategoria'].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(api, { description: 'E2E 052: limpieza', operations });
});

async function def(name: string) {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  return (await api.get(`/api/model/entities/${name}`)).json();
}

/** El filtro ya declarado (para que cada prueba corra sola, E2E_INDEPENDENT). */
async function ensureFilter() {
  const d = await def('FbOrden');
  if (d.fields.find((f: { name: string }) => f.name === 'Proveedor').lookupFilter) return;
  const api = await apiAs(ADMIN.username, ADMIN.password);
  await applyPlan(api, { description: 'E2E 052: filtro', operations: [{ op: 'updateField', entity: 'FbOrden', field: 'Proveedor',
    lookupFilter: { conditions: [{ field: 'Activo', op: 'eq', value: true }, { field: 'Categorias', op: 'contains', source: 'Categoria' }] } }] });
}

async function choose(page: Page, label: string, search: string, option: string) {
  await page.getByRole('combobox', { name: label }).fill(search);
  const opt = page.getByRole('listbox').getByRole('option', { name: option });
  await expect(opt).toBeVisible();
  const visibleToHuman = await opt.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.bottom <= window.innerHeight && (hit === el || el.contains(hit));
  });
  expect(visibleToHuman, `la opción ${option} debe verse completa`).toBe(true);
  await opt.click();
}

test('H1 CA1 · H4 CA1–CA2: el editor arma el filtro en renglones y la vista previa lo describe', async ({ page }) => {
  await page.goto('/admin/model?edit=FbOrden');
  await page.getByRole('button', { name: 'Mostrar opciones de Proveedor' }).click();
  const filter = page.getByRole('group', { name: 'Filtro del buscador' });
  await expect(filter.getByText('El buscador ofrece todos los registros de Proveedores 052 a su alcance.')).toBeVisible();
  // Si ya estaba (otra vuelta de la misma base), se quita primero.
  while (await filter.getByRole('button', { name: /^Quitar condición 1 del filtro/ }).count())
    await filter.getByRole('button', { name: 'Quitar condición 1 del filtro' }).click();

  await filter.getByRole('button', { name: 'Agregar condición al filtro' }).click();
  await filter.getByLabel('Filtro, condición 1: campo de Proveedores 052').selectOption('Activo');
  await expect(filter.getByLabel('Filtro, condición 1: operador')).toHaveValue('eq');
  await expect(filter.getByLabel('Filtro, condición 1: valor')).toHaveValue('true');

  await filter.getByRole('button', { name: 'Agregar condición al filtro' }).click();
  await filter.getByLabel('Filtro, condición 2: campo de Proveedores 052').selectOption('Categorias');
  // Un campo de varios valores empieza en «contiene»; el valor sale del registro (la categoría de la orden).
  await expect(filter.getByLabel('Filtro, condición 2: operador')).toHaveValue('contains');
  await filter.getByLabel('Filtro, condición 2: comparar con').selectOption('source');
  const source = filter.getByLabel('Filtro, condición 2: dato del registro');
  await expect(source.getByRole('option')).toHaveText(['Dato…', 'Categoría']);
  await source.selectOption('Categoria');
  await expect(filter.getByText(/solo los registros de Proveedores 052 que cumplen todas las condiciones/)).toBeVisible();
  await expectAccessible(page);

  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText(/filtro del buscador: 2 condiciones/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  const prov = (await def('FbOrden')).fields.find((f: { name: string }) => f.name === 'Proveedor');
  expect(prov.lookupFilter.conditions).toEqual([
    { field: 'Activo', op: 'eq', value: true },
    { field: 'Categorias', op: 'contains', source: 'Categoria' },
  ]);
});

test('H2 CA1–CA2: el buscador dice qué falta y ofrece solo los que cumplen con lo capturado', async ({ page }) => {
  await ensureFilter();
  await page.goto('/c/FbOrden/new');
  const proveedor = page.getByRole('combobox', { name: 'Proveedor' });
  await proveedor.click();
  await expect(page.getByRole('listbox').getByText('Elige primero categoría')).toBeVisible();
  await page.keyboard.press('Escape');

  await choose(page, 'Categoría', 'Papel', 'Papelería 052');
  // Pregunta al servidor con el borrador: el filtro no viaja en la petición.
  const req = await tras(page.waitForRequest((r) => r.url().includes('/api/data/FbOrden/lookup/Proveedor')), () => proveedor.click());
  expect(req.method()).toBe('POST');
  expect(req.postDataJSON().values.Categoria).toBe(ids.papel);
  expect(JSON.stringify(req.postDataJSON())).not.toContain('Activo');
  await expect(page.getByRole('listbox').getByRole('option')).toHaveText(['Papeles 052']);
  await page.keyboard.press('Escape');

  // Otra categoría: otra respuesta.
  await page.getByRole('combobox', { name: 'Categoría' }).focus();
  await choose(page, 'Categoría', 'Limp', 'Limpieza 052');
  await proveedor.click();
  await expect(page.getByRole('listbox').getByRole('option')).toHaveText(['Limpia 052']);
  await expectAccessible(page);
});

test('H3 CA1: lo que el buscador no ofrecería no se guarda, por la API ni desde el formulario', async ({ page }) => {
  await ensureFilter();
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const bad = await api.post('/api/data/FbOrden', { data: { Nota: 'Inactivo 052', Categoria: ids.papel, Proveedor: ids.viejo } });
  expect(bad.status()).toBe(400);
  expect((await bad.json()).errors.Proveedor[0]).toMatch(/no cumple el filtro del buscador/);

  // En el formulario: elegido cuando cumplía; otra persona lo desactiva antes de guardar.
  const temporal = (await (await api.post('/api/data/FbProveedor', { data: { Nombre: `Temporal 052 ${Date.now()}`, Activo: true, Categorias: [ids.papel] } })).json()) as { id: number; Nombre: string; _rowVersion: string };
  await page.goto('/c/FbOrden/new');
  await page.getByLabel('Nota').fill('Desde el formulario 052');
  await choose(page, 'Categoría', 'Papel', 'Papelería 052');
  await choose(page, 'Proveedor', 'Temporal', temporal.Nombre);
  expect((await api.put(`/api/data/FbProveedor/${temporal.id}`, { data: { Activo: false, _rowVersion: temporal._rowVersion } })).status()).toBe(200);
  await page.getByRole('button', { name: 'Crear', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'el registro elegido no cumple el filtro del buscador' })).toBeVisible();
  const list = (await (await api.get('/api/data/FbOrden', { params: { search: 'Desde el formulario 052' } })).json()).items as unknown[];
  expect(list).toHaveLength(0);
});
