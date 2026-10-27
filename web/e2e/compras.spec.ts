// Spec 003 · Módulo de Compras. CA3: cada regla de negocio 1–9 tiene al menos una prueba (ver títulos "R#").
// El módulo se instala desde la UI (spec 002 · H7); los datos de apoyo se preparan por API.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

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
  // Espera a que la búsqueda filtre: la opción buscada debe ser la primera, como la vería una persona.
  await expect(page.getByRole('listbox').getByRole('option').first()).toHaveAccessibleName(option);
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
  await expect(page.getByText('Instalado', { exact: true })).toBeVisible();

  // Spec 024: el módulo aparece solo en el selector (sin recargar) y su menú no mezcla la configuración.
  // Antes (revisión exploratoria 003) el menú plano empujaba «Configuración» fuera de la pantalla.
  await page.getByRole('banner').getByRole('button', { name: 'Cambiar de módulo' }).click();
  await page.getByRole('main').getByRole('link', { name: /^Compras/ }).click();
  await expect(page).toHaveURL(/\/m\/Compras$/);
  const nav = page.getByLabel('Navegación principal');
  await expect(nav.getByRole('link', { name: 'Órdenes de compra' })).toBeVisible();
  await expect(nav.getByRole('link', { name: /Partidas de/ })).toHaveCount(0);
  await expect(nav.getByRole('link', { name: 'Auditoría' })).toHaveCount(0);
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
  // Compras 1.4.0 (spec 007): con la moneda en pesos el tipo de cambio no se muestra (su valor por defecto es 1).
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveCount(0);

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
  // Compras 1.4.0: la autorización está en la pestaña «Seguimiento».
  await page.getByRole('tab', { name: 'Seguimiento' }).click();
  await expect(page.getByLabel(/^Autorizado por/)).toHaveValue('Gerente E2E');
  await expect(page.getByLabel(/^Fecha de autorización/)).not.toHaveValue('');
  // Historial visible con el cambio de estado.
  // razón: el nombre está también en el campo «Autorizado por»; el del historial es el último y se acaba de comprobar el campo.
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

  // Spec 005: la partida de la recepción se liga a la partida de la orden (el producto se toma de ella).
  const lineId = sentOc.body.Partidas[0].id;
  let rec = await (await a.post('/api/data/Recepcion', { data: { OrdenCompra: id, Partidas: [{ PartidaOrden: lineId, Cantidad: 100 }] } })).json();
  expect(rec.Folio).toMatch(/^REC-\d{6}$/);
  expect(rec.Partidas[0].Producto).toBe(ids.caja);
  const blocked = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(blocked.status).toBe(400);
  expect(blocked.body.title).toMatch(/factura/);

  rec = await (await a.put(`/api/data/Recepcion/${rec.id}`, { data: { _rowVersion: rec._rowVersion, Factura: 'F-2026-001' } })).json();
  expect((await transition('almacen', 'Recepcion', rec, 'confirmar')).body.Estado).toBe('Confirmada');
  // Spec 005 · H2 CA3: la orden se recibe sola; ya nadie la marca a mano.
  const received = await (await a.get(`/api/data/OrdenCompra/${id}`)).json();
  expect(received.Estado).toBe('Recibida');
  expect(received.PendientePorRecibir).toBe(0);

  const history = await (await (await as('comprador')).get(`/api/data/OrdenCompra/${id}/history`)).json();
  expect(history.map((h: { to: string }) => h.to)).toEqual(['Borrador', 'PorAutorizar', 'Autorizada', 'Enviada', 'Recibida']);
});

test('Requisición: el solicitante la envía y solo el gerente la aprueba', async () => {
  const s = await as('solicitante');
  const dep = (await (await s.get('/api/data/Departamento/lookup')).json())[0].id;
  let req = await (await s.post('/api/data/Requisicion', { data: {
    Departamento: dep, Almacen: ids.almacen, Justificacion: 'Reposición de empaque', Partidas: [{ Producto: ids.caja, Cantidad: 40 }],
  } })).json();
  expect(req.Folio).toMatch(/^REQ-\d{6}$/);
  expect(req.TotalEstimado).toBe(740); // 40 × costo último 18.50
  expect(req.Solicitante__label).toBe('Solicitante E2E');

  req = (await transition('solicitante', 'Requisicion', req, 'enviar')).body;
  expect((await transition('solicitante', 'Requisicion', req, 'aprobar')).status).toBe(403);
  expect((await transition('gerente', 'Requisicion', req, 'aprobar')).body.Estado).toBe('Aprobada');
});

// ---------------- Spec 004 (Compras 1.1.0) ----------------

test('Spec 004 · H1: fecha y solicitante de la requisición los asigna el sistema, aunque se envíen otros', async ({ browser }) => {
  const s = await as('solicitante');
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const otherUser = (await (await admin.get('/api/users/lookup', { params: { search: 'Director E2E' } })).json())[0].id;
  const dep = (await (await s.get('/api/data/Departamento/lookup')).json())[0].id;
  const today = new Date().toLocaleDateString('en-CA'); // AAAA-MM-DD local (el servidor corre en esta máquina)

  // Intento de suplantar: fecha pasada y otro solicitante.
  let req = await (await s.post('/api/data/Requisicion', { data: {
    Departamento: dep, Almacen: ids.almacen, Justificacion: 'Prueba de campos del sistema',
    Fecha: '2020-01-01', Solicitante: otherUser, Partidas: [{ Producto: ids.caja, Cantidad: 1 }],
  } })).json();
  expect(req.Fecha).toBe(today);
  expect(req.Solicitante__label).toBe('Solicitante E2E');

  // Tampoco se cambian al editar (CA2), ni por API directa.
  req = await (await s.put(`/api/data/Requisicion/${req.id}`, { data: { _rowVersion: req._rowVersion, Fecha: '2020-01-01', Solicitante: otherUser } })).json();
  expect(req.Fecha).toBe(today);
  expect(req.Solicitante__label).toBe('Solicitante E2E');

  // CA3: la pantalla los muestra bloqueados y lo explica.
  const page = await pageAs(browser, 'solicitante');
  await page.goto('/c/Requisicion/new');
  await expect(page.getByLabel(/^Fecha(?! requerida)/)).toBeDisabled();
  await expect(page.getByLabel(/^Solicitante/)).toBeDisabled();
  await expect(page.getByText('Lo asigna el sistema').first()).toBeVisible();
  await expect(page.getByLabel(/^Solicitante/)).toHaveValue('Solicitante E2E');
  await page.context().close();
});

test('Spec 004 · H3: cada partida sugiere el almacén de la requisición y se puede cambiar', async () => {
  const s = await as('solicitante');
  const otro = await (await (await apiAs(ADMIN.username, ADMIN.password)).post('/api/data/Almacen', { data: { Clave: 'ALM-02', Nombre: 'Almacén refacciones' } })).json();
  const computed = (await (await s.post('/api/data/Requisicion/compute', { data: { values: {
    Almacen: ids.almacen, Partidas: [{ Producto: ids.caja, Cantidad: 1 }, { Producto: ids.caja, Cantidad: 2, Almacen: otro.id }],
  } } })).json()).values;
  expect(computed.Partidas[0].Almacen).toBe(ids.almacen);   // sugerido desde el encabezado
  expect(computed.Partidas[1].Almacen).toBe(otro.id);       // elegido por partida
  expect(computed.Partidas[1].Almacen__label).toBe('Almacén refacciones');

  const missing = await s.post('/api/data/Requisicion', { data: { Departamento: 1, Justificacion: 'x', Partidas: [] } });
  expect((await missing.json()).errors.Almacen).toBeTruthy(); // almacén destino obligatorio
});

test('Spec 004: "Autorizado por" solo lo asigna la transición, no la captura', async () => {
  const c = await as('comprador');
  const director = (await (await c.get('/api/users/lookup', { params: { search: 'Director E2E' } })).json())[0].id;
  const oc = await newOrder('comprador', [{ producto: ids.caja, cantidad: 1 }], { AutorizadoPor: director, FechaAutorizacion: '2026-01-01T00:00:00Z' });
  expect(oc.body.AutorizadoPor).toBeNull();
  expect(oc.body.FechaAutorizacion).toBeNull();
});

test('Spec 004 · H2: una instalación nueva ya incluye todas las actualizaciones', async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const mod = (await (await admin.get('/api/admin/modules')).json()).find((m: { id: string }) => m.id === 'compras');
  expect(mod.installedVersion).toBe(mod.version);
  expect(mod.upgradeAvailable).toBe(false);
  expect((await admin.post('/api/admin/modules/compras/upgrade/preview')).status()).toBe(409);
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

// ---------------- Spec 005 (Compras 1.2.0): cantidades recibidas y pendientes ----------------

type Doc = { id: number; _rowVersion: string; Folio: string; Partidas: { id: number }[]; [k: string]: unknown };

/** Orden lista para recibir: creada, autorizada por el gerente y enviada al proveedor. */
async function sentOrder(lines: { producto: number; cantidad: number; precio?: number }[]): Promise<Doc> {
  const c = await as('comprador');
  const res = await c.post('/api/data/OrdenCompra', { data: {
    Proveedor: ids.proveedor, Almacen: ids.almacen,
    Partidas: lines.map((l) => ({ Producto: l.producto, Cantidad: l.cantidad, ...(l.precio ? { PrecioUnitario: l.precio } : {}) })),
  } });
  expect(res.status(), await res.text()).toBe(201);
  const sent = await transition('comprador', 'OrdenCompra', await res.json(), 'enviar');
  const ok = await transition('gerente', 'OrdenCompra', sent.body, 'autorizar');
  const out = await transition('comprador', 'OrdenCompra', ok.body, 'enviarProveedor');
  expect(out.body.Estado).toBe('Enviada');
  return out.body;
}

async function confirmReception(oc: { id: number }, partidas: { PartidaOrden: number; Cantidad: number }[], factura: string) {
  const a = await as('almacen');
  const res = await a.post('/api/data/Recepcion', { data: { OrdenCompra: oc.id, Factura: factura, Partidas: partidas } });
  expect(res.status(), await res.text()).toBe(201);
  return transition('almacen', 'Recepcion', await res.json(), 'confirmar');
}

let partialOc: Doc;

test('Spec 005 · H2 CA1 · CA2: la orden muestra lo pendiente y el buscador solo ofrece partidas de la orden elegida', async () => {
  partialOc = await sentOrder([{ producto: ids.caja, cantidad: 10, precio: 20 }, { producto: ids.pelicula, cantidad: 4 }]);
  const lines = partialOc.Partidas as unknown as { id: number; CantidadRecibida: number; Pendiente: number }[];
  expect(lines.map((l) => [l.CantidadRecibida, l.Pendiente])).toEqual([[0, 10], [0, 4]]);
  expect(partialOc.PendientePorRecibir).toBe(14);
  // Las transiciones de recepción ya no se ofrecen a personas.
  const offered = (partialOc._workflow as { transitions: { name: string }[] }).transitions.map((t) => t.name);
  expect(offered).not.toContain('recibir');
  expect(offered).not.toContain('recibirParcial');

  const a = await as('almacen');
  // Regresión (F-01, spec 005): filtrar por un campo «documento padre» respondía 400.
  const lookup = await (await a.get('/api/data/OrdenCompraPartida/lookup', { params: { filterField: 'Orden', filterValue: String(partialOc.id) } })).json();
  expect(lookup.map((x: { id: number }) => x.id).sort()).toEqual(lines.map((l) => l.id).sort());
  expect(lookup.map((x: { label: string }) => x.label)).toContain('CAJ-30 · Caja corrugada 30×30×30 cm · pendiente 10.00');
  // Sin orden elegida no hay nada que ofrecer; un filtro por un campo inexistente se rechaza.
  expect(await (await a.get('/api/data/OrdenCompraPartida/lookup', { params: { filterField: 'Orden', filterValue: '' } })).json()).toEqual([]);
  expect((await a.get('/api/data/OrdenCompraPartida/lookup', { params: { filterField: 'Orden]; DROP TABLE x;--', filterValue: '1' } })).status()).toBe(400);

  // Sugerencias al ligar: producto y cantidad pendiente.
  const computed = (await (await a.post('/api/data/Recepcion/compute', { data: { values: {
    OrdenCompra: partialOc.id, Partidas: [{ PartidaOrden: lines[1].id }],
  } } })).json()).values;
  expect(computed.Partidas[0].Producto).toBe(ids.pelicula);
  expect(computed.Partidas[0].Cantidad).toBe(4);
});

test('Spec 005 · H2 CA4: no se recibe más de lo pendiente ni una partida de otra orden', async () => {
  const a = await as('almacen');
  const over = await a.post('/api/data/Recepcion', { data: { OrdenCompra: partialOc.id, Partidas: [{ PartidaOrden: partialOc.Partidas[0].id, Cantidad: 11 }] } });
  expect(over.status()).toBe(400);
  expect((await over.json()).errors['Partidas[0].Cantidad'].join(' ')).toMatch(/excede lo pendiente/);

  const other = (await (await a.get(`/api/data/OrdenCompra/${Number(ocUiUrl.split('/').pop())}`)).json()).Partidas[0].id;
  const foreign = await a.post('/api/data/Recepcion', { data: { OrdenCompra: partialOc.id, Partidas: [{ PartidaOrden: other, Cantidad: 1 }] } });
  expect(foreign.status()).toBe(400);
  expect((await foreign.json()).errors['Partidas[0].PartidaOrden'].join(' ')).toMatch(/no pertenece/);

  // Nadie marca la orden recibida a mano: la transición es del sistema, aunque se llame por API.
  const manual = await transition('almacen', 'OrdenCompra', partialOc, 'recibir');
  expect(manual.status).toBe(400);
  expect(manual.body.title).toMatch(/la ejecuta el sistema/);
});

test('Spec 005 · H2 CA3 · CA5: una recepción parcial acumula, actualiza el costo y deja la orden en «Recibida parcial»', async () => {
  const c = await as('comprador');
  expect((await (await c.get(`/api/data/Producto/${ids.caja}`)).json()).CostoUltimo).toBe(18.5);
  const confirmed = await confirmReception(partialOc, [{ PartidaOrden: partialOc.Partidas[0].id, Cantidad: 6 }], 'F-005-1');
  expect(confirmed.status, JSON.stringify(confirmed.body)).toBe(200);

  const oc = await (await c.get(`/api/data/OrdenCompra/${partialOc.id}`)).json();
  expect(oc.Estado).toBe('RecibidaParcial');
  expect(oc.Partidas[0].CantidadRecibida).toBe(6);
  expect(oc.Partidas[0].Pendiente).toBe(4);
  expect(oc.PendientePorRecibir).toBe(8);
  // CA5: el costo último toma el precio neto de la orden en pesos.
  expect((await (await c.get(`/api/data/Producto/${ids.caja}`)).json()).CostoUltimo).toBe(20);
});

test('Spec 005 · H2: el almacenista liga la partida desde la UI y al completar la orden se recibe sola', async ({ browser }) => {
  const page = await pageAs(browser, 'almacen');
  await page.goto('/c/Recepcion/new');
  await page.getByRole('button', { name: 'Agregar partida' }).click();
  // Sin orden elegida, el buscador de la partida lo explica.
  await page.getByLabel('Partida de la orden, partida 1').click();
  await expect(page.getByText('Elige primero orden de compra')).toBeVisible();
  await page.keyboard.press('Escape');

  await pick(page, /^Orden de compra/, partialOc.Folio, new RegExp(partialOc.Folio));
  await pick(page, 'Partida de la orden, partida 1', 'PEL', /PEL-ST .* pendiente 4\.00/);
  await expect(page.getByLabel('Producto, partida 1')).toHaveValue(/Película estirable/);
  await expect(page.getByLabel('Cantidad recibida, partida 1')).toHaveValue('4');
  await page.getByLabel(/^Factura del proveedor/).fill('F-005-2');
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await page.getByRole('button', { name: 'Confirmar recepción' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirmar recepción' }).click();
  await expect(page.getByText('Estado: Confirmada')).toBeVisible();

  // Falta la caja: sigue parcial. La última recepción completa la orden.
  const c = await as('comprador');
  expect((await (await c.get(`/api/data/OrdenCompra/${partialOc.id}`)).json()).Estado).toBe('RecibidaParcial');
  expect((await confirmReception(partialOc, [{ PartidaOrden: partialOc.Partidas[0].id, Cantidad: 4 }], 'F-005-3')).status).toBe(200);

  await page.goto(`/c/OrdenCompra/${partialOc.id}`);
  await expect(page.getByText('Estado', { exact: true }).locator('..').getByText('Recibida', { exact: true })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Recibido' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Pendiente', exact: true })).toBeVisible();
  // Spec 005 · H1 CA6: el historial dice que el cambio fue automático y desde qué recepción.
  await expect(page.getByText(/Automática: Acumular lo recibido en la orden \(Recepción REC-\d{6}\)/).first()).toBeVisible();
  await page.context().close();

  const history = await (await c.get(`/api/data/OrdenCompra/${partialOc.id}/history`)).json();
  expect(history.map((h: { to: string }) => h.to)).toEqual(['Borrador', 'PorAutorizar', 'Autorizada', 'Enviada', 'RecibidaParcial', 'Recibida']);
});
