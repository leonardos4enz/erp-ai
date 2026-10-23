// Spec 003 · Módulo de Compras. CA3: cada regla de negocio 1–9 tiene al menos una prueba (ver títulos "R#").
// El módulo se instala desde la UI (spec 002 · H7); los datos de apoyo se preparan por API.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de compras 2026';
const USERS = {
  comprador: { role: 'Comprador', name: 'Comprador E2E' },
  gerente: { role: 'Gerente de compras', name: 'Gerente E2E' },
  director: { role: 'Director', name: 'Director E2E' },
  almacen: { role: 'Almacenista', name: 'Almacenista E2E' },
  solicitante: { role: 'Solicitante', name: 'Solicitante E2E' },
} as const;
type Who = keyof typeof USERS;
const username = (w: Who) => `e2e-${w}`;

const api: Partial<Record<Who, APIRequestContext>> = {};
const ids = { proveedor: 0, almacen: 0, caja: 0, pelicula: 0 };
let ocUiUrl = '';

async function as(w: Who) {
  return (api[w] ??= await apiAs(username(w), PWD));
}

async function pageAs(browser: Browser, w: Who): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, username(w), PWD);
  return page;
}

/**
 * Elige una opción en un combobox de referencia como lo haría una persona.
 * Regresión (F-20): Playwright puede hacer clic en opciones recortadas por un contenedor con scroll
 * (las desplaza solo), pero una persona no las ve. Por eso primero se exige que la opción sea el
 * elemento visible en su propio centro: ni recortada ni tapada.
 */
async function pick(page: Page, label: string | RegExp, search: string, option: RegExp) {
  await page.getByLabel(label).fill(search);
  const opt = page.getByRole('option', { name: option }).first();
  await expect(opt).toBeVisible();
  const visibleToHuman = await opt.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.bottom <= window.innerHeight && (hit === el || el.contains(hit));
  });
  expect(visibleToHuman, `la opción ${option} debe verse completa sin desplazar contenedores`).toBe(true);
  await opt.click();
}

async function newOrder(w: Who, lines: { producto: number; cantidad: number }[], extra: Record<string, unknown> = {}) {
  const res = await (await as(w)).post('/api/data/OrdenCompra', {
    data: { Proveedor: ids.proveedor, Almacen: ids.almacen, Partidas: lines.map((l) => ({ Producto: l.producto, Cantidad: l.cantidad })), ...extra },
  });
  return { status: res.status(), body: await res.json() };
}

async function transition(w: Who, entity: string, row: { id: number; _rowVersion: string }, name: string, more: Record<string, unknown> = {}) {
  const res = await (await as(w)).post(`/api/data/${entity}/${row.id}/transition`, { data: { transition: name, _rowVersion: row._rowVersion, ...more } });
  return { status: res.status(), body: await res.json() };
}

test('H7: instalar el módulo de Compras desde la UI', async ({ page }) => {
  await page.goto('/admin/modules');
  await page.getByRole('button', { name: 'Instalar Compras' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText('Gerente de compras')).toBeVisible();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Instalar módulo' }).click();
  await expect(page.getByText('Módulo instalado')).toBeVisible({ timeout: 30_000 });
  // El menú se actualiza sin recargar y las partidas no aparecen como catálogos.
  const nav = page.getByLabel('Navegación principal');
  await expect(nav.getByRole('link', { name: 'Órdenes de compra' })).toBeVisible();
  await expect(nav.getByRole('link', { name: /Partidas de/ })).toHaveCount(0);
  await expect(page.getByText('Instalado', { exact: true })).toBeVisible();

  // Regresión (revisión exploratoria 003): con un módulo, el menú plano empujaba "Configuración" fuera
  // de la pantalla. Ahora el módulo es una sección plegable y la configuración sigue a la vista.
  const group = nav.getByRole('button', { name: 'Compras' });
  await expect(group).toHaveAttribute('aria-expanded', 'true');
  await group.click();
  await expect(nav.getByRole('link', { name: 'Órdenes de compra' })).toHaveCount(0);
  await expect(nav.getByRole('link', { name: 'Auditoría' })).toBeInViewport();
  await group.click();
});

test('preparar usuarios por rol y datos de referencia', async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  for (const [w, u] of Object.entries(USERS) as [Who, (typeof USERS)[Who]][]) {
    const res = await admin.post('/api/admin/users', {
      data: { username: username(w), displayName: u.name, password: PWD, roleIds: [await roleId(admin, u.role)] },
    });
    expect(res.status(), await res.text()).toBe(201);
  }
  const c = await as('comprador');
  ids.proveedor = (await (await c.get('/api/data/Proveedor', { params: { search: 'Cartones' } })).json()).items[0].id;
  ids.almacen = (await (await c.get('/api/data/Almacen/lookup')).json())[0].id;
  ids.caja = (await (await c.get('/api/data/Producto', { params: { search: 'CAJ-30' } })).json()).items[0].id;
  ids.pelicula = (await (await c.get('/api/data/Producto', { params: { search: 'estirable' } })).json()).items[0].id;
  expect(Object.values(ids).every((v) => v > 0)).toBe(true);
});

test('CA2 · R1 · R3 · R9: el comprador captura una orden con totales en vivo y la envía', async ({ browser }) => {
  const page = await pageAs(browser, 'comprador');
  await page.goto('/c/OrdenCompra/new');
  await expect(page.getByRole('heading', { name: 'Registrar orden de compra' })).toBeVisible();
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveValue('1'); // valor por defecto

  await pick(page, /^Proveedor/, 'Cartones', /Cartones del Norte/);
  await expect(page.getByLabel(/^Moneda/)).toHaveValue('MXN'); // sugerido desde el proveedor
  await pick(page, /^Almacén destino/, 'general', /Almacén general/);

  await page.getByRole('button', { name: 'Agregar partida' }).click();
  await pick(page, 'Producto, partida 1', 'CAJ', /Caja corrugada/);
  await expect(page.getByLabel('Producto, partida 1')).toHaveValue(/Caja corrugada/);
  await expect(page.getByLabel('Precio unitario, partida 1')).toHaveValue('18.5'); // costo último del producto
  // Al reabrir el buscador, el producto actual sigue visible como guía.
  await page.getByLabel('Producto, partida 1').click();
  await expect(page.getByLabel('Producto, partida 1')).toHaveAttribute('placeholder', /Caja corrugada/);
  await page.keyboard.press('Escape');
  await page.getByLabel('Cantidad, partida 1').fill('100');
  await expect(page.getByText('2,146.00').first()).toBeVisible(); // 100 × 18.50 + 16 % IVA, calculado en vivo

  // Regresión (revisión exploratoria 003): enfocar la última columna de la tabla de partidas desplazaba
  // toda la página porque un .sr-only absoluto escapaba del contenedor con scroll.
  await page.getByLabel('IVA %, partida 1').focus();
  expect(await page.evaluate(() => [window.scrollX, document.documentElement.scrollWidth - document.documentElement.clientWidth]))
    .toEqual([0, 0]);

  // R3: cantidad > 0 (error por celda)
  await page.getByLabel('Cantidad, partida 1').fill('0');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('La cantidad debe ser mayor a cero.')).toBeVisible();
  await page.getByLabel('Cantidad, partida 1').fill('100');

  // R1: la entrega no puede ser anterior a la orden
  await page.getByLabel(/^Fecha de entrega/).fill('2020-01-01');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('La fecha de entrega no puede ser anterior a la fecha de la orden.')).toBeVisible();
  await page.getByLabel(/^Fecha de entrega/).fill('2099-12-31');
  await expectAccessible(page);

  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await expect(page).toHaveURL(/\/c\/OrdenCompra\/\d+$/);
  await expect(page.getByRole('heading', { name: /Orden de compra OC-\d{6}/ })).toBeVisible();
  ocUiUrl = page.url();

  await page.getByRole('button', { name: 'Enviar a autorización' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Enviar a autorización' }).click();
  await expect(page.getByText('Estado: Por autorizar')).toBeVisible();
  // R9: fuera de Borrador el documento queda cerrado a edición.
  await expect(page.getByText(/ya no se puede editar/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guardar cambios' })).toHaveCount(0);
  await page.context().close();
});

test('R8: el gerente autoriza y quedan registrados quién y cuándo', async ({ browser }) => {
  const page = await pageAs(browser, 'gerente');
  await page.goto(ocUiUrl);
  await page.getByRole('button', { name: 'Autorizar' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Autorizar' }).click();
  await expect(page.getByText('Estado: Autorizada')).toBeVisible();
  await expect(page.getByLabel(/^Autorizado por/)).toHaveValue('Gerente E2E');
  await expect(page.getByLabel(/^Fecha de autorización/)).not.toHaveValue('');
  // Historial visible con el cambio de estado.
  await expect(page.getByText('Gerente E2E').last()).toBeVisible();
  await page.context().close();
});

test('R8: rechazar exige motivo; sin él no cambia el estado', async ({ browser }) => {
  const oc = await newOrder('comprador', [{ producto: ids.caja, cantidad: 10 }]);
  const sent = await transition('comprador', 'OrdenCompra', oc.body, 'enviar');
  expect(sent.body.Estado).toBe('PorAutorizar');

  const page = await pageAs(browser, 'gerente');
  await page.goto(`/c/OrdenCompra/${oc.body.id}`);
  await page.getByRole('button', { name: 'Rechazar' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Rechazar' }).click();
  await expect(dialog.getByText('Este campo es obligatorio')).toBeVisible(); // validación en el cliente
  await expect(page.getByText('Estado', { exact: true }).locator('..').getByText('Por autorizar')).toBeVisible();
  await dialog.getByLabel(/^Motivo de rechazo/).fill('Precio fuera de mercado');
  await dialog.getByRole('button', { name: 'Rechazar' }).click();
  await expect(page.getByText('Estado: Rechazada')).toBeVisible();
  await page.context().close();
});

test('R6: arriba de $50,000 MXN solo autoriza el Director (y el gerente ve por qué)', async ({ browser }) => {
  const big = await newOrder('comprador', [{ producto: ids.pelicula, cantidad: 300 }]); // 300 × 245 + IVA = 85,260
  expect(big.body.TotalMXN).toBeGreaterThan(50000);
  const sent = await transition('comprador', 'OrdenCompra', big.body, 'enviar');

  const page = await pageAs(browser, 'gerente');
  await page.goto(`/c/OrdenCompra/${big.body.id}`);
  const btn = page.getByRole('button', { name: 'Autorizar' });
  await expect(btn).toHaveAttribute('aria-disabled', 'true');
  await expect(btn).toHaveAttribute('title', /Director/);
  await page.context().close();

  expect((await transition('gerente', 'OrdenCompra', sent.body, 'autorizar')).status).toBe(400);
  const ok = await transition('director', 'OrdenCompra', sent.body, 'autorizar');
  expect(ok.status).toBe(200);
  expect(ok.body.AutorizadoPor__label).toBe('Director E2E');
});

test('R7: control interno — quien crea la orden no la autoriza (salvo el Director)', async () => {
  const own = await newOrder('gerente', [{ producto: ids.caja, cantidad: 5 }]);
  const sent = await transition('gerente', 'OrdenCompra', own.body, 'enviar');
  const denied = await transition('gerente', 'OrdenCompra', sent.body, 'autorizar');
  expect(denied.status).toBe(400);
  expect(denied.body.title).toMatch(/control interno/);
  // Un rol sin la transición recibe 403, no 400.
  expect((await transition('comprador', 'OrdenCompra', sent.body, 'autorizar')).status).toBe(403);
});

test('R5: sin partidas no se puede enviar a autorización', async ({ browser }) => {
  const empty = await newOrder('comprador', []);
  expect(empty.status).toBe(201);
  const t = empty.body._workflow.transitions.find((x: { name: string }) => x.name === 'enviar');
  expect(t.allowed).toBe(false);
  expect(t.reason).toBe('Agrega al menos una partida.');
  expect((await transition('comprador', 'OrdenCompra', empty.body, 'enviar')).status).toBe(400);

  const page = await pageAs(browser, 'comprador');
  await page.goto(`/c/OrdenCompra/${empty.body.id}`);
  await expect(page.getByRole('button', { name: 'Enviar a autorización' })).toHaveAttribute('aria-disabled', 'true');
  await page.context().close();
});

test('R2: no se hacen órdenes a proveedores inactivos', async () => {
  const c = await as('comprador');
  const prov = await (await c.post('/api/data/Proveedor', {
    data: { RFC: 'INA000101AA1', RazonSocial: 'Proveedor Inactivo, S.A.', Activo: false },
  })).json();
  const res = await c.post('/api/data/OrdenCompra', { data: { Proveedor: prov.id, Almacen: ids.almacen, Moneda: 1 } });
  expect(res.status()).toBe(400);
  expect((await res.json()).errors.Proveedor[0]).toMatch(/inactivo/);
});

test('R3 · R4: rangos de partidas y tipo de cambio en pesos', async () => {
  const c = await as('comprador');
  const bad = await c.post('/api/data/OrdenCompra', { data: {
    Proveedor: ids.proveedor, Almacen: ids.almacen, TipoCambio: 17.5,
    Partidas: [{ Producto: ids.caja, Cantidad: 1, PrecioUnitario: -1, DescuentoPct: 150, IvaPct: 101 }],
  } });
  const errors = (await bad.json()).errors;
  expect(bad.status()).toBe(400);
  expect(errors.TipoCambio[0]).toMatch(/tipo de cambio debe ser 1/);
  expect(errors['Partidas[0].PrecioUnitario']).toBeTruthy();
  expect(errors['Partidas[0].DescuentoPct']).toBeTruthy();
  expect(errors['Partidas[0].IvaPct']).toBeTruthy();
});

test('Recepción: solo se confirma con la orden enviada y número de factura; luego la orden se recibe', async () => {
  // La orden capturada por UI ya está autorizada: el comprador la envía al proveedor.
  const id = Number(ocUiUrl.split('/').pop());
  const oc = await (await (await as('comprador')).get(`/api/data/OrdenCompra/${id}`)).json();
  const sentOc = await transition('comprador', 'OrdenCompra', oc, 'enviarProveedor');
  expect(sentOc.body.Estado).toBe('Enviada');

  const a = await as('almacen');
  const computed = (await (await a.post('/api/data/Recepcion/compute', { data: { values: { OrdenCompra: id } } })).json()).values;
  expect(computed.Almacen).toBe(ids.almacen); // sugerido desde la orden

  let rec = await (await a.post('/api/data/Recepcion', { data: { OrdenCompra: id, Partidas: [{ Producto: ids.caja, Cantidad: 100 }] } })).json();
  expect(rec.Folio).toMatch(/^REC-\d{6}$/);
  const blocked = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(blocked.status).toBe(400);
  expect(blocked.body.title).toMatch(/factura/);

  rec = await (await a.put(`/api/data/Recepcion/${rec.id}`, { data: { _rowVersion: rec._rowVersion, Factura: 'F-2026-001' } })).json();
  expect((await transition('almacen', 'Recepcion', rec, 'confirmar')).body.Estado).toBe('Confirmada');
  expect((await transition('almacen', 'OrdenCompra', sentOc.body, 'recibir')).body.Estado).toBe('Recibida');

  const history = await (await (await as('comprador')).get(`/api/data/OrdenCompra/${id}/history`)).json();
  expect(history.map((h: { to: string }) => h.to)).toEqual(['Borrador', 'PorAutorizar', 'Autorizada', 'Enviada', 'Recibida']);
});

test('Requisición: el solicitante la envía y solo el gerente la aprueba', async () => {
  const s = await as('solicitante');
  const dep = (await (await s.get('/api/data/Departamento/lookup')).json())[0].id;
  let req = await (await s.post('/api/data/Requisicion', { data: {
    Departamento: dep, Justificacion: 'Reposición de empaque', Partidas: [{ Producto: ids.caja, Cantidad: 40 }],
  } })).json();
  expect(req.Folio).toMatch(/^REQ-\d{6}$/);
  expect(req.TotalEstimado).toBe(740); // 40 × costo último 18.50
  expect(req.Solicitante__label).toBe('Solicitante E2E');

  req = (await transition('solicitante', 'Requisicion', req, 'enviar')).body;
  expect((await transition('solicitante', 'Requisicion', req, 'aprobar')).status).toBe(403);
  expect((await transition('gerente', 'Requisicion', req, 'aprobar')).body.Estado).toBe('Aprobada');
});

test('Listado de órdenes: filtro por estado con conteos', async ({ browser }) => {
  const page = await pageAs(browser, 'comprador');
  await page.goto('/c/OrdenCompra');
  const chip = page.getByRole('radio', { name: /Por autorizar/ });
  await chip.click();
  await expect(page).toHaveURL(/state=PorAutorizar/);
  const badges = page.getByRole('table').getByText('Por autorizar');
  await expect(badges.first()).toBeVisible();
  await expect(page.getByRole('table').getByText('Rechazada')).toHaveCount(0);
  await expectAccessible(page);
  await page.context().close();
});

test('Seguridad: las partidas no se editan directo ni el estado se fuerza por API', async () => {
  const c = await as('comprador');
  expect((await c.post('/api/data/OrdenCompraPartida', { data: { Cantidad: 1 } })).status()).toBe(400);
  const oc = await newOrder('comprador', [{ producto: ids.caja, cantidad: 1 }]);
  const forced = await c.put(`/api/data/OrdenCompra/${oc.body.id}`, { data: { _rowVersion: oc.body._rowVersion, Estado: 'Autorizada' } });
  expect((await forced.json()).Estado).toBe('Borrador'); // el campo de estado se ignora
  // El almacenista no puede crear órdenes.
  expect((await (await as('almacen')).post('/api/data/OrdenCompra', { data: {} })).status()).toBe(403);
});
