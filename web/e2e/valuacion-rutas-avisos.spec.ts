// Spec 023 · Capacidades del motor: listas que se fusionan (merge), reglas de aviso y rutas de tres niveles. Corre después de
// valuacion-politicas.spec.ts (Inventario, Contabilidad y Costeo instalados; almacén ALM-01 con cuentas) y antes de
// visual.spec.ts. Las capacidades genéricas se ejercen con un módulo de prueba propio (Ruta*: país → cliente → pedido →
// partida); el caso 7 de la 022 («avisar») con la regla real de Costeo, que se restaura al terminar.
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, openJson, test } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de compras 2026';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };
type Body = Row & { title?: string; errors?: Record<string, string[]>; items: Row[]; _warnings?: Record<string, string[]> };

let adminApi: APIRequestContext | undefined;
const admin = async () => (adminApi ??= await apiAs(ADMIN.username, ADMIN.password));
async function call(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Body };
}
const post = async (url: string, data: object, who?: APIRequestContext) => call(await (who ?? await admin()).post(url, { data }));
const put = async (url: string, data: object) => call(await (await admin()).put(url, { data }));
const get = async (url: string, params?: Record<string, string | number>) => (await (await admin()).get(url, { params })).json();
const transition = async (entity: string, row: Row, name: string, extra: object = {}, who?: APIRequestContext) =>
  post(`/api/data/${entity}/${row.id}/transition`, { transition: name, _rowVersion: row._rowVersion, ...extra }, who);
type Entity = { rules?: { name: string; severity?: string }[]; views?: { name: string }[] };
const model = async (entity: string) => (await get(`/api/model/entities/${entity}`)) as Entity;

const LIMITE = 'El pedido excede el límite de crédito del cliente.';
const PIEZAS = 'Para clientes de Estados Unidos, más de 10 piezas por partida requiere revisión.';

/** Módulo de prueba: rutas de 1 a 3 saltos, reglas de aviso y transiciones con las tres políticas. */
const RUTAS = {
  description: 'E2E 023: rutas y avisos',
  operations: [
    { op: 'createEntity', name: 'RutaPais', label: 'País', labelPlural: 'Países', displayField: 'Clave', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', maxLength: 3, required: true, unique: true },
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 60 } ] },
    { op: 'createEntity', name: 'RutaCliente', label: 'Cliente de ruta', labelPlural: 'Clientes de ruta', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'Pais', label: 'País', type: 'reference', referenceEntity: 'RutaPais', required: true },
      { name: 'LimiteCredito', label: 'Límite de crédito', type: 'decimal', precision: 18, scale: 2 } ] },
    { op: 'createEntity', name: 'RutaArticulo', label: 'Artículo de ruta', labelPlural: 'Artículos de ruta', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'PiezasUS', label: 'Piezas a Estados Unidos', type: 'decimal', precision: 18, scale: 2, editable: false, defaultValue: '0' } ] },
    { op: 'createEntity', name: 'RutaPedido', label: 'Pedido de ruta', labelPlural: 'Pedidos de ruta', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'RP-', padding: 4 },
      { name: 'Cliente', label: 'Cliente', type: 'reference', referenceEntity: 'RutaCliente', required: true },
      { name: 'PaisCliente', label: 'País del cliente', type: 'formula', resultType: 'text', formula: 'Cliente.Pais.Clave' } ] },
    { op: 'createEntity', name: 'RutaPedidoPartida', label: 'Partida', labelPlural: 'Partidas', fields: [
      { name: 'Pedido', label: 'Pedido', type: 'parent', referenceEntity: 'RutaPedido' },
      { name: 'Articulo', label: 'Artículo', type: 'reference', referenceEntity: 'RutaArticulo', required: true },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal', precision: 18, scale: 2, required: true },
      { name: 'Precio', label: 'Precio', type: 'decimal', precision: 18, scale: 2, required: true },
      { name: 'Importe', label: 'Importe', type: 'formula', resultType: 'decimal', precision: 18, scale: 2, formula: 'Cantidad * Precio' },
      // Tres saltos desde la partida: el pedido (en memoria) → su cliente → su país → la clave.
      { name: 'PaisDestino', label: 'País de destino', type: 'formula', resultType: 'text', formula: 'Pedido.Cliente.Pais.Clave' } ] },
    { op: 'addField', entity: 'RutaPedido', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'RutaPedidoPartida' } },
    { op: 'addField', entity: 'RutaPedido', field: { name: 'Total', label: 'Total', type: 'formula', resultType: 'decimal', precision: 18, scale: 2, formula: 'SUM(Partidas.Importe)' } },
    { op: 'setRules', entity: 'RutaPedido', rules: [
      { name: 'LimiteExcedido', field: 'Total', severity: 'warning', assert: 'ISBLANK(Cliente.LimiteCredito) OR Total <= Cliente.LimiteCredito', message: LIMITE } ] },
    { op: 'setRules', entity: 'RutaPedidoPartida', rules: [
      { name: 'CantidadPositiva', field: 'Cantidad', assert: 'Cantidad > 0', message: 'La cantidad debe ser mayor a cero.' },
      { name: 'MuchasPiezasUS', field: 'Cantidad', severity: 'warning', when: "Pedido.Cliente.Pais.Clave = 'US'", assert: 'Cantidad <= 10', message: PIEZAS } ] },
    { op: 'setWorkflow', entity: 'RutaPedido', workflow: { initial: 'Borrador', states: [
      { name: 'Borrador', label: 'Borrador' }, { name: 'Confirmado', label: 'Confirmado', color: 'success' }, { name: 'Cerrado', label: 'Cerrado', editable: false } ],
      transitions: [
        { name: 'confirmar', label: 'Confirmar', from: ['Borrador'], to: 'Confirmado', variant: 'primary' },
        { name: 'confirmarDirecto', label: 'Confirmar sin preguntar', from: ['Borrador'], to: 'Confirmado', onWarning: 'allow' },
        { name: 'cerrar', label: 'Cerrar', from: ['Confirmado'], to: 'Cerrado', onWarning: 'block' },
        { name: 'reabrir', label: 'Reabrir', from: ['Confirmado'], to: 'Borrador' } ] } },
    // Acumulado con filtro de tres saltos (se traduce a SQL): piezas confirmadas a Estados Unidos por artículo.
    { op: 'setAutomations', entity: 'RutaPedido', automations: [
      { name: 'PiezasUS', label: 'Acumular piezas a Estados Unidos', on: 'enterState', state: 'Confirmado', actions: [
        { type: 'rollup', lines: 'Partidas', target: 'Articulo', field: 'PiezasUS', sum: 'Cantidad',
          filter: "Pedido.Cliente.Pais.Clave = 'US' AND Pedido.Estado = 'Confirmado'" } ] } ] },
  ],
};

const ids = { us: 0, mx: 0, acme: 0, norte: 0, tornillo: 0 };
async function ensureModule() {
  if (ids.acme) return;
  if (!(await (await admin()).get('/api/model/entities/RutaPedido')).ok()) await applyPlan(await admin(), RUTAS);
  const find = async (entity: string, search: string) => ((await get(`/api/data/${entity}`, { search })).items as Row[])[0]?.id;
  const ensure = async (entity: string, search: string, data: object) => {
    const found = await find(entity, search);
    if (found) return found;
    const r = await post(`/api/data/${entity}`, data);
    expect(r.status, JSON.stringify(r.body)).toBe(201);
    return r.body.id;
  };
  ids.us = await ensure('RutaPais', 'US', { Clave: 'US', Nombre: 'Estados Unidos' });
  ids.mx = await ensure('RutaPais', 'MX', { Clave: 'MX', Nombre: 'México' });
  ids.acme = await ensure('RutaCliente', 'Acme', { Nombre: 'Acme Corp', Pais: ids.us, LimiteCredito: 1000 });
  ids.norte = await ensure('RutaCliente', 'Norteña', { Nombre: 'Distribuidora Norteña', Pais: ids.mx, LimiteCredito: 1000 });
  ids.tornillo = await ensure('RutaArticulo', 'Tornillo', { Nombre: 'Tornillo 1/4' });
}
test.beforeEach(ensureModule);

async function pedido(cliente: number, lines: { Cantidad: number; Precio: number }[]) {
  const r = await post('/api/data/RutaPedido', { Cliente: cliente, Partidas: lines.map((l) => ({ Articulo: ids.tornillo, ...l })) });
  expect(r.status, JSON.stringify(r.body)).toBe(201);
  return r.body;
}
const fresh = async (id: number) => (await get(`/api/data/RutaPedido/${id}`)) as Body;
const audit = async (entity: string, action: string) =>
  ((await get('/api/audit', { entity, action, pageSize: 20 })) as { items: { recordId: string; after?: Record<string, unknown> }[] }).items;

// ---------------- H3: rutas de tres niveles ----------------

test('H3 CA1: una fórmula de la partida lee Pedido.Cliente.Pais.Clave (tres saltos) y una del documento Cliente.Pais.Clave', async () => {
  const p = await pedido(ids.acme, [{ Cantidad: 2, Precio: 10 }]);
  expect(p).toMatchObject({ PaisCliente: 'US', Total: 20 });
  expect((p.Partidas as Row[])[0]).toMatchObject({ PaisDestino: 'US', Importe: 20 });
  // En vivo (/compute): cambiar de cliente cambia la ruta en la partida aún sin guardar.
  const c = await post('/api/data/RutaPedido/compute', { id: p.id, values: { Cliente: ids.norte } });
  expect(c.status).toBe(200);
  expect((c.body as unknown as { values: { Partidas: Row[] } }).values.Partidas[0].PaisDestino).toBe('MX');
});

test('H3 CA1: el modelador rechaza una ruta que no existe o pasa el tope, con el salto que falla', async () => {
  const preview = async (formula: string) => call(await (await admin()).post('/api/model/plan/preview', { data: { description: 'ruta', operations: [
    { op: 'updateField', entity: 'RutaPedidoPartida', field: 'PaisDestino', formula }] } }));
  const bad = await preview('Pedido.Cliente.Region.Clave');
  expect(JSON.stringify(bad.body)).toContain("'Region' no existe en RutaCliente ('Pedido.Cliente.Region.Clave')");
  const deep = await preview('Pedido.Cliente.Pais.Clave.Otro');
  expect(JSON.stringify(deep.body)).toContain('máximo 3 niveles');
});

test('H3 CA2: un acumulado con filtro de tres saltos (traducido a SQL) suma solo lo confirmado a Estados Unidos', async () => {
  const antes = Number(((await get(`/api/data/RutaArticulo/${ids.tornillo}`)) as Row).PiezasUS);
  const us = await pedido(ids.acme, [{ Cantidad: 3, Precio: 1 }]);
  const mx = await pedido(ids.norte, [{ Cantidad: 4, Precio: 1 }]);
  expect((await transition('RutaPedido', us, 'confirmar')).status).toBe(200);
  expect((await transition('RutaPedido', await fresh(mx.id), 'confirmar')).status).toBe(200);
  expect(Number(((await get(`/api/data/RutaArticulo/${ids.tornillo}`)) as Row).PiezasUS)).toBe(antes + 3);
});

// ---------------- H2: reglas de aviso ----------------

test('H2 CA1 · CA2 · CA4: un aviso no impide guardar; la respuesta lo trae en _warnings y queda en la auditoría', async () => {
  const p = await pedido(ids.acme, [{ Cantidad: 12, Precio: 100 }]); // 1200 > 1000 y 12 piezas a US
  expect(p._warnings).toEqual({ Total: [LIMITE], 'Partidas[0].Cantidad': [PIEZAS] });
  // Un error sigue bloqueando.
  const bad = await post('/api/data/RutaPedido', { Cliente: ids.acme, Partidas: [{ Articulo: ids.tornillo, Cantidad: -1, Precio: 1 }] });
  expect(bad.status).toBe(400);
  // Al abrirlo y en /compute: los avisos vigentes.
  expect((await fresh(p.id))._warnings).toEqual(p._warnings);
  const c = await post('/api/data/RutaPedido/compute', { id: p.id, values: { Partidas: [{ id: (p.Partidas as Row[])[0].id, Cantidad: 5, Precio: 300 }] } });
  expect((c.body as unknown as { warnings: Record<string, string[]> }).warnings).toEqual({ Total: [LIMITE] });
  // Sin avisos no hay _warnings.
  const ok = await pedido(ids.norte, [{ Cantidad: 1, Precio: 1 }]);
  expect(ok._warnings).toBeUndefined();
  const created = (await audit('RutaPedido', 'create')).find((a) => a.recordId === String(p.id))!;
  expect(created.after?._warnings).toEqual(p._warnings);
});

test('H2 CA3: confirm pide aceptar los avisos concretos (428); block no ejecuta; allow sigue sin preguntar', async () => {
  const p = await pedido(ids.acme, [{ Cantidad: 12, Precio: 1 }]);
  const asked = await transition('RutaPedido', p, 'confirmar');
  expect(asked.status).toBe(428);
  expect(asked.body.errors).toEqual({ 'Partidas[0].Cantidad': [PIEZAS] });
  // Aceptar otro mensaje no basta: hay que aceptar los que el servidor calculó.
  expect((await transition('RutaPedido', p, 'confirmar', { acceptWarnings: ['otro'] })).status).toBe(428);
  const done = await transition('RutaPedido', p, 'confirmar', { acceptWarnings: [PIEZAS] });
  expect(done.status, JSON.stringify(done.body)).toBe(200);
  expect(done.body.Estado).toBe('Confirmado');
  const wf = (await audit('RutaPedido', 'workflow.confirmar')).find((a) => a.recordId === String(p.id))!;
  expect(wf.after?._warnings).toEqual({ 'Partidas[0].Cantidad': [PIEZAS] });

  const blocked = await transition('RutaPedido', done.body, 'cerrar', { acceptWarnings: [PIEZAS] });
  expect(blocked.status).toBe(400);
  expect(blocked.body.errors).toEqual({ 'Partidas[0].Cantidad': [PIEZAS] });

  const q = await pedido(ids.acme, [{ Cantidad: 11, Precio: 1 }]);
  const direct = await transition('RutaPedido', q, 'confirmarDirecto');
  expect(direct.status, JSON.stringify(direct.body)).toBe(200);
  expect((await audit('RutaPedido', 'workflow.confirmarDirecto')).find((a) => a.recordId === String(q.id))!.after?._warnings)
    .toEqual({ 'Partidas[0].Cantidad': [PIEZAS] });
  // Sin avisos, confirm no pregunta.
  const r = await pedido(ids.norte, [{ Cantidad: 11, Precio: 1 }]);
  expect((await transition('RutaPedido', r, 'confirmar')).status).toBe(200);
});

test('H2 CA2: la importación lista los avisos por fila y la importa igual', async () => {
  const csv = 'Nombre,País,Límite de crédito\nCliente con aviso,US,10\n';
  // Una regla de aviso temporal en el catálogo de clientes (se quita al terminar).
  const api = await admin();
  await applyPlan(api, { description: 'aviso temporal', operations: [{ op: 'setRules', entity: 'RutaCliente', merge: true, rules: [
    { name: 'LimiteBajo', field: 'LimiteCredito', severity: 'warning', assert: 'LimiteCredito >= 100', message: 'El límite de crédito es muy bajo.' }] }] });
  try {
    const res = await api.post('/api/data/RutaCliente/import', { params: { dryRun: 'false' }, headers: { 'Content-Type': 'application/octet-stream' }, data: Buffer.from(csv, 'utf8') });
    const body = await res.json();
    expect(res.status(), JSON.stringify(body)).toBe(200);
    expect(body).toMatchObject({ imported: 1, valid: true, warnings: [{ row: 2, column: 'Límite de crédito', field: 'LimiteCredito', message: 'El límite de crédito es muy bajo.' }] });
  } finally {
    await applyPlan(api, { description: 'quitar aviso temporal', operations: [{ op: 'setRules', entity: 'RutaCliente', rules: [] }] });
  }
});

test('H2 CA2 · CA3 (UI): el formulario muestra los avisos en vivo y el diálogo pide confirmarlos, solo con teclado', async ({ page }) => {
  const p = await pedido(ids.acme, [{ Cantidad: 2, Precio: 1 }]);
  await page.goto(`/c/RutaPedido/${p.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'Avisos' })).toHaveCount(0);
  const cantidad = page.locator('#f-Partidas-0-Cantidad');
  await cantidad.fill('12');
  const avisos = page.getByRole('status').filter({ hasText: 'Avisos' });
  await expect(avisos).toContainText(`Partida 1 · Cantidad: ${PIEZAS}`);
  await page.getByRole('button', { name: 'Guardar cambios' }).click();
  await expect(page.getByRole('button', { name: 'Guardar cambios' })).toBeEnabled();
  await expect(avisos).toContainText(PIEZAS); // guardado con su aviso (no se bloquea)
  expect((await fresh(p.id)).Partidas).toEqual([expect.objectContaining({ Cantidad: 12 })]);
  await expectAccessible(page);

  await page.getByRole('button', { name: 'Confirmar', exact: true }).focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Confirmar' });
  await expect(dialog).toBeVisible();
  // El botón de confirmar no es el que recibe el foco (Cancelar sí): Tab lo alcanza.
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Confirmar' })).toBeFocused();
  await page.keyboard.press('Enter');
  const alert = dialog.getByRole('alert');
  await expect(alert).toContainText('Confirma los avisos para continuar');
  await expect(alert).toContainText(PIEZAS);
  await expect(dialog.getByRole('button', { name: 'Continuar con avisos' })).toBeVisible();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Continuar con avisos' }).focus();
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('group', { name: 'Proceso del documento' })).toContainText('Confirmado');
  expect((await fresh(p.id)).Estado).toBe('Confirmado');
});

test('H2 (modelador): el panel de la transición ofrece «Si hay avisos» y el JSON de reglas explica severity', async ({ page }) => {
  await page.goto('/admin/model?edit=RutaPedido&tab=proceso');
  await page.getByRole('button', { name: /^Transición Cerrar: de Confirmado a Cerrado$/ }).click();
  const policy = page.getByLabel('Si hay avisos');
  await expect(policy).toHaveValue('block');
  await expect(policy.locator('option')).toHaveText(['Pedir confirmación', 'No ejecutarla', 'Continuar sin preguntar']);
  await page.getByRole('button', { name: /^Transición Confirmar: de Borrador a Confirmado$/ }).click();
  await expect(page.getByLabel('Si hay avisos')).toHaveValue('confirm');
  await expectAccessible(page);
  // Spec 026: las reglas tienen su pestaña; la severidad es un control y el JSON avanzado la sigue explicando.
  await page.getByRole('tab', { name: 'Reglas' }).click();
  await openJson(page, 'Reglas (JSON)');
  await expect(page.getByText(/severity \(«warning»\)/)).toBeVisible();
});

test('H2 CA6: caso 7 de la 022 — con PolizaConCuentas en aviso, confirmar pide confirmación y queda Póliza pendiente', async () => {
  const api = await admin();
  const almacen = await apiAs('e2e-almacen', PWD);
  const rules = ((await model('Recepcion')).rules ?? []) as { name: string; severity?: string }[];
  const rule = rules.find((r) => r.name === 'PolizaConCuentas')!;
  expect(rule.severity).toBeUndefined(); // por defecto, bloquear (022)
  const alm2 = ((await get('/api/data/Almacen', { pageSize: 50 })).items as Row[]).find((a) => a.Clave === 'ALM-02')!; // sin cuentas
  const proveedor = ((await get('/api/data/Proveedor', { search: 'Cartones' })).items as Row[])[0];
  const producto = ((await get('/api/data/Producto', { search: 'POL-B' })).items as Row[])[0];
  await applyPlan(api, { description: 'Política 7: avisar', operations: [{ op: 'setRules', entity: 'Recepcion', merge: true, rules: [{ ...rule, severity: 'warning' }] }] });
  try {
    const comprador = await apiAs('e2e-comprador', PWD);
    const gerente = await apiAs('e2e-gerente', PWD);
    let oc = (await post('/api/data/OrdenCompra', { Proveedor: proveedor.id, Almacen: alm2.id, Partidas: [{ Producto: producto.id, Cantidad: 1, PrecioUnitario: 30, DescuentoPct: 0 }] }, comprador)).body;
    for (const [who, name] of [[comprador, 'enviar'], [gerente, 'autorizar'], [comprador, 'enviarProveedor']] as const) {
      const r = await transition('OrdenCompra', oc, name, {}, who);
      expect(r.status, JSON.stringify(r.body)).toBe(200);
      oc = r.body;
    }
    const rec = (await post('/api/data/Recepcion', { OrdenCompra: oc.id, Factura: 'F-AVISO', Partidas: [{ PartidaOrden: (oc.Partidas as Row[])[0].id }] }, almacen)).body;
    expect(rec.id, JSON.stringify(rec)).toBeTruthy();
    // Spec 029 (D-05): la regla lee el costo total de la recepción, que sale del precio de la orden y el almacenista no ve;
    // a él no se le muestra el aviso (R-023-3, R-029-11). Confirma el gerente de compras, que sí ve el costo.
    expect((await (await almacen.get(`/api/data/Recepcion/${rec.id}`)).json())).not.toHaveProperty('CostoTotal');
    const asked = await transition('Recepcion', rec, 'confirmar', {}, gerente);
    expect(asked.status, JSON.stringify(asked.body)).toBe(428);
    const message = asked.body.errors!.Almacen[0];
    expect(message).toMatch(/cuenta de inventario/);
    const ok = await transition('Recepcion', rec, 'confirmar', { acceptWarnings: [message] }, gerente);
    expect(ok.status, JSON.stringify(ok.body)).toBe(200);
    expect(ok.body).toMatchObject({ Estado: 'Confirmada', PolizaPendiente: true });
    expect(ok.body._warnings).toEqual({ Almacen: [message] }); // el aviso sigue a la vista en el registro
    const wf = (await audit('Recepcion', 'workflow.confirmar')).find((a) => a.recordId === String(rec.id))!;
    expect(wf.after?._warnings).toEqual({ Almacen: [message] });
    await comprador.dispose();
    await gerente.dispose();
  } finally {
    await applyPlan(api, { description: 'Política 7: bloquear', operations: [{ op: 'setRules', entity: 'Recepcion', merge: true, rules: [rule] }] });
    await almacen.dispose();
  }
  expect(((await model('Recepcion')).rules ?? []).find((r) => r.name === 'PolizaConCuentas')!.severity).toBeUndefined();
});

// ---------------- H1: listas que se fusionan ----------------

test('H1 CA1 · CA4: setViews con merge agrega una vista a Recepción sin quitar «Pólizas pendientes» de Costeo', async () => {
  const api = await admin();
  const before = (await model('Recepcion')).views ?? [];
  expect(before.map((v) => v.name)).toContain('PolizasPendientes');
  const plan = { description: 'vista extra', operations: [{ op: 'setViews', entity: 'Recepcion', merge: true, views: [
    { name: 'E2eConfirmadas', label: 'Confirmadas (E2E)', state: 'Confirmada' }] }] };
  const preview = await call(await api.post('/api/model/plan/preview', { data: plan }));
  expect(JSON.stringify(preview.body)).toContain('Agregar o actualizar 1 vista(s) compartida(s)');
  await applyPlan(api, plan);
  try {
    expect(((await model('Recepcion')).views ?? []).map((v) => v.name)).toEqual([...before.map((v) => v.name), 'E2eConfirmadas']);
  } finally {
    await applyPlan(api, { description: 'restaurar vistas', operations: [{ op: 'setViews', entity: 'Recepcion', views: before }] });
  }
});

test('H4: Inventario 1.7.0 está instalado y la partida de recepción usa la moneda de la orden por la ruta de tres saltos', async () => {
  const mods = (await get('/api/admin/modules')) as { id: string; installedVersion?: string }[];
  expect(mods.find((m) => m.id === 'inventario')).toMatchObject({ installedVersion: '1.8.0' });
  const e = (await get('/api/model/entities/RecepcionPartida')) as { fields: { name: string; formula?: string }[] };
  expect(e.fields.find((f) => f.name === 'CostoUnitario')!.formula).toContain('COALESCE(Recepcion.Moneda, Recepcion.OrdenCompra.Moneda.Clave)');
});

test('visual: el aviso del formulario y el diálogo que pide confirmarlo, en claro y oscuro', async ({ page }) => {
  const p = await pedido(ids.acme, [{ Cantidad: 12, Precio: 1 }]);
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto(`/c/RutaPedido/${p.id}`);
    const avisos = page.getByRole('status').filter({ hasText: 'Avisos' });
    await expect(avisos).toBeVisible();
    await expect(avisos).toHaveScreenshot(`aviso-formulario-${scheme}.png`);
    await page.getByRole('button', { name: 'Confirmar', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Confirmar' });
    await dialog.getByRole('button', { name: 'Confirmar' }).click();
    await expect(dialog.getByRole('button', { name: 'Continuar con avisos' })).toBeVisible();
    await page.evaluate(() => Promise.all(document.getAnimations().map((a) => a.finished.catch(() => undefined))));
    await expect(dialog).toHaveScreenshot(`dialogo-avisos-${scheme}.png`);
    await dialog.getByRole('button', { name: 'Cancelar' }).click();
  }
});

test('móvil: el aviso y el diálogo caben sin scroll horizontal', async ({ page }) => {
  const p = await pedido(ids.acme, [{ Cantidad: 12, Precio: 1 }]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/c/RutaPedido/${p.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'Avisos' })).toBeVisible();
  const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(await overflow()).toBeLessThanOrEqual(0);
  await page.getByRole('button', { name: 'Confirmar', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Confirmar' });
  await dialog.getByRole('button', { name: 'Confirmar' }).click();
  await expect(dialog.getByRole('alert')).toBeVisible();
  expect(await overflow()).toBeLessThanOrEqual(0);
  const box = await dialog.boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(390);
});

