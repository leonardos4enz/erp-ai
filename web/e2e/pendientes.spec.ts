// Spec 014 · Pendientes menores y riesgos residuales. Cada prueba de un defecto se verificó fallando con el código
// anterior. Corre después de compras, inventario y multiempresa (Compras 1.8.0 e Inventario instalados). El catálogo de
// importación es del grupo «Pruebas 014» y se archiva al terminar (no altera el menú ni las capturas de referencia).
import { execFileSync, spawn } from 'node:child_process';
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, DB, apiAs, applyPlan, expect, test } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 014';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };
type Company = { id: number; code: string; isDefault: boolean };

let admin: APIRequestContext;
const ids: Record<string, number> = {};
const today = new Date().toLocaleDateString('en-CA'); // aaaa-mm-dd, hora local como el servidor

/** SQL directo sobre la base desechable de la suite (nunca la de desarrollo). */
function sql(query: string) {
  const db = DB;
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  return execFileSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-h', '-1', '-W', '-Q',
    `SET NOCOUNT ON; ${query}`]).toString().trim();
}

async function create(entity: string, data: object) {
  const res = await admin.post(`/api/data/${entity}`, { data });
  expect(res.status(), `${entity} ${JSON.stringify(data)} → ${await res.text()}`).toBe(201);
  return (await res.json()) as Row;
}
const first = async (entity: string, search: string) =>
  ((await (await admin.get(`/api/data/${entity}`, { params: { search } })).json()).items as Row[])[0];
/** Id por el buscador (lo que ve la persona en el campo de referencia), con la empresa actual. */
async function lookup(entity: string, search: string) {
  const items = (await (await admin.get(`/api/data/${entity}/lookup`, { params: { search } })).json()) as { id: number }[];
  expect(items.length, `${entity} «${search}»`).toBeGreaterThan(0);
  return items[0].id;
}
const existencia = async (producto: number) =>
  ((await (await admin.get('/api/data/Existencia', { params: { filters: JSON.stringify([{ field: 'Producto', op: 'eq', value: producto }]) } })).json())
    .items as Row[])[0];

test.beforeAll(async () => {
  admin = await apiAs(ADMIN.username, ADMIN.password);
  const main = ((await (await admin.get('/api/admin/companies')).json()) as Company[]).find((c) => c.isDefault)!;
  expect((await admin.post('/api/auth/company', { data: { companyId: main.id } })).status()).toBe(200);
  ids.company = main.id;
});

test.afterAll(async () => {
  const model = (await (await admin.get('/api/model/entities')).json()) as { name: string }[];
  const operations = ['P14Mov', 'P14Saldo', 'P14Cuenta', 'P14Ticket'].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(admin, { description: 'E2E 014: limpieza', operations });
  await admin.dispose();
});

test('preparar: productos de prueba, tipo de cambio y catálogo de importación', async () => {
  const pza = await first('UnidadMedida', 'PZA');
  ids.unidad = pza.id;
  ids.almacen = await lookup('Almacen', 'Almacén general'); // ALM-01 de la empresa principal
  ids.proveedorUsd = await lookup('Proveedor', 'Industrial Supply');
  ids.departamento = await lookup('Departamento', 'Almacén');
  const producto = async (clave: string, data: object) =>
    (await first('Producto', clave))?.id ?? (await create('Producto', { Clave: clave, Descripcion: `Producto ${clave}`, Unidad: pza.id, ...data })).id;
  ids.costoMxn = await producto('P14-MXN', { CostoUltimo: 170, MonedaCosto: 'MXN' });
  ids.costoUsd = await producto('P14-USD', { CostoUltimo: 10, MonedaCosto: 'USD' });
  ids.muchos = await producto('P14-MUCHOS', {});
  ids.stock = await producto('P14-STOCK', {});
  expect((await admin.put('/api/admin/exchange-rates', { data: { from: 'USD', to: 'MXN', date: today, rate: 17 } })).status()).toBe(200);

  // Sin automatizaciones: lo que se mide es solo el folio (un acumulado sobre un texto sin índice haría que la
  // importación y el alta se esperaran por escanear filas sin confirmar, que no es lo que se prueba).
  await applyPlan(admin, { description: 'Spec 014 · importación', operations: [
    { op: 'createEntity', group: GROUP, name: 'P14Ticket', label: 'Ticket 014', labelPlural: 'Tickets 014', displayField: 'Folio', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'T14-', padding: 6 },
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true, unique: true },
      { name: 'Grupo', label: 'Grupo', type: 'text', required: true },
    ] },
    // Acumulado por «match» en un catálogo sin folio: nada serializa las altas antes de acumular.
    { op: 'createEntity', group: GROUP, name: 'P14Cuenta', label: 'Cuenta 014', labelPlural: 'Cuentas 014', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
    ] },
    { op: 'createEntity', group: GROUP, name: 'P14Saldo', label: 'Saldo 014', labelPlural: 'Saldos 014', fields: [
      { name: 'Cuenta', label: 'Cuenta', type: 'reference', referenceEntity: 'P14Cuenta', editable: false },
      { name: 'Saldo', label: 'Saldo', type: 'decimal', editable: false, defaultValue: '0' },
    ] },
    { op: 'setRules', entity: 'P14Saldo', rules: [{ name: 'SinNegativos', field: 'Saldo', assert: 'Saldo >= 0', message: 'Saldo insuficiente.' }] },
    { op: 'createEntity', group: GROUP, name: 'P14Mov', label: 'Movimiento 014', labelPlural: 'Movimientos 014', fields: [
      { name: 'Cuenta', label: 'Cuenta', type: 'reference', referenceEntity: 'P14Cuenta', required: true },
      { name: 'Importe', label: 'Importe', type: 'decimal', required: true },
    ] },
    { op: 'setAutomations', entity: 'P14Mov', automations: [{ name: 'Saldo', label: 'Actualizar el saldo', on: 'create', actions: [
      { type: 'rollup', entity: 'P14Saldo', match: [{ field: 'Cuenta', source: 'Cuenta' }], createMissing: true, field: 'Saldo', sum: 'Importe' }] }] },
  ] });
  ids.cuenta = (await create('P14Cuenta', { Nombre: 'Caja 014' })).id;
});

test('H1 CA1 · CA2 · CA4: el precio sugerido está en la moneda de la orden y el costo estimado en la moneda base', async () => {
  // Regresión (auditoría 014): la orden en dólares recibía el costo en pesos (170) y la requisición el costo en dólares (10).
  const oc = await create('OrdenCompra', { Proveedor: ids.proveedorUsd, Almacen: ids.almacen, Partidas: [{ Producto: ids.costoMxn, Cantidad: 2 }] });
  expect(oc.Moneda__label).toBe('USD');
  expect(oc.TipoCambio).toBe(17);
  const partida = (oc.Partidas as Row[])[0];
  expect(partida.PrecioUnitario).toBe(10);
  expect(partida.CostoMXN).toBe(170); // el costo en moneda base vuelve a ser el costo último

  const req = await create('Requisicion', { Departamento: ids.departamento, Almacen: ids.almacen, Justificacion: 'E2E 014',
    Partidas: [{ Producto: ids.costoUsd, Cantidad: 1 }, { Producto: ids.costoMxn, Cantidad: 1 }] });
  expect((req.Partidas as Row[]).map((p) => p.CostoEstimado)).toEqual([170, 170]);

  // La moneda del costo se captura (clave de 3 letras) y se sugiere con la moneda base.
  const sinMoneda = await (await admin.get(`/api/data/Producto/${ids.muchos}`)).json();
  expect(sinMoneda.MonedaCosto).toBe('MXN');
  const mala = await admin.put(`/api/data/Producto/${ids.costoUsd}`, { data: { MonedaCosto: 'US', _rowVersion: (await (await admin.get(`/api/data/Producto/${ids.costoUsd}`)).json())._rowVersion } });
  expect(mala.status()).toBe(400);
  expect(JSON.stringify(await mala.json())).toContain('clave de 3 letras');
});

test('H1 CA3: sin tipo de cambio no se sugiere un precio en otra moneda', async () => {
  // Cálculo en vivo de una orden en euros (sin tipo registrado): tipo de cambio y precio vacíos, para capturarse.
  const eur = { id: await lookup('Moneda', 'EUR') };
  const res = await admin.post('/api/data/OrdenCompra/compute', { data: { values: { Proveedor: ids.proveedorUsd, Almacen: ids.almacen, Moneda: eur.id,
    Partidas: [{ Producto: ids.costoMxn, Cantidad: 1 }, { Cantidad: 1 }] } } });
  expect(res.status(), await res.text()).toBe(200);
  const { values } = await res.json();
  expect(values.TipoCambio ?? null).toBeNull();
  expect(values.Partidas.map((p: Row) => p.PrecioUnitario ?? null)).toEqual([null, null]); // antes: 170 (pesos) y nada
});

test('H2 CA1: el acumulado suma en la base aunque haya más de 10 000 movimientos', async () => {
  // Regresión (auditoría 014): pasado el tope en memoria, todo movimiento nuevo del producto fallaba con 400.
  // 10 001 entradas previas se insertan directo (preparación de datos); el movimiento que se prueba va por la API.
  sql(`INSERT app.MovimientoInventario (Folio, Fecha, Tipo, Almacen, Producto, Cantidad, Efecto, Estado, CompanyId, CreatedBy)
       SELECT 'B14-' + CAST(n AS varchar(10)), CAST(GETDATE() AS date), N'Entrada', ${ids.almacen}, ${ids.muchos}, 1, 1, N'Aplicado', ${ids.company},
              (SELECT Id FROM meta.[User] WHERE Username = N'admin')
         FROM (SELECT TOP 10001 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x`);
  const res = await admin.post('/api/data/MovimientoInventario', { data: { Tipo: 'Entrada', Almacen: ids.almacen, Producto: ids.muchos, Cantidad: 5 } });
  expect(res.status(), await res.text()).toBe(201);
  expect((await existencia(ids.muchos)).Cantidad).toBe(10_006);
});

test('H6 CA1: salidas simultáneas del mismo producto y almacén: pasan las que caben, el resto 400', async () => {
  // Riesgo residual de la 012 (6a): pasa también con el código anterior (la secuencia de folios serializa las altas).
  await create('MovimientoInventario', { Tipo: 'Entrada', Almacen: ids.almacen, Producto: ids.stock, Cantidad: 10 });
  const results = await Promise.all(Array.from({ length: 10 }, () =>
    admin.post('/api/data/MovimientoInventario', { data: { Tipo: 'Salida', Almacen: ids.almacen, Producto: ids.stock, Cantidad: 3 } })
      .then(async (r) => ({ status: r.status(), title: ((await r.json()) as { title?: string }).title ?? '' }))));
  expect(results.map((r) => r.status).sort()).toEqual([201, 201, 201, 400, 400, 400, 400, 400, 400, 400]);
  for (const r of results.filter((x) => x.status === 400)) expect(r.title).toContain('No hay existencias suficientes');
  expect((await existencia(ids.stock)).Cantidad).toBe(1);
});

test('H6 CA1: cargos simultáneos a la misma cuenta en un catálogo sin folio: pasan los que caben, sin esperas', async () => {
  // Regresión (riesgo residual 012, 6a): sin folio nada serializaba las altas; con el código anterior esperaban 10 s al
  // bloqueo por clave y se rechazaba un cargo que cabía (dos 201 en lugar de tres).
  const started = Date.now();
  await create('P14Mov', { Cuenta: ids.cuenta, Importe: 10 });
  const statuses = await Promise.all(Array.from({ length: 10 }, () =>
    admin.post('/api/data/P14Mov', { data: { Cuenta: ids.cuenta, Importe: -3 } }).then((r) => r.status())));
  expect(statuses.sort()).toEqual([201, 201, 201, 400, 400, 400, 400, 400, 400, 400]);
  expect(Date.now() - started).toBeLessThan(5000);
  const saldo = (await (await admin.get('/api/data/P14Saldo')).json()).items as Row[];
  expect(saldo.find((s) => s.Cuenta === ids.cuenta)?.Saldo).toBe(1);
});

// ---------------- H3: folios de la importación ----------------
const csv = (rows: number, grupo: string, extra = '') =>
  Buffer.from(['Nombre;Grupo', ...Array.from({ length: rows }, (_, i) => `${grupo} ${i + 1};${grupo}`), ...(extra ? [extra] : [])].join('\n'));
const importFile = (file: Buffer, dryRun: boolean) =>
  admin.post('/api/data/P14Ticket/import', { params: { dryRun: String(dryRun) }, data: file, headers: { 'Content-Type': 'application/octet-stream' } });
const folioNumber = (r: Row) => Number(String(r.Folio).replace('T14-', ''));

/**
 * Otra sesión retiene (sin confirmar) el nombre único de la primera fila: la importación queda detenida justo después de
 * tomar su primer folio, que es donde el código anterior retenía la secuencia. Así la prueba no depende de tiempos.
 */
function holdName(nombre: string, seconds: number) {
  const db = DB;
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  const p = spawn('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-Q',
    `SET NOCOUNT ON; BEGIN TRAN; INSERT app.P14Ticket (Folio, Nombre, Grupo, CreatedBy) VALUES (N'EXT-014', N'${nombre}', N'Externo', 1);
     WAITFOR DELAY '00:00:0${seconds}'; ROLLBACK;`]);
  return new Promise<void>((resolve) => p.on('exit', () => resolve()));
}

/** Importa (o previsualiza) mientras otra persona da de alta un ticket; devuelve cuál terminó primero. */
async function createWhileImporting(grupo: string, dryRun: boolean) {
  const held = holdName(`${grupo} 1`, 4);
  await new Promise((r) => setTimeout(r, 1000)); // que la otra sesión ya tenga el nombre
  const started = Date.now();
  let importDone = 0;
  const importing = importFile(csv(200, grupo), dryRun).then((r) => { importDone = Date.now(); return r; });
  await new Promise((r) => setTimeout(r, 500));
  const ticket = await create('P14Ticket', { Nombre: `Alta durante ${grupo}`, Grupo: 'Captura' });
  const createDone = Date.now();
  const res = await importing;
  await held;
  expect(res.status(), await res.text()).toBe(200);
  expect(importDone - started, 'la importación esperó a la otra sesión').toBeGreaterThan(1500);
  return { ticket, createdFirst: createDone < importDone, result: await res.json() };
}

test('H3 CA1: la vista previa no toca la secuencia ni detiene las altas', async () => {
  // Regresión (diferido de la 012): el alta esperaba a que la vista previa liberara el folio.
  const before = await create('P14Ticket', { Nombre: 'Antes', Grupo: 'Captura' });
  const { ticket, createdFirst, result } = await createWhileImporting('Vista', true);
  expect(result).toMatchObject({ dryRun: true, valid: true, imported: 0 });
  expect(createdFirst, 'el alta no espera a la vista previa').toBe(true);
  expect(folioNumber(ticket)).toBe(folioNumber(before) + 1); // la vista previa no consumió folios
});

test('H3 CA2 · CA3: la importación reserva su bloque, no detiene altas y devuelve el bloque si falla', async () => {
  const { ticket, createdFirst, result } = await createWhileImporting('Importados', false);
  expect(result).toMatchObject({ dryRun: false, imported: 200 });
  expect(createdFirst, 'el alta no espera a la importación').toBe(true);
  const [count, min, max] = sql(`SELECT CONCAT(COUNT(*), ' ', MIN(CAST(REPLACE(Folio, 'T14-', '') AS int)), ' ', MAX(CAST(REPLACE(Folio, 'T14-', '') AS int)))
    FROM app.P14Ticket WHERE Grupo = N'Importados' AND IsDeleted = 0`).split(' ').map(Number);
  // Folios consecutivos para lo importado, y el alta simultánea recibe el siguiente al bloque.
  expect(count).toBe(200);
  expect(max - min).toBe(199);
  expect(folioNumber(ticket)).toBe(max + 1);

  // Una importación con error devuelve su bloque: el siguiente folio no deja hueco.
  const failed = await importFile(csv(20, 'Fallidos', ';SinNombre'), false);
  expect(failed.status()).toBe(400);
  const next = await create('P14Ticket', { Nombre: 'Después del error', Grupo: 'Captura' });
  expect(folioNumber(next)).toBe(folioNumber(ticket) + 1);
});

test('H6 CA2: con aprobación y cambios directos a la vez, Entornos lo advierte', async ({ page }) => {
  const info = await (await admin.get('/api/admin/environments')).json();
  expect(info).toMatchObject({ requireApproval: true, directChangesOverride: true }); // así corre la suite
  await page.goto('/admin/environments');
  await expect(page.getByText('Este entorno exige aprobación, pero permite cambios directos')).toBeVisible();
  await expect(page.getByText('Environments:AllowDirectChanges')).toBeVisible();
});
