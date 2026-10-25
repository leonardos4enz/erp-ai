// Spec 022 · Costeo 2: políticas de valuación configurables. Corre después de valuacion-contable.spec.ts (Contabilidad y
// Costeo instalados, contador y periodo del mes) y antes de visual.spec.ts. Cada política se prueba con su valor por defecto
// (el que instalan Inventario 1.6.0 y Costeo 1.1.0) y, donde la alternativa es un cambio de metadato, también con ella;
// el metadato se restaura al terminar la prueba.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test, uiLogin } from './helpers';

// Al verificar contra el código anterior (E2E_INDEPENDENT=1) una falla no salta las pruebas siguientes.
test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de compras 2026';
const users = { comprador: 'e2e-comprador', gerente: 'e2e-gerente', almacen: 'e2e-almacen', supervisor: 'e2e-supervisor', contador: 'e2e-contador' } as const;
type Who = keyof typeof users | 'admin';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };
type Body = Row & { title?: string; errors?: Record<string, string[]>; items: Row[] };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
async function call(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Body };
}
const post = async (w: Who, url: string, data: object) => call(await (await as(w)).post(url, { data }));
const put = async (w: Who, url: string, data: object) => call(await (await as(w)).put(url, { data }));
const get = async (w: Who, url: string, params?: Record<string, string | number>) => (await (await as(w)).get(url, { params })).json();
const transition = async (w: Who, entity: string, row: Row, name: string, extra: object = {}) =>
  post(w, `/api/data/${entity}/${row.id}/transition`, { transition: name, _rowVersion: row._rowVersion, ...extra });

const pad = (n: number) => String(n).padStart(2, '0');
const iso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const now = new Date();
const TODAY = iso(now);
const MONTH_START = iso(new Date(now.getFullYear(), now.getMonth(), 1));
const MONTH_END = iso(new Date(now.getFullYear(), now.getMonth() + 1, 0));
/** Una fecha del mes en curso (periodo abierto) distinta de hoy. */
const OTHER_DAY = TODAY === MONTH_START ? MONTH_END : MONTH_START;
const money = (n: number) => Math.round(n * 100) / 100;

const ids = { almacen1: 0, almacen2: 0, proveedor: 0, rodamiento: 0, a: 0, b: 0, c: 0, d: 0 };

async function ensureIds() {
  if (ids.almacen1) return;
  const almacenes = (await get('admin', '/api/data/Almacen', { pageSize: 50 })).items as Row[];
  ids.almacen1 = almacenes.find((a) => a.Clave === 'ALM-01')!.id; // con cuentas contables (valuacion-contable.spec.ts)
  ids.almacen2 = almacenes.find((a) => a.Clave === 'ALM-02')!.id; // sin cuentas
  ids.rodamiento = (await get('admin', '/api/data/Producto', { search: 'ROD-6204' })).items[0].id;
  ids.proveedor = (await get('admin', '/api/data/Proveedor', { search: 'Cartones' })).items[0].id;
  // Productos de esta prueba (se crean una vez; así cada prueba puede correr sola con E2E_INDEPENDENT).
  const rodamiento = (await get('admin', `/api/data/Producto/${ids.rodamiento}`)) as Row;
  for (const [key, metodo, costo] of [['a', 'FIFO', 10], ['b', 'Promedio', 12.5], ['c', 'FIFO', 10], ['d', 'Promedio', 0]] as const) {
    ids[key] = ((await get('admin', '/api/data/Producto', { search: `POL-${key.toUpperCase()}` })).items as Row[])[0]?.id ?? 0;
    if (ids[key]) continue;
    const p = await post('admin', '/api/data/Producto', { Clave: `POL-${key.toUpperCase()}`, Descripcion: `Política ${key} (${metodo})`,
      Unidad: rodamiento.Unidad, Categoria: rodamiento.Categoria, MetodoCosteo: metodo, CostoUltimo: costo });
    expect(p.status, JSON.stringify(p.body)).toBe(201);
    ids[key] = p.body.id;
  }
}
test.beforeEach(ensureIds);

async function existencia(almacen: number, producto: number) {
  const r = await get('supervisor', '/api/data/Existencia', { pageSize: 500 });
  return r.items.find((x: Row) => x.Almacen === almacen && x.Producto === producto) as Row | undefined;
}
async function movimiento(w: Who, data: object) {
  const r = await post(w, '/api/data/MovimientoInventario', data);
  expect(r.status, JSON.stringify(r.body)).toBe(201);
  return r.body;
}
async function movimientos(folio: string) {
  return (await get('almacen', '/api/data/MovimientoInventario', { search: folio, pageSize: 20 })).items as Row[];
}
async function ordenEnviada(data: object): Promise<Row> {
  const created = await post('comprador', '/api/data/OrdenCompra', data);
  expect(created.status, JSON.stringify(created.body)).toBe(201);
  const sent = await transition('comprador', 'OrdenCompra', created.body, 'enviar');
  const auth = await transition('gerente', 'OrdenCompra', sent.body, 'autorizar');
  const oc = await transition('comprador', 'OrdenCompra', auth.body, 'enviarProveedor');
  expect(oc.status, JSON.stringify(oc.body)).toBe(200);
  return oc.body;
}
async function recepcion(oc: Row, extra: object = {}) {
  const r = await post('almacen', '/api/data/Recepcion', { OrdenCompra: oc.id, Partidas: [{ PartidaOrden: (oc.Partidas as Row[])[0].id }], ...extra });
  expect(r.status, JSON.stringify(r.body)).toBe(201);
  return r.body;
}
const fresh = async (entity: string, id: number) => (await get('almacen', `/api/data/${entity}/${id}`)) as Row;
async function traspaso(partidas: { Producto: number; Cantidad: number }[], origen = ids.almacen1, destino = ids.almacen2) {
  const tra = await post('almacen', '/api/data/Traspaso', { AlmacenOrigen: origen, AlmacenDestino: destino, Partidas: partidas });
  expect(tra.status, JSON.stringify(tra.body)).toBe(201);
  const applied = await transition('almacen', 'Traspaso', tra.body, 'aplicar');
  expect(applied.status, JSON.stringify(applied.body)).toBe(200);
  return tra.body;
}

type Entity = { rules?: { name: string }[]; automations?: { name: string; actions: Record<string, unknown>[] }[] };
const model = async (entity: string) => (await get('admin', `/api/model/entities/${entity}`)) as Entity;
/** Quita una regla (la alternativa de la política) y la devuelve al final, pase lo que pase. */
async function withoutRule(entity: string, rule: string, body: () => Promise<void>) {
  const admin = await as('admin');
  const rules = (await model(entity)).rules ?? [];
  expect(rules.map((r) => r.name)).toContain(rule);
  await applyPlan(admin, { description: `Política: sin ${rule}`, operations: [{ op: 'setRules', entity, rules: rules.filter((r) => r.name !== rule) }] });
  try { await body(); }
  finally { await applyPlan(admin, { description: `Política: con ${rule}`, operations: [{ op: 'setRules', entity, rules }] }); }
}
/** Cambia una opción de la valuación (`Valuar`) y la restaura al final. */
async function withValuation(change: Record<string, unknown>, body: () => Promise<void>) {
  const admin = await as('admin');
  const valuar = (await model('MovimientoInventario')).automations!.find((a) => a.name === 'Valuar')!;
  const apply = (action: Record<string, unknown>) => applyPlan(admin, { description: 'Política de valuación', operations: [
    { op: 'setAutomations', entity: 'MovimientoInventario', merge: true, automations: [{ ...valuar, actions: [action] }] }] });
  await apply({ ...valuar.actions[0], ...change });
  try { await body(); }
  finally { await apply(valuar.actions[0]); }
}

async function pageAs(browser: Browser, w: keyof typeof users): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

test('H8: Inventario 1.6.0 y Costeo 1.1.0 están instalados con las políticas por defecto', async () => {
  const mods = (await get('admin', '/api/admin/modules')) as { id: string; installedVersion?: string; version: string }[];
  expect(mods.find((m) => m.id === 'inventario')).toMatchObject({ installedVersion: '1.8.0' }); // 1.7.0: spec 023; 1.8.0: spec 024 (módulo de navegación)
  expect(mods.find((m) => m.id === 'costeo')).toMatchObject({ installedVersion: '1.1.0' });
  const valuar = (await model('MovimientoInventario')).automations!.find((a) => a.name === 'Valuar')!.actions[0];
  expect(valuar).toMatchObject({ uncostedEntry: 'fallback', methodChange: 'merge', totalCost: 'CostoTotal', quantityField: 'CantidadCosteada' });
  expect((await model('Recepcion')).rules!.map((r) => r.name)).toEqual(expect.arrayContaining(['TipoCambioDeLaRecepcion', 'PolizaConCuentas']));
});

test('H6 CA2 · H1 CA1 · CA2: el traspaso entra al costo real de la salida; el promedio es el de lo costeado', async () => {
  // El rodamiento tiene existencia anterior a Costeo en ALM-01 (sin capas).
  const antes = (await existencia(ids.almacen1, ids.rodamiento))!;
  const legacy = antes.Cantidad as number;
  expect(legacy).toBeGreaterThanOrEqual(3);
  expect(antes).toMatchObject({ Valor: 0, CostoPromedio: null, CosteoIncompleto: true });

  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.rodamiento, Cantidad: 3, CostoUnitario: 17.37 });
  // H6: el promedio es el de lo costeado (3 a 17.37), no 52.11 entre toda la existencia; y avisa que hay existencia sin capas.
  expect(await existencia(ids.almacen1, ids.rodamiento)).toMatchObject({ Cantidad: legacy + 3, Valor: 52.11, CantidadCosteada: 3,
    CostoPromedio: 17.37, CosteoIncompleto: true });

  const destinoAntes = ((await existencia(ids.almacen2, ids.rodamiento))?.Valor as number | null) ?? 0;
  // CA1: 4 unidades, 3 con capas (52.11) y 1 al costo de respaldo.
  const t1 = await traspaso([{ Producto: ids.rodamiento, Cantidad: 4 }]);
  const m1 = await movimientos(t1.Folio as string);
  const salida1 = m1.find((m) => m.Tipo === 'Traspaso salida')!;
  const entrada1 = m1.find((m) => m.Tipo === 'Traspaso entrada')!;
  expect(salida1.CostoTotal).not.toBe(-money(4 * 17.37)); // lo no cubierto salió al respaldo, no al promedio de lo cubierto
  expect(entrada1.CostoTotal).toBe(-(salida1.CostoTotal as number));
  const destino1 = (await existencia(ids.almacen2, ids.rodamiento))!;
  expect(money((destino1.Valor as number) - destinoAntes)).toBe(entrada1.CostoTotal);
  expect(entrada1.Fecha).toBe(t1.Fecha); // los movimientos llevan la fecha del traspaso

  // CA2: el origen ya no tiene capas; la entrada vale lo mismo que la salida (el respaldo), no el promedio del destino.
  const t2 = await traspaso([{ Producto: ids.rodamiento, Cantidad: 1 }]);
  const m2 = await movimientos(t2.Folio as string);
  const salida2 = m2.find((m) => m.Tipo === 'Traspaso salida')!;
  expect(m2.find((m) => m.Tipo === 'Traspaso entrada')!.CostoTotal).toBe(-(salida2.CostoTotal as number));
  expect(await existencia(ids.almacen1, ids.rodamiento)).toMatchObject({ Cantidad: legacy - 2, Valor: 0, CantidadCosteada: 0, CosteoIncompleto: true });
});

test('H1 CA3: dos partidas FIFO del mismo producto consumen capas distintas', async () => {
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.a, Cantidad: 10, CostoUnitario: 10 });
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.a, Cantidad: 10, CostoUnitario: 13 });
  const t = await traspaso([{ Producto: ids.a, Cantidad: 8 }, { Producto: ids.a, Cantidad: 8 }]);
  const m = await movimientos(t.Folio as string);
  const costs = (tipo: string) => m.filter((x) => x.Tipo === tipo).map((x) => x.CostoTotal as number).sort((x, y) => x - y);
  expect(costs('Traspaso salida')).toEqual([-98, -80]); // 8 × 10; 2 × 10 + 6 × 13
  expect(costs('Traspaso entrada')).toEqual([80, 98]);
  expect(await existencia(ids.almacen1, ids.a)).toMatchObject({ Cantidad: 4, Valor: 52, CantidadCosteada: 4, CosteoIncompleto: false });
  expect(await existencia(ids.almacen2, ids.a)).toMatchObject({ Cantidad: 16, Valor: 178, CostoPromedio: 11.125, CosteoIncompleto: false });
  // El costo de cada salida queda en su partida del traspaso.
  const partidas = ((await get('almacen', `/api/data/Traspaso/${t.id}`)) as Row).Partidas as Row[];
  expect(partidas.map((p) => p.CostoOrigen).sort()).toEqual([80, 98]);
});

test('H2 CA2: un ajuste sin costo de un producto sin capas entra al costo último', async () => {
  const ajuste = await movimiento('supervisor', { Tipo: 'Ajuste', Almacen: ids.almacen2, Producto: ids.b, Cantidad: 4, Notas: 'Sobrante sin costo' });
  expect(ajuste).toMatchObject({ CostoTotal: 50, CostoUnitario: 12.5 }); // antes: 0
  expect(await existencia(ids.almacen2, ids.b)).toMatchObject({ Valor: 50, CostoPromedio: 12.5 });
});

test('H2 CA1 · CA3: sin costo ni respaldo se rechaza; con «require» también con respaldo', async () => {
  // Defecto (fallback): el producto D no tiene costo último, así que no hay respaldo.
  const sinRespaldo = await post('supervisor', '/api/data/MovimientoInventario', { Tipo: 'Ajuste', Almacen: ids.almacen2, Producto: ids.d, Cantidad: 2, Notas: 'Sin costo' });
  expect(sinRespaldo.status).toBe(400);
  expect(sinRespaldo.body.title).toMatch(/^No se pudo completar «Valuar el movimiento»: La entrada no tiene costo unitario .* captura el costo unitario\.$/);
  expect(await existencia(ids.almacen2, ids.d)).toBeUndefined(); // se revirtió todo

  // Alternativa: exigir el costo aunque haya respaldo (el producto C tiene costo último 10 y no tiene capas en ALM-01).
  await withValuation({ uncostedEntry: 'require' }, async () => {
    const r = await post('supervisor', '/api/data/MovimientoInventario', { Tipo: 'Ajuste', Almacen: ids.almacen1, Producto: ids.c, Cantidad: 2, Notas: 'Sin costo' });
    expect(r.status).toBe(400);
    expect(r.body.title).toMatch(/captura el costo unitario/);
    // Con el costo capturado sí entra.
    expect((await movimiento('supervisor', { Tipo: 'Ajuste', Almacen: ids.almacen1, Producto: ids.c, Cantidad: 2, CostoUnitario: 9, Notas: 'Con costo' })).CostoTotal).toBe(18);
  });
});

test('H3 CA1 · CA3: sin tipo de cambio a su fecha la recepción no se confirma; sin la regla usa el de la orden', async ({ browser }) => {
  const monedas = (await get('admin', '/api/data/Moneda', { pageSize: 50 })).items as Row[];
  const chf = monedas.find((m) => m.Clave === 'CHF')?.id ?? (await post('admin', '/api/data/Moneda', { Clave: 'CHF', Nombre: 'Franco suizo', Simbolo: 'Fr' })).body.id;
  // Sin tipo de cambio registrado, la orden lo captura a mano (18).
  const oc = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen1, Moneda: chf, TipoCambio: 18,
    Partidas: [{ Producto: ids.b, Cantidad: 2, PrecioUnitario: 5, DescuentoPct: 0 }] });
  const rec = await recepcion(oc, { Factura: 'F-CHF-1' }); // el borrador sí se guarda

  const page = await pageAs(browser, 'almacen');
  await page.goto(`/c/Recepcion/${rec.id}`);
  await page.getByRole('button', { name: 'Confirmar recepción' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirmar recepción' }).click();
  await expect(page.getByRole('dialog').getByText(/No hay tipo de cambio de la moneda de la orden a la moneda base/)).toBeVisible();
  await expectAccessible(page);
  await page.context().close();
  const blocked = await transition('almacen', 'Recepcion', await fresh('Recepcion', rec.id), 'confirmar');
  expect(blocked.status).toBe(400);
  expect(blocked.body.errors!.Fecha[0]).toMatch(/^No hay tipo de cambio/);
  expect(await movimientos(rec.Folio as string)).toHaveLength(0);

  // Alternativa: sin la regla, la recepción toma el tipo de la orden (la moneda base es MXN).
  await withoutRule('Recepcion', 'TipoCambioDeLaRecepcion', async () => {
    const ok = await transition('almacen', 'Recepcion', await fresh('Recepcion', rec.id), 'confirmar');
    expect(ok.status, JSON.stringify(ok.body)).toBe(200);
    expect(await get('admin', `/api/data/Recepcion/${rec.id}`)).toMatchObject({ CostoTotal: 180 }); // el almacenista no lo ve (029, D-05)
    expect(await movimientos(rec.Folio as string)).toEqual([expect.objectContaining({ CostoUnitario: 90, CostoTotal: 180 })]);
    const pol = ((await get('contador', '/api/data/Poliza', { search: rec.Folio as string })).items as Row[])[0];
    expect(pol).toMatchObject({ TotalCargos: 180, TotalAbonos: 180 }); // la póliza y el movimiento valen lo mismo (CA2)
  });
});

test('H3 CA2: en una empresa con otra moneda base, sin tipo de cambio ninguna partida se confirma sin costo (aun sin la regla)', async () => {
  // Con base MXN siempre hay respaldo (el costo de la orden); en base USD no: la regla invariante CostoCalculado detiene la
  // confirmación aunque se quite la política TipoCambioDeLaRecepcion. Empresa propia, que se desactiva al terminar.
  const admin = await apiAs(ADMIN.username, ADMIN.password); // sesión propia: cambia de empresa
  const created = await admin.post('/api/admin/companies', { data: { code: 'e2e-usd-22', name: 'Dólares E2E 022', baseCurrency: 'USD' } });
  expect(created.status(), await created.text()).toBe(200);
  const company = await created.json();
  const gerente = ((await (await admin.get('/api/admin/users')).json()) as { id: number; username: string; displayName: string; roleIds: number[]; unit?: string; companyIds: number[] }[])
    .find((u) => u.username === users.gerente)!;
  const setCompanies = (companyIds: number[]) => admin.put(`/api/admin/users/${gerente.id}`, { data: {
    displayName: gerente.displayName, isActive: true, roleIds: gerente.roleIds, unit: gerente.unit, companyIds } });
  try {
    expect((await setCompanies([...gerente.companyIds, company.id])).status()).toBe(200);
    expect((await admin.post('/api/auth/company', { data: { companyId: company.id } })).status()).toBe(200);
    const g = await apiAs(users.gerente, PWD);
    expect((await g.post('/api/auth/company', { data: { companyId: company.id } })).status()).toBe(200);

    const alm = await (await admin.post('/api/data/Almacen', { data: { Clave: 'ALM-USD', Nombre: 'Almacén en dólares' } })).json();
    const eur = ((await (await admin.get('/api/data/Moneda', { params: { pageSize: 50 } })).json()).items as Row[]).find((m) => m.Clave === 'EUR')!;
    const oc = await (await admin.post('/api/data/OrdenCompra', { data: { Proveedor: ids.proveedor, Almacen: alm.id, Moneda: eur.id, TipoCambio: 20,
      Partidas: [{ Producto: ids.b, Cantidad: 2, PrecioUnitario: 5, DescuentoPct: 0 }] } })).json();
    expect(oc.Folio, JSON.stringify(oc)).toBeTruthy();
    const step = async (who: APIRequestContext, row: Row, name: string) => {
      const r = await who.post(`/api/data/OrdenCompra/${row.id}/transition`, { data: { transition: name, _rowVersion: row._rowVersion } });
      expect(r.status(), await r.text()).toBe(200);
      return (await r.json()) as Row;
    };
    const sent = await step(admin, await step(g, await step(admin, oc, 'enviar'), 'autorizar'), 'enviarProveedor');
    const rec = await (await admin.post('/api/data/Recepcion', { data: { OrdenCompra: sent.id, Factura: 'F-USD',
      Partidas: [{ PartidaOrden: (sent.Partidas as Row[])[0].id }] } })).json();
    expect((rec.Partidas as Row[])[0].CostoUnitario).toBeNull(); // no hay EUR→USD ni respaldo en pesos
    const confirm = async () => {
      const fresh = await (await admin.get(`/api/data/Recepcion/${rec.id}`)).json();
      const r = await admin.post(`/api/data/Recepcion/${rec.id}/transition`, { data: { transition: 'confirmar', _rowVersion: fresh._rowVersion } });
      return { status: r.status(), body: (await r.json()) as Body };
    };
    await withoutRule('Recepcion', 'TipoCambioDeLaRecepcion', async () => {
      const blocked = await confirm();
      expect(blocked.status).toBe(400);
      expect(blocked.body.errors!['Partidas[0].CostoUnitario'][0]).toMatch(/no tiene costo en la moneda base/);
    });
    expect(((await (await admin.get(`/api/data/Recepcion/${rec.id}`)).json()) as Row).Estado).toBe('Borrador');
    await g.dispose();
  } finally {
    await setCompanies(gerente.companyIds);
    await api.gerente?.dispose(); // cambiar sus empresas revoca sus sesiones: las pruebas siguientes inician otra
    delete api.gerente;
    await admin.put(`/api/admin/companies/${company.id}`, { data: { name: company.name, baseCurrency: 'USD', isActive: false } });
    await admin.dispose();
  }
});

test('H4 CA1: la entrada al almacén lleva la fecha de la recepción, igual que su póliza', async () => {
  const oc = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen1, Partidas: [{ Producto: ids.b, Cantidad: 1, PrecioUnitario: 20, DescuentoPct: 0 }] });
  const rec = await recepcion(oc, { Factura: 'F-FECHA', Fecha: OTHER_DAY });
  const ok = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(ok.status, JSON.stringify(ok.body)).toBe(200);
  expect(await movimientos(rec.Folio as string)).toEqual([expect.objectContaining({ Fecha: OTHER_DAY, CostoTotal: 20 })]); // antes: hoy
  const pol = ((await get('contador', '/api/data/Poliza', { search: rec.Folio as string })).items as Row[])[0];
  expect(pol.Fecha).toBe(OTHER_DAY);
});

test('H5 CA2: un producto FIFO que pasa a Promedio sale al promedio de todas sus capas', async () => {
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen2, Producto: ids.c, Cantidad: 10, CostoUnitario: 10 });
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen2, Producto: ids.c, Cantidad: 10, CostoUnitario: 20 });
  const producto = (await get('admin', `/api/data/Producto/${ids.c}`)) as Row;
  const changed = await put('admin', `/api/data/Producto/${ids.c}`, { _rowVersion: producto._rowVersion, MetodoCosteo: 'Promedio' });
  expect(changed.status, JSON.stringify(changed.body)).toBe(200);
  const s = await movimiento('almacen', { Tipo: 'Salida', Almacen: ids.almacen2, Producto: ids.c, Cantidad: 5 });
  expect(s).toMatchObject({ CostoTotal: -75, CostoUnitario: 15 }); // antes: -50 (seguía consumiendo FIFO)
  expect(await existencia(ids.almacen2, ids.c)).toMatchObject({ Cantidad: 15, Valor: 225, CostoPromedio: 15 });
});

test('H7 CA1 · CA3: sin cuentas en el almacén la recepción con costo no se confirma; la de costo cero sí, sin póliza', async ({ browser }) => {
  const oc = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen2, Partidas: [{ Producto: ids.b, Cantidad: 2, PrecioUnitario: 8, DescuentoPct: 0 }] });
  const rec = await recepcion(oc, { Factura: 'F-SIN-CUENTAS' });
  const blocked = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(blocked.status).toBe(400);
  expect(blocked.body.errors!.Almacen[0]).toMatch(/cuenta de inventario y la cuenta de entradas por facturar/);

  const page = await pageAs(browser, 'almacen');
  await page.goto(`/c/Recepcion/${rec.id}`);
  await page.getByRole('button', { name: 'Confirmar recepción' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirmar recepción' }).click();
  // Regresión (revisión exploratoria 022, F-03): el motivo se anuncia y el foco vuelve al botón, dentro del diálogo.
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('no tiene la cuenta de inventario y la cuenta de entradas por facturar');
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Confirmar recepción' })).toBeFocused();
  await page.context().close();

  // CA3: costo cero no necesita póliza (no cuadraría), así que no se bloquea ni se marca.
  const gratis = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen2, Partidas: [{ Producto: ids.b, Cantidad: 1, PrecioUnitario: 0, DescuentoPct: 0 }] });
  const r0 = await recepcion(gratis, { Factura: 'F-MUESTRA' });
  const ok = await transition('almacen', 'Recepcion', r0, 'confirmar');
  expect(ok.status, JSON.stringify(ok.body)).toBe(200);
  expect(ok.body.PolizaPendiente).toBeFalsy();
  expect((await get('contador', '/api/data/Poliza', { search: r0.Folio as string })).items).toHaveLength(0);
});

test('H7 CA2: sin la regla, la recepción se confirma y queda como póliza pendiente', async ({ page }) => {
  const recs = (await get('almacen', '/api/data/Recepcion', { search: 'F-SIN-CUENTAS' })).items as Row[];
  const rec = recs.find((r) => r.Estado === 'Borrador')!;
  await withoutRule('Recepcion', 'PolizaConCuentas', async () => {
    const ok = await transition('almacen', 'Recepcion', await fresh('Recepcion', rec.id), 'confirmar');
    expect(ok.status, JSON.stringify(ok.body)).toBe(200);
    expect(ok.body).toMatchObject({ Estado: 'Confirmada', PolizaPendiente: true });
    expect(await get('admin', `/api/data/Recepcion/${rec.id}`)).toMatchObject({ CostoTotal: 16 }); // el almacenista no lo ve (029, D-05)
    expect(await movimientos(rec.Folio as string)).toEqual([expect.objectContaining({ CostoTotal: 16 })]);
    expect((await get('contador', '/api/data/Poliza', { search: rec.Folio as string })).items).toHaveLength(0);
  });
  // Queda a la vista en «Pólizas pendientes» (quien lee recepciones; aquí, el administrador).
  await page.goto('/c/Recepcion?view=PolizasPendientes');
  await expect(page.getByRole('cell', { name: rec.Folio as string })).toBeVisible();
  await expectAccessible(page);
});
