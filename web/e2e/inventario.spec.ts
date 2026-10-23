// Spec 005 · H3: módulo de Inventario. Corre después de compras.spec.ts (requiere Compras instalado y
// reutiliza sus usuarios). Los datos de apoyo se preparan por API; lo que se prueba, por la UI cuando aplica.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de compras 2026'; // usuarios creados por compras.spec.ts
const users = { comprador: 'e2e-comprador', gerente: 'e2e-gerente', almacen: 'e2e-almacen', supervisor: 'e2e-supervisor' } as const;
type Who = keyof typeof users;
const api: Partial<Record<Who | 'admin', APIRequestContext>> = {};
const ids = { almacen1: 0, almacen2: 0, rodamiento: 0, proveedor: 0 };

const as = async (w: Who | 'admin') =>
  (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));

type Row = { id: number; _rowVersion: string; [k: string]: unknown };

async function transition(w: Who, entity: string, row: Row, name: string) {
  const res = await (await as(w)).post(`/api/data/${entity}/${row.id}/transition`, { data: { transition: name, _rowVersion: row._rowVersion } });
  return { status: res.status(), body: await res.json() };
}

async function stock(almacen: number, producto: number): Promise<number | null> {
  const r = await (await (await as('almacen')).get('/api/data/Existencia', { params: { pageSize: 200 } })).json();
  return r.items.find((x: { Almacen: number; Producto: number }) => x.Almacen === almacen && x.Producto === producto)?.Cantidad ?? null;
}

async function movements(referencia: string) {
  const r = await (await (await as('almacen')).get('/api/data/MovimientoInventario', { params: { pageSize: 200 } })).json();
  return r.items.filter((m: { Referencia: string | null }) => m.Referencia === referencia)
    .map((m: { Tipo: string; Almacen: number; Cantidad: number; Efecto: number }) => ({ tipo: m.Tipo, almacen: m.Almacen, cantidad: m.Cantidad, efecto: m.Efecto }));
}

async function pageAs(browser: Browser, w: Who): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

/** Igual que en compras.spec.ts (F-20): la opción debe verse sin desplazar contenedores. */
async function pick(page: Page, label: string | RegExp, search: string, option: RegExp) {
  await page.getByLabel(label).fill(search);
  // Espera a que la búsqueda filtre: la opción buscada debe ser la primera, como la vería una persona.
  await expect(page.getByRole('listbox').getByRole('option').first()).toHaveAccessibleName(option);
  const opt = page.getByRole('option', { name: option }).first();
  await expect(opt).toBeVisible();
  const visibleToHuman = await opt.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.bottom <= window.innerHeight && (hit === el || el.contains(hit));
  });
  expect(visibleToHuman).toBe(true);
  await opt.click();
}

test('H7: instalar Inventario desde la UI (ya con Compras)', async ({ page }) => {
  await page.goto('/admin/modules');
  await page.getByRole('button', { name: 'Instalar Inventario' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText('Supervisor de inventario')).toBeVisible();
  await expect(dialog.getByText(/Agregar o actualizar 1 automatización\(es\) en 'Recepción'/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Instalar módulo' }).click();
  await expect(page.getByText('Módulo instalado')).toBeVisible({ timeout: 30_000 });
  const nav = page.getByLabel('Navegación principal');
  await expect(nav.getByRole('link', { name: 'Existencias' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Movimientos de inventario' })).toBeVisible();

  // Las automatizaciones de Compras se conservan: Inventario agregó la suya (merge).
  const recepcion = await (await (await as('admin')).get('/api/model/entities/Recepcion')).json();
  expect(recepcion.automations.map((a: { name: string }) => a.name)).toEqual(['AcumularRecibido', 'EntradaAlmacen']);
});

test('preparar supervisor y datos de referencia', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/users', {
    data: { username: users.supervisor, displayName: 'Supervisor E2E', password: PWD, roleIds: [await roleId(admin, 'Supervisor de inventario')] },
  });
  expect(res.status(), await res.text()).toBe(201);
  const almacenes = await (await admin.get('/api/data/Almacen', { params: { pageSize: 50 } })).json();
  ids.almacen1 = almacenes.items.find((a: { Clave: string }) => a.Clave === 'ALM-01').id;
  ids.almacen2 = almacenes.items.find((a: { Clave: string }) => a.Clave === 'ALM-02').id; // creado en compras.spec (spec 004 · H3)
  ids.rodamiento = (await (await admin.get('/api/data/Producto', { params: { search: 'ROD-6204' } })).json()).items[0].id;
  ids.proveedor = (await (await admin.get('/api/data/Proveedor', { params: { search: 'Cartones' } })).json()).items[0].id;
  expect(Object.values(ids).every((v) => v > 0)).toBe(true);
  expect(await stock(ids.almacen1, ids.rodamiento)).toBeNull();
});

test('H3 CA1: confirmar una recepción genera la entrada y crea la existencia del almacén', async () => {
  const c = await as('comprador');
  const created = await (await c.post('/api/data/OrdenCompra', { data: {
    Proveedor: ids.proveedor, Almacen: ids.almacen1, Partidas: [{ Producto: ids.rodamiento, Cantidad: 50 }],
  } })).json();
  const sent = await transition('comprador', 'OrdenCompra', created, 'enviar');
  const auth = await transition('gerente', 'OrdenCompra', sent.body, 'autorizar');
  const oc = (await transition('comprador', 'OrdenCompra', auth.body, 'enviarProveedor')).body;

  const a = await as('almacen');
  const rec = await (await a.post('/api/data/Recepcion', { data: {
    OrdenCompra: oc.id, Factura: 'F-INV-1', Partidas: [{ PartidaOrden: oc.Partidas[0].id }],
  } })).json();
  expect(rec.Partidas[0].Cantidad).toBe(50); // sugerida: lo pendiente
  const confirmed = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(confirmed.status, JSON.stringify(confirmed.body)).toBe(200);

  expect(await movements(rec.Folio)).toEqual([{ tipo: 'Entrada', almacen: ids.almacen1, cantidad: 50, efecto: 50 }]);
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(50);
  // Las dos automatizaciones de la recepción corrieron en la misma transacción.
  expect((await (await c.get(`/api/data/OrdenCompra/${oc.id}`)).json()).Estado).toBe('Recibida');
});

test('H3 CA2: una salida por la UI descuenta la existencia y nunca la deja negativa', async ({ browser }) => {
  const page = await pageAs(browser, 'almacen');
  async function salida(cantidad: string) {
    await page.goto('/c/MovimientoInventario/new');
    await page.getByLabel(/^Tipo/).selectOption('Salida');
    await pick(page, /^Almacén/, 'general', /Almacén general/);
    await pick(page, /^Producto/, 'Rodamiento', /Rodamiento 6204/);
    await page.getByLabel(/^Cantidad/).fill(cantidad);
    await page.getByRole('button', { name: 'Crear' }).click();
  }
  await salida('20');
  await expect(page.getByText('Registro creado')).toBeVisible();
  await expect(page.getByText('Documento cerrado a edición')).toBeVisible(); // aplicado: inmutable
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(30);

  const before = (await (await (await as('almacen')).get('/api/data/MovimientoInventario')).json()).total;
  await salida('100');
  await expect(page.getByText('No se pudo completar «Actualizar la existencia»: No hay existencias suficientes: la operación dejaría el inventario en negativo.')).toBeVisible();
  await expectAccessible(page);
  await page.context().close();
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(30);
  expect((await (await (await as('almacen')).get('/api/data/MovimientoInventario')).json()).total).toBe(before);
});

test('H3 CA4: los movimientos no se editan ni se eliminan y las existencias no se capturan', async () => {
  const a = await as('almacen');
  const mov = (await (await a.get('/api/data/MovimientoInventario', { params: { pageSize: 1 } })).json()).items[0];
  const edit = { data: { _rowVersion: mov._rowVersion, Cantidad: 999 } };
  expect((await a.put(`/api/data/MovimientoInventario/${mov.id}`, edit)).status()).toBe(403); // ningún rol edita movimientos
  expect((await (await as('admin')).put(`/api/data/MovimientoInventario/${mov.id}`, edit)).status()).toBe(409); // ni el admin: estado no editable
  expect((await (await as('admin')).delete(`/api/data/MovimientoInventario/${mov.id}`)).status()).toBe(409);
  expect((await (await as('supervisor')).delete(`/api/data/MovimientoInventario/${mov.id}`)).status()).toBe(403);
  expect((await a.post('/api/data/Existencia', { data: {} })).status()).toBe(403);
  // Ni el administrador: los campos los asigna el sistema y la regla lo explica.
  const forced = await (await as('admin')).post('/api/data/Existencia', { data: { Almacen: ids.almacen1, Producto: ids.rodamiento, Cantidad: 1000 } });
  expect(forced.status()).toBe(400);
  expect(JSON.stringify(await forced.json())).toMatch(/las crea el sistema/);
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(30);
});

test('H3 CA5: solo el supervisor de inventario registra ajustes; los traspasos solo por documento', async () => {
  const ajuste = { Tipo: 'Ajuste', Almacen: ids.almacen1, Producto: ids.rodamiento, Cantidad: -5, Notas: 'Merma' };
  const denied = await (await as('almacen')).post('/api/data/MovimientoInventario', { data: ajuste });
  expect(denied.status()).toBe(400);
  expect((await denied.json()).errors.Tipo[0]).toMatch(/supervisor de inventario/);

  const ok = await (await as('supervisor')).post('/api/data/MovimientoInventario', { data: ajuste });
  expect(ok.status(), await ok.text()).toBe(201);
  expect((await ok.json()).Efecto).toBe(-5);
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(25);

  const fake = await (await as('almacen')).post('/api/data/MovimientoInventario', { data: { ...ajuste, Tipo: 'Traspaso entrada', Cantidad: 5, Referencia: 'TRA-999999' } });
  expect(fake.status()).toBe(400);
  expect((await fake.json()).errors.Tipo[0]).toMatch(/documento de traspaso/); // la referencia no la captura una persona
});

test('H3 CA3: un traspaso aplicado mueve la mercancía; si no alcanza, no mueve nada', async () => {
  const a = await as('almacen');
  const same = await a.post('/api/data/Traspaso', { data: { AlmacenOrigen: ids.almacen1, AlmacenDestino: ids.almacen1, Partidas: [{ Producto: ids.rodamiento, Cantidad: 1 }] } });
  expect((await same.json()).errors.AlmacenDestino[0]).toMatch(/distinto del origen/);

  const tra = await (await a.post('/api/data/Traspaso', { data: {
    AlmacenOrigen: ids.almacen1, AlmacenDestino: ids.almacen2, Partidas: [{ Producto: ids.rodamiento, Cantidad: 10 }],
  } })).json();
  expect(tra.Folio).toMatch(/^TRA-\d{6}$/);
  expect((await transition('almacen', 'Traspaso', tra, 'aplicar')).body.Estado).toBe('Aplicado');
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(15);
  expect(await stock(ids.almacen2, ids.rodamiento)).toBe(10);
  expect(await movements(tra.Folio)).toEqual(expect.arrayContaining([
    { tipo: 'Traspaso salida', almacen: ids.almacen1, cantidad: 10, efecto: -10 },
    { tipo: 'Traspaso entrada', almacen: ids.almacen2, cantidad: 10, efecto: 10 },
  ]));

  const big = await (await a.post('/api/data/Traspaso', { data: {
    AlmacenOrigen: ids.almacen1, AlmacenDestino: ids.almacen2, Partidas: [{ Producto: ids.rodamiento, Cantidad: 100 }],
  } })).json();
  const failed = await transition('almacen', 'Traspaso', big, 'aplicar');
  expect(failed.status).toBe(400);
  expect(failed.body.title).toMatch(/No hay existencias suficientes/);
  expect((await (await a.get(`/api/data/Traspaso/${big.id}`)).json()).Estado).toBe('Borrador');
  expect(await movements(big.Folio)).toEqual([]);
  expect(await stock(ids.almacen1, ids.rodamiento)).toBe(15);
  expect(await stock(ids.almacen2, ids.rodamiento)).toBe(10);
});

test('Existencias: el almacenista las consulta en la UI', async ({ browser }) => {
  const page = await pageAs(browser, 'almacen');
  await page.goto('/c/Existencia');
  const table = page.getByRole('table');
  await expect(table.getByText('Rodamiento 6204-2RS').first()).toBeVisible();
  await expect(table.getByText('Almacén refacciones')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Nuevo', exact: true })).toHaveCount(0); // no se capturan
  await expectAccessible(page);
  await page.context().close();
});
