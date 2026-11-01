// Spec 043 · Módulo de Ventas. Corre después de valuacion-contable.spec.ts (Contabilidad y Costeo instalados, el contador de
// esa prueba y el periodo del mes) y antes de visual.spec.ts. Instala Ventas desde la UI; los datos de apoyo se preparan por
// API y lo que se prueba (precio sugerido, conversión en pedido, aviso de crédito, surtido, factura, cobro, modelador y
// tablero) se revisa por la UI. Los saldos, las pólizas, la concurrencia y la seguridad por API están en integración
// (tests/Erp.IntegrationTests/SalesTests.cs).
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de ventas 2043';
const users = { vendedor: 'e2e-vendedor', gerente: 'e2e-gerente-ventas', surtidor: 'e2e-surtidor', cobranza: 'e2e-cobranza' } as const;
const ROLES: Record<keyof typeof users, string> = { vendedor: 'Vendedor', gerente: 'Gerente de ventas', surtidor: 'Surtidor', cobranza: 'Facturación y cobranza' };
type Who = keyof typeof users | 'admin' | 'contador';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password)
  : w === 'contador' ? await apiAs('e2e-contador', 'frase larga de compras 2026') : await apiAs(users[w], PWD));

const get = async (w: Who, url: string, params?: Record<string, string | number>) => (await (await as(w)).get(url, { params })).json();
async function post(w: Who, url: string, data: object) {
  const res = await (await as(w)).post(url, { data });
  return { status: res.status(), body: (await res.json().catch(() => null)) as Row };
}
const transition = async (w: Who, entity: string, row: Row, name: string) =>
  post(w, `/api/data/${entity}/${row.id}/transition`, { transition: name, _rowVersion: row._rowVersion });

const CLAVE = 'VTA-E2E-01';
const ids = { almacen: 0, producto: 0, mxn: 0, cliente: 0, banco: 0 };
const RFC = 'VEN430101E2E';

async function pageAs(browser: Browser, w: keyof typeof users): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

/** Elige una opción de un campo de referencia como una persona: la opción buscada debe verse completa (F-20 de la 003). */
async function pick(page: Page, label: string | RegExp, search: string, option: RegExp) {
  await page.getByLabel(label).fill(search);
  await expect(page.getByRole('listbox').getByRole('option').first()).toHaveAccessibleName(option);
  const opt = page.getByRole('option', { name: option }).first();
  await expect(opt).toBeVisible();
  const visibleToHuman = await opt.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.bottom <= window.innerHeight && (hit === el || el.contains(hit));
  });
  expect(visibleToHuman, `la opción ${option} debe verse completa`).toBe(true);
  await opt.click();
}

/** Ejecuta una acción del proceso desde la UI: botón y confirmación del diálogo. */
async function act(page: Page, name: string) {
  await page.getByRole('button', { name, exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name, exact: true }).click();
}

const estado = (page: Page) => page.getByRole('group', { name: 'Proceso del documento' });

async function find(w: Who, entity: string, field: string, value: unknown): Promise<Row> {
  const r = await get(w, `/api/data/${entity}`, { pageSize: 200 });
  const row = (r.items as Row[]).find((x) => x[field] === value);
  expect(row, `${entity} con ${field} = ${String(value)}`).toBeTruthy();
  return get(w, `/api/data/${entity}/${row!.id}`);
}

test('H6 CA1: Ventas se instala desde la UI con su vista previa y queda entre Compras e Inventario', async ({ page }) => {
  const admin = await as('admin');
  const installed = ((await (await admin.get('/api/admin/modules')).json()) as { id: string; installedVersion: string | null }[])
    .find((m) => m.id === 'ventas')?.installedVersion;
  await page.goto('/admin/modules');
  const card = page.getByRole('article').filter({ has: page.getByRole('heading', { name: 'Ventas' }) });
  await expect(card.getByText(/Usa Inventario, Contabilidad, Costeo/)).toBeVisible();
  if (!installed) { // en modo independiente puede venir instalado de una vuelta anterior
    await page.getByRole('button', { name: 'Instalar Ventas' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('El plan es válido')).toBeVisible();
    // Roles nuevos del módulo («Vendedor» y «Gerente de ventas» ya los crea seguridad.spec.ts: R-043-3).
    await expect(dialog.getByText(/Surtidor/).first()).toBeVisible();
    await expect(dialog.getByText(/Definir 1 automatización\(es\) en 'Cotización': Crear el pedido de la cotización/)).toBeVisible();
    await expectAccessible(page);
    await dialog.getByRole('button', { name: 'Instalar módulo' }).click();
    await expect(page.getByText('Módulo instalado')).toBeVisible({ timeout: 30_000 });
  }
  await page.goto('/');
  const cards = page.getByRole('main').getByRole('list').getByRole('link');
  await expect(cards.first()).toBeVisible();
  const names = await cards.evaluateAll((as) => as.map((a) => a.querySelector('.entity-card-title')?.textContent));
  expect(names.slice(0, 3)).toEqual(['Compras', 'Ventas', 'Inventario']);
});

test('preparar usuarios, almacén con cuentas y existencia, y periodo abierto', async () => {
  const admin = await as('admin');
  for (const [w, u] of Object.entries(users) as [keyof typeof users, string][]) {
    const res = await admin.post('/api/admin/users', { data: { username: u, displayName: `${ROLES[w]} E2E`, password: PWD, roleIds: [await roleId(admin, ROLES[w])] } });
    expect([201, 409], await res.text()).toContain(res.status());
  }
  const cuentas: Record<string, number> = {};
  for (const c of (await get('admin', '/api/data/CuentaContable', { pageSize: 200 })).items as Row[]) cuentas[c.Codigo as string] = c.id;
  ids.banco = cuentas['1100'];
  ids.mxn = ((await get('admin', '/api/data/Moneda', { search: 'MXN' })).items as Row[])[0].id;
  const unidad = ((await get('admin', '/api/data/UnidadMedida', { search: 'PZA' })).items as Row[])[0].id;
  const accounts = { CuentaInventario: cuentas['1200'], CuentaEntradas: cuentas['2150'], CuentaCostoVentas: cuentas['5100'], CuentaVentas: cuentas['4100'],
    CuentaClientes: cuentas['1150'], CuentaIvaTrasladado: cuentas['2160'] };
  const existing = ((await get('admin', '/api/data/Almacen', { search: 'VTA43' })).items as Row[])[0];
  ids.almacen = existing?.id ?? (await post('admin', '/api/data/Almacen', { Clave: 'VTA43', Nombre: 'Almacén ventas E2E', ...accounts })).body.id;
  const prod = ((await get('admin', '/api/data/Producto', { search: CLAVE })).items as Row[])[0];
  ids.producto = prod?.id ?? (await post('admin', '/api/data/Producto', { Clave: CLAVE, Descripcion: 'Taladro de banco E2E', Unidad: unidad, PrecioVenta: 250, MonedaPrecio: 'MXN' })).body.id;
  expect(ids.almacen * ids.producto).toBeGreaterThan(0);
  expect((await post('admin', '/api/data/MovimientoInventario', { Tipo: 'Entrada', Almacen: ids.almacen, Producto: ids.producto, Cantidad: 50, CostoUnitario: 100 })).status).toBe(201);

  // El periodo del mes lo crea valuacion-contable.spec.ts (y lo deja abierto); en modo independiente se asegura aquí.
  const today = new Date().toISOString().slice(0, 10);
  const periodos = (await get('admin', '/api/data/PeriodoContable', { pageSize: 100 })).items as Row[];
  const vigente = periodos.find((p) => String(p.Inicio) <= today && String(p.Fin) >= today);
  if (!vigente) {
    const y = new Date().getFullYear();
    expect((await post('admin', '/api/data/PeriodoContable', { Nombre: `Ventas ${y}`, Inicio: `${y}-01-01`, Fin: `${y}-12-31` })).status).toBe(201);
  } else if (vigente.Estado === 'Cerrado') {
    expect((await post('admin', `/api/data/PeriodoContable/${vigente.id}/transition`, { transition: 'reabrir', comment: 'Ventas E2E', _rowVersion: vigente._rowVersion })).status).toBe(200);
  }
});

test('H2 CA1 · CA2 · H3 CA1 · CA2 (UI): el vendedor da de alta su cliente, cotiza con el precio sugerido y al aceptar se crea el pedido', async ({ browser }) => {
  const page = await pageAs(browser, 'vendedor');
  const previo = ((await get('vendedor', '/api/data/Cliente', { search: RFC })).items as Row[])[0];
  if (previo) ids.cliente = previo.id;
  else {
    await page.goto('/c/Cliente/new');
    await page.getByLabel(/^RFC/).fill('VEN43');
    await page.getByLabel(/^Razón social/).fill('Ferretería La Ñandú, S.A. de C.V.');
    await pick(page, /^Moneda/, 'MXN', /MXN/);
    await page.getByRole('button', { name: 'Crear' }).click();
    await expect(page.getByText('El RFC debe tener 12 (persona moral) o 13 caracteres (persona física).')).toBeVisible();
    await page.getByLabel(/^RFC/).fill(RFC);
    await expectAccessible(page);
    await page.getByRole('button', { name: 'Crear' }).click();
    await expect(page.getByText('Registro creado')).toBeVisible();
    ids.cliente = Number(page.url().split('/').at(-1));
    // El saldo lo calcula el sistema: se ve, no se captura.
    await expect(page.getByLabel(/^Saldo por cobrar/)).not.toBeEditable();
  }

  await page.goto('/c/Cotizacion/new');
  await expect(page.getByRole('heading', { name: 'Registrar cotización' })).toBeVisible();
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveCount(0); // en la moneda base no se muestra
  await pick(page, /^Cliente/, 'Ñandú', /Ferretería La Ñandú/);
  await expect(page.getByLabel(/^Moneda/)).toHaveValue('MXN'); // la del cliente
  await pick(page, /^Almacén que surte/, 'ventas E2E', /Almacén ventas E2E/);
  await page.getByRole('button', { name: 'Agregar partida' }).click();
  await pick(page, 'Producto, partida 1', 'Taladro', /Taladro de banco E2E/);
  await expect(page.getByLabel('Precio unitario, partida 1')).toHaveValue('250'); // H2 CA2: precio de venta en la moneda del documento
  await page.getByLabel('Cantidad, partida 1').fill('2');
  await expect(page.getByText('580.00').first()).toBeVisible(); // 2 × 250 + 16 % IVA, en vivo
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await act(page, 'Enviar al cliente');
  await expect(estado(page)).toContainText('Enviada al cliente');
  await act(page, 'Aceptar y crear pedido');
  await expect(estado(page)).toContainText('Aceptada');
  const folio = page.getByLabel(/^Pedido/);
  await expect(folio).toHaveValue(/^PED-\d{6}$/);
  const pedido = await find('vendedor', 'Pedido', 'Folio', await folio.inputValue());
  expect(pedido.Estado).toBe('Borrador');
  expect(pedido.Partidas).toEqual([expect.objectContaining({ Producto: ids.producto, Cantidad: 2, PrecioUnitario: 250 })]);
  await page.context().close();
});

test('H2 CA3 (UI): sobre el límite, el vendedor ve el aviso y no confirma; el gerente confirma aceptando el aviso', async ({ browser }) => {
  const cliente = (await get('gerente', `/api/data/Cliente/${ids.cliente}`)) as Row;
  const limite = await (await as('gerente')).put(`/api/data/Cliente/${ids.cliente}`, { data: { LimiteCredito: 100, _rowVersion: cliente._rowVersion } });
  expect(limite.status(), await limite.text()).toBe(200);
  const pedido = (await post('vendedor', '/api/data/Pedido', { Cliente: ids.cliente, Almacen: ids.almacen, Partidas: [{ Producto: ids.producto, Cantidad: 2 }] })).body;

  const page = await pageAs(browser, 'vendedor');
  await page.goto(`/c/Pedido/${pedido.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'Avisos' })).toContainText('supera su límite de crédito');
  await page.getByRole('button', { name: 'Confirmar pedido', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Confirmar pedido', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Solo el gerente de ventas confirma un pedido sobre el límite.');
  await page.keyboard.press('Escape');
  await page.context().close();

  const g = await pageAs(browser, 'gerente');
  await g.goto(`/c/Pedido/${pedido.id}`);
  await act(g, 'Confirmar pedido');
  const confirm = g.getByRole('dialog');
  await expect(confirm.getByRole('alert')).toContainText('Confirma los avisos para continuar');
  await expect(confirm.getByRole('alert')).toContainText('supera su límite de crédito');
  await expectAccessible(g);
  await confirm.getByRole('button', { name: 'Continuar con avisos' }).click();
  await expect(confirm).toBeHidden();
  await expect(estado(g)).toContainText('Confirmado');
  await g.context().close();
  // Para lo que sigue, el cliente queda sin límite.
  const c2 = (await get('gerente', `/api/data/Cliente/${ids.cliente}`)) as Row;
  expect((await (await as('gerente')).put(`/api/data/Cliente/${ids.cliente}`, { data: { LimiteCredito: null, _rowVersion: c2._rowVersion } })).status()).toBe(200);
});

let pedidoSurtido: Row;

test('H4 CA1 · CA2 · CA4 (UI): el surtidor confirma la salida sin ver costos; el pedido queda surtido y la póliza de costo de ventas aplicada', async ({ browser }) => {
  const nuevo = (await post('vendedor', '/api/data/Pedido', { Cliente: ids.cliente, Almacen: ids.almacen, Partidas: [{ Producto: ids.producto, Cantidad: 3 }] })).body;
  pedidoSurtido = (await transition('vendedor', 'Pedido', nuevo, 'confirmar')).body;
  expect(pedidoSurtido.Estado).toBe('Confirmado');

  const page = await pageAs(browser, 'surtidor');
  await page.goto('/c/Embarque/new');
  await pick(page, /^Pedido/, String(pedidoSurtido.Folio), new RegExp(String(pedidoSurtido.Folio)));
  await expect(page.getByLabel(/^Almacén/)).toHaveValue(/Almacén ventas E2E/);
  await page.getByRole('button', { name: 'Agregar partida' }).click();
  await pick(page, 'Partida del pedido, partida 1', CLAVE, /VTA-E2E-01 .* por surtir 3\.00/);
  await expect(page.getByLabel('Cantidad, partida 1')).toHaveValue('3');
  await expect(page.getByText(/Costo/)).toHaveCount(0); // H4 CA4: ni la columna ni el total
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await act(page, 'Confirmar salida');
  await expect(estado(page)).toContainText('Confirmado');
  const folio = (await page.getByRole('heading', { level: 1 }).textContent())!.match(/EMB-\d{6}/)![0];
  await page.context().close();

  pedidoSurtido = (await get('vendedor', `/api/data/Pedido/${pedidoSurtido.id}`)) as Row;
  expect(pedidoSurtido.Estado).toBe('Surtido');
  const embarque = await find('gerente', 'Embarque', 'Folio', folio);
  expect(embarque.CostoTotal).toBe(300); // 3 × 100, promedio
  const poliza = await find('contador', 'Poliza', 'Referencia', folio);
  expect(poliza).toEqual(expect.objectContaining({ Estado: 'Aplicada', Origen: 'Automática', TotalCargos: 300, TotalAbonos: 300 }));
});

test('H5 CA1 · CA2 (UI): facturación emite la factura de lo surtido y la descarga en PDF; el saldo del cliente sube', async ({ browser }) => {
  const antes = Number(((await get('cobranza', `/api/data/Cliente/${ids.cliente}`)) as Row).Saldo);
  const page = await pageAs(browser, 'cobranza');
  await page.goto('/c/Factura/new');
  await pick(page, /^Pedido/, String(pedidoSurtido.Folio), new RegExp(String(pedidoSurtido.Folio)));
  await expect(page.getByLabel(/^Cliente/)).toHaveValue(/Ferretería La Ñandú/);
  await page.getByRole('button', { name: 'Agregar partida' }).click();
  await pick(page, 'Partida del pedido, partida 1', CLAVE, /VTA-E2E-01 .* por facturar 3\.00/);
  await expect(page.getByLabel('Cantidad, partida 1')).toHaveValue('3');
  await expect(page.getByText('870.00').first()).toBeVisible(); // 3 × 250 + IVA
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await act(page, 'Emitir factura');
  await expect(estado(page)).toContainText('Emitida');
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Descargar PDF' }).click(); // una sola plantilla: descarga directa
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^Factura-FAC-\d{6}\.pdf$/);
  await page.context().close();
  expect(Number(((await get('cobranza', `/api/data/Cliente/${ids.cliente}`)) as Row).Saldo)).toBe(antes + 870);
  expect(((await get('vendedor', `/api/data/Pedido/${pedidoSurtido.id}`)) as Row).Estado).toBe('Facturado');
});

test('H5 CA3 (UI): el cobro sugiere el saldo de la factura; al aplicarlo la factura queda pagada y la póliza de ingreso aplicada', async ({ browser }) => {
  const factura = ((await get('cobranza', '/api/data/Factura', { pageSize: 200 })).items as Row[]).find((f) => f.Cliente === ids.cliente && f.Estado === 'Emitida')!;
  const page = await pageAs(browser, 'cobranza');
  await page.goto('/c/Cobro/new');
  await pick(page, /^Cliente/, 'Ñandú', /Ferretería La Ñandú/);
  await pick(page, /^Cuenta de banco/, '1100', /1100 · Bancos/);
  await page.getByRole('button', { name: 'Agregar partida' }).click();
  await pick(page, 'Factura, partida 1', String(factura.Folio), new RegExp(String(factura.Folio)));
  await expect(page.getByLabel('Importe aplicado, partida 1')).toHaveValue(String(factura.Saldo));
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await act(page, 'Aplicar cobro');
  await expect(estado(page)).toContainText('Aplicado');
  const folio = (await page.getByRole('heading', { level: 1 }).textContent())!.match(/COB-\d{6}/)![0];
  await page.context().close();
  expect(((await get('cobranza', `/api/data/Factura/${factura.id}`)) as Row).Estado).toBe('Pagada');
  const poliza = await find('contador', 'Poliza', 'Referencia', folio);
  expect(poliza).toEqual(expect.objectContaining({ Estado: 'Aplicada', Tipo: 'Ingreso', TotalCargos: 870, TotalAbonos: 870 }));
});

test('H1 CA3 (modelador): la plantilla de partidas ofrece «Repetir por cada partida de», sin JSON', async ({ page }) => {
  await page.goto('/admin/model?edit=Cotizacion&tab=automatizaciones');
  await page.getByRole('button', { name: /^Crear el pedido de la cotización/ }).click();
  const from = page.getByLabel('Partida 1: repetir por cada partida de');
  await expect(from).toHaveValue('Partidas');
  await expect(from.locator('option')).toHaveText(['No: una sola partida', 'Partidas']);
  await expectAccessible(page);
});

test('H6 CA3: el tablero Ventas muestra el margen al gerente y no al vendedor, que arma su tablero personal con lo suyo', async ({ browser }) => {
  const g = await pageAs(browser, 'gerente');
  await g.goto('/dashboards/Ventas');
  await expect(g.getByRole('heading', { name: 'Margen por producto' })).toBeVisible();
  await expect(g.getByRole('heading', { name: 'Productos más vendidos' })).toBeVisible();
  await expectAccessible(g);
  await g.context().close();

  const res = await post('vendedor', '/api/my-dashboards', { label: `Mis ventas E2E ${Date.now()}`, widgets: [
    { type: 'table', entity: 'PedidoPartida', report: 'ProductosMasVendidos' }, { type: 'kpi', entity: 'Factura', report: 'MontoFacturado' }] });
  expect(res.status, JSON.stringify(res.body)).toBe(201);
  const v = await pageAs(browser, 'vendedor');
  await v.goto('/dashboards/Ventas');
  await expect(v.getByRole('heading', { name: 'Productos más vendidos' })).toBeVisible();
  await expect(v.getByRole('heading', { name: 'Margen por producto' })).toHaveCount(0);
  await v.goto(`/my-dashboards/${res.body.id}`);
  await expect(v.getByRole('cell', { name: /Taladro de banco E2E/ }).first()).toBeVisible();
  await v.context().close();
});
