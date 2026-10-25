// Spec 016 · Automatizaciones asíncronas y programadas: bandeja transaccional, tareas por calendario con un rol de
// servicio, bitácora y reintentos. La bandeja corre en segundo plano cada segundo en la suite (Automations__PollSeconds).
// Para simular que «llegó la hora» de una tarea se adelanta su siguiente ocurrencia con SQL sobre la base desechable.
import { execFileSync, spawn } from 'node:child_process';
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, DB, anonApi, apiAs, applyPlan, expect, expectAccessible, openJson, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 016'; // grupo propio del menú: no altera las capturas de referencia
const PWD = 'frase larga de tareas programadas 2026';
const users = { capturista: 'e2e-j16-capturista', temporal: 'e2e-j16-temporal' } as const;
type Who = 'admin' | keyof typeof users;
type Row = Record<string, unknown> & { id: number };
type Run = { id: number; kind: string; name: string; label: string; recordId?: number; runAs: string; manual: boolean; status: string; attempts: number;
  processed: number; succeeded: number; failed: number; skipped: number; more: boolean; lastError?: string | null; nextAttemptAt?: string | null };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
const ids: Record<string, number> = {};
const rec: Record<string, Row> = {};

/** Fecha local a n días de hoy (aaaa-mm-dd): el servidor compara con su TODAY() local. */
const day = (n: number) => { const d = new Date(); d.setDate(d.getDate() + n); return d.toLocaleDateString('en-CA'); };

/** SQL directo sobre la base desechable de la suite (nunca la de desarrollo). */
function sql(query: string) {
  const db = DB;
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  return execFileSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-h', '-1', '-W', '-Q',
    `SET NOCOUNT ON; ${query}`]).toString().trim();
}

/** Mantiene bloqueado un pedido unos segundos desde otra conexión (como una persona guardándolo). */
function holdLock(id: number, seconds: number) {
  const db = DB;
  const p = spawn('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-Q',
    `SET NOCOUNT ON; BEGIN TRAN; SELECT [Id] FROM app.J16Pedido WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = ${Number(id)}; WAITFOR DELAY '00:00:0${seconds}'; ROLLBACK;`]);
  return new Promise<void>((resolve) => p.on('exit', () => resolve()));
}

/** Visible para una persona: en el centro del elemento no hay otra cosa encima ni está recortado (ver pick() en compras). */
async function expectTopmost(locator: import('@playwright/test').Locator) {
  await expect(locator).toBeVisible();
  const ok = await locator.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  });
  expect(ok, 'el control debe verse sin desplazar la tabla').toBe(true);
}

async function create(w: Who, entity: string, data: object) {
  const res = await (await as(w)).post(`/api/data/${entity}`, { data });
  return { status: res.status(), body: await res.json() as Row & { title?: string } };
}
const get = async (entity: string, id: number) => (await (await as('admin')).get(`/api/data/${entity}/${id}`)).json() as Promise<Row>;
/** Edita con la versión actual del registro (concurrencia optimista). */
async function update(w: Who, entity: string, id: number, data: object) {
  const current = await get(entity, id);
  const res = await (await as(w)).put(`/api/data/${entity}/${id}`, { data: { ...data, _rowVersion: current._rowVersion } });
  expect(res.status(), await res.text()).toBe(200);
}
const count = async (entity: string) => (await (await (await as('admin')).get(`/api/data/${entity}`)).json()).total as number;

async function runs(q: Record<string, string> = {}): Promise<Run[]> {
  const res = await (await as('admin')).get('/api/admin/automations/runs', { params: q });
  expect(res.status(), await res.text()).toBe(200);
  return res.json();
}
const asyncRuns = (name = 'RegistrarUltimo', entity = 'J16Pedido') => runs({ kind: 'async', entity, name });
const runFor = async (recordId: number) => (await asyncRuns()).find((r) => r.recordId === recordId);

/** Espera a que la ejecución termine (sale de pendiente/en curso) y la devuelve. */
async function settle(find: () => Promise<Run | undefined>): Promise<Run> {
  let last: Run | undefined;
  await expect.poll(async () => { last = await find(); return last && !['pending', 'running'].includes(last.status); }, { timeout: 20_000 }).toBe(true);
  return last!;
}
const lastSchedule = async () => (await runs({ kind: 'schedule', entity: 'J16Pedido', name: 'VencerPedidos' }))[0];

async function schedule() {
  const list = await (await (await as('admin')).get('/api/admin/automations/schedules')).json() as
    { entity: string; name: string; active: boolean; nextRunAt?: string | null; calendar: string; role: string; batchSize: number }[];
  return list.find((s) => s.entity === 'J16Pedido' && s.name === 'VencerPedidos')!;
}

const SCHEDULE = {
  name: 'VencerPedidos', label: 'Vencer pedidos', description: 'Pedidos abiertos con vencimiento pasado',
  every: 'day', at: '07:00', where: "Estado = 'Abierto' AND Vence < TODAY()", role: 'J16Cobranza', batchSize: 2,
  actions: [
    { type: 'transition', transition: 'vencer' },
    { type: 'update', target: 'Cliente', set: [{ field: 'Avisos', value: 'Cliente.Avisos + 1' }] },
  ],
};

// Gen1 → Gen2 → … → Gen6 → Fin: cada alta crea la siguiente desde la bandeja (sin ciclo, así que la vista previa la acepta).
const chain = () => {
  const ops: object[] = [{ op: 'createEntity', group: GROUP, name: 'J16Fin', label: 'Fin 016', labelPlural: 'Fines 016', fields: [{ name: 'Nota', label: 'Nota', type: 'text' }] }];
  for (let i = 1; i <= 6; i++)
    ops.push({ op: 'createEntity', group: GROUP, name: `J16Gen${i}`, label: `Generación ${i}`, labelPlural: `Generaciones ${i}`, fields: [{ name: 'Nota', label: 'Nota', type: 'text' }] });
  for (let i = 1; i <= 6; i++) {
    const next = i === 6 ? 'J16Fin' : `J16Gen${i + 1}`;
    ops.push({ op: 'setAutomations', entity: `J16Gen${i}`, automations: [{ name: 'Siguiente', label: `Siguiente generación ${i}`, on: 'create', mode: 'async',
      actions: [{ type: 'create', entity: next, set: [{ field: 'Nota', value: 'Nota' }] }] }] });
  }
  return ops;
};

test('preparar: roles, usuarios, catálogos con una asíncrona y una tarea programada', async () => {
  const admin = await as('admin');
  const flags = { canManageModel: false, canManageUsers: false, canViewAudit: false };
  for (const name of ['J16Cobranza', 'J16Capturista']) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 016', ...flags, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    ids[name] = (await res.json()).id;
  }
  await applyPlan(admin, { description: 'Spec 016 · pedidos', operations: [
    { op: 'createEntity', group: GROUP, name: 'J16Cliente', label: 'Cliente 016', labelPlural: 'Clientes 016', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Bloqueado', label: 'Bloqueado', type: 'boolean' },
      { name: 'Avisos', label: 'Avisos', type: 'decimal', defaultValue: '0' },
      { name: 'UltimoPedido', label: 'Último pedido', type: 'text', editable: false },
      { name: 'UltimaNota', label: 'Última nota', type: 'text', editable: false },
      { name: 'UltimoPor', label: 'Último por', type: 'user', editable: false },
    ] },
    { op: 'setRules', entity: 'J16Cliente', rules: [
      { name: 'NoBloqueado', message: 'El cliente está bloqueado.', assert: 'ISBLANK(UltimoPedido) OR NOT Bloqueado' },
      { name: 'MaxAvisos', message: 'El cliente ya tiene el máximo de avisos.', assert: 'Avisos <= 2' },
    ] },
    { op: 'createEntity', group: GROUP, name: 'J16Pedido', label: 'Pedido 016', labelPlural: 'Pedidos 016', displayField: 'Folio', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'P16-', padding: 4 },
      { name: 'Cliente', label: 'Cliente', type: 'reference', referenceEntity: 'J16Cliente', required: true },
      { name: 'Region', label: 'Región', type: 'choice', options: ['Norte', 'Sur'], required: true },
      { name: 'Vence', label: 'Vence', type: 'date' },
      { name: 'Monto', label: 'Monto', type: 'decimal' },
      { name: 'Notas', label: 'Notas', type: 'text' },
    ] },
    { op: 'setRules', entity: 'J16Pedido', rules: [{ name: 'MontoPositivo', message: 'El monto no puede ser negativo.', assert: 'Monto >= 0' }] },
    { op: 'setWorkflow', entity: 'J16Pedido', workflow: { initial: 'Abierto',
      states: [{ name: 'Abierto', label: 'Abierto' }, { name: 'Vencido', label: 'Vencido', editable: false }],
      transitions: [{ name: 'vencer', label: 'Vencer', from: ['Abierto'], to: 'Vencido', system: true }] } },
    { op: 'setAutomations', entity: 'J16Pedido', automations: [{ name: 'RegistrarUltimo', label: 'Registrar último pedido', on: 'create', mode: 'async',
      when: 'Monto > 0', actions: [{ type: 'update', target: 'Cliente', set: [
        { field: 'UltimoPedido', value: 'Folio' }, { field: 'UltimaNota', value: 'Notas' }, { field: 'UltimoPor', value: 'CURRENTUSER()' }] }] }] },
    // El rol de servicio de la tarea solo ve los pedidos del norte.
    { op: 'setSecurity', entity: 'J16Pedido', security: { records: [{ roles: ['J16Cobranza'], where: "Region = 'Norte'", access: 'read' }, { roles: ['J16Capturista'] }] } },
    { op: 'setSchedules', entity: 'J16Pedido', schedules: [SCHEDULE] },
    ...chain(),
  ] });
  const perms = (entity: string, all: boolean) => ({ entity, canRead: true, canCreate: all, canUpdate: all, canDelete: all });
  for (const [role, list] of [['J16Cobranza', [perms('J16Pedido', false)]], ['J16Capturista', [perms('J16Pedido', true), perms('J16Cliente', false)]]] as const) {
    const res = await admin.put(`/api/admin/roles/${ids[role]}`, { data: { name: role, description: 'E2E spec 016', ...flags, permissions: list } });
    expect(res.status(), await res.text()).toBe(200);
  }
  for (const [key, username] of Object.entries(users)) {
    const res = await admin.post('/api/admin/users', { data: { username, displayName: key === 'capturista' ? 'Capturista J16' : 'Temporal J16', password: PWD, roleIds: [ids.J16Capturista] } });
    expect(res.status(), await res.text()).toBe(201);
    ids[key] = (await res.json()).id;
  }
  for (const [key, data] of [['libre', { Nombre: 'Cliente libre' }], ['bloqueado', { Nombre: 'Cliente bloqueado', Bloqueado: true }],
    ['bloqueado2', { Nombre: 'Cliente bloqueado dos', Bloqueado: true }], ['a', { Nombre: 'Cliente A' }], ['b', { Nombre: 'Cliente B' }],
    ['sur', { Nombre: 'Cliente Sur' }]] as const) {
    const r = await create('admin', 'J16Cliente', data);
    expect(r.status, JSON.stringify(r.body)).toBe(201);
    rec[key] = r.body;
  }
});

test('H1 CA1: el trabajo se encola en la transacción del evento; si se revierte o no cumple «when», no queda nada', async () => {
  const before = (await asyncRuns()).length;
  const bad = await create('capturista', 'J16Pedido', { Cliente: rec.libre.id, Region: 'Norte', Monto: -5 });
  expect(bad.status).toBe(400);
  const zero = await create('capturista', 'J16Pedido', { Cliente: rec.libre.id, Region: 'Norte', Monto: 0 });
  expect(zero.status).toBe(201);
  expect((await asyncRuns()).length).toBe(before);

  // Camino feliz: la persona recibe su respuesta y la automatización corre después, a su nombre.
  const ok = await create('capturista', 'J16Pedido', { Cliente: rec.libre.id, Region: 'Norte', Monto: 10, Notas: 'primera' });
  expect(ok.status).toBe(201);
  const run = await settle(() => runFor(ok.body.id));
  expect(run.status).toBe('succeeded');
  expect(run.runAs).toBe('Capturista J16');
  const cliente = await get('J16Cliente', rec.libre.id);
  expect(cliente.UltimoPedido).toBe(ok.body.Folio);
  expect(cliente.UltimoPor).toBe(ids.capturista);
});

test('H1 CA2 · CA3 · H4 CA3: una falla no afecta a quien guardó; el negocio no se reintenta; reintentar usa los datos actuales', async () => {
  const p = await create('capturista', 'J16Pedido', { Cliente: rec.bloqueado.id, Region: 'Norte', Monto: 25, Notas: 'al guardar' });
  expect(p.status, 'quien guarda no se entera de la falla').toBe(201);
  const failed = await settle(() => runFor(p.body.id));
  expect(failed.status).toBe('failed');
  expect(failed.attempts, 'una regla del negocio no se reintenta').toBe(1);
  expect(failed.lastError).toMatch(/Registrar último pedido.*El cliente está bloqueado/);
  expect((await get('J16Cliente', rec.bloqueado.id)).UltimoPedido ?? null).toBeNull();
  expect((await get('J16Pedido', p.body.id)).Estado, 'el pedido se guardó').toBe('Abierto');

  // Se corrige el dato y cambia el pedido después del evento: el reintento lee lo de ahora.
  const admin = await as('admin');
  await update('admin', 'J16Cliente', rec.bloqueado.id, { Bloqueado: false });
  await update('capturista', 'J16Pedido', p.body.id, { Notas: 'después del evento' });
  expect((await admin.post(`/api/admin/automations/runs/${failed.id}/retry`)).status()).toBe(204);
  const retried = await settle(() => runFor(p.body.id));
  expect(retried.status).toBe('succeeded');
  const cliente = await get('J16Cliente', rec.bloqueado.id);
  expect(cliente.UltimoPedido).toBe(p.body.Folio);
  expect(cliente.UltimaNota).toBe('después del evento');
  expect(cliente.UltimoPor, 'a nombre de quien disparó el evento, no de quien reintentó').toBe(ids.capturista);
  // Solo lo fallido o cancelado se reintenta.
  expect((await admin.post(`/api/admin/automations/runs/${retried.id}/retry`)).status()).toBe(404);

  const audit = await (await admin.get('/api/audit', { params: { entity: 'J16Cliente', action: 'automation.update', pageSize: '20' } })).json();
  const entry = audit.items.find((a: { recordId: string }) => a.recordId === String(rec.bloqueado.id));
  expect(entry.user).toBe(users.capturista);
  const retryAudit = await (await admin.get('/api/audit', { params: { action: 'automation.retry', pageSize: '5' } })).json();
  expect(retryAudit.items[0].after.run).toBe(failed.id);
});

test('H1 CA4: se cancela si quien la disparó ya no está activo o si el registro ya no existe', async () => {
  const admin = await as('admin');
  const p = await create('temporal', 'J16Pedido', { Cliente: rec.bloqueado2.id, Region: 'Norte', Monto: 5 });
  expect(p.status).toBe(201);
  const failed = await settle(() => runFor(p.body.id));
  expect(failed.status).toBe('failed');
  await update('admin', 'J16Cliente', rec.bloqueado2.id, { Bloqueado: false });
  expect((await admin.put(`/api/admin/users/${ids.temporal}`, { data: { displayName: 'Temporal J16', isActive: false, roleIds: [ids.J16Capturista] } })).status()).toBe(200);
  expect((await admin.post(`/api/admin/automations/runs/${failed.id}/retry`)).status()).toBe(204);
  const canceled = await settle(() => runFor(p.body.id));
  expect(canceled.status).toBe('canceled');
  expect(canceled.lastError).toMatch(/ya no está activo/);
  expect((await get('J16Cliente', rec.bloqueado2.id)).UltimoPedido ?? null, 'no actuó a nombre de alguien dado de baja').toBeNull();

  // Registro eliminado antes de reintentar.
  await update('admin', 'J16Cliente', rec.bloqueado2.id, { Bloqueado: true });
  const q = await create('capturista', 'J16Pedido', { Cliente: rec.bloqueado2.id, Region: 'Norte', Monto: 5 });
  const failed2 = await settle(() => runFor(q.body.id));
  expect(failed2.status).toBe('failed');
  expect((await (await as('capturista')).delete(`/api/data/J16Pedido/${q.body.id}`)).status()).toBe(204);
  expect((await admin.post(`/api/admin/automations/runs/${failed2.id}/retry`)).status()).toBe(204);
  const gone = await settle(() => runFor(q.body.id));
  expect(gone.status).toBe('canceled');
  expect(gone.lastError).toMatch(/El registro ya no existe/);
});

test('H1 CA5: una cadena de asíncronas se corta a las 5 generaciones', async () => {
  // Gen2 … Gen6 → Fin: cinco generaciones, se completa.
  expect((await create('admin', 'J16Gen2', { Nota: 'cadena corta' })).status).toBe(201);
  await expect.poll(() => count('J16Fin'), { timeout: 30_000 }).toBe(1);
  // Gen1 … : la sexta generación falla y no queda su registro.
  expect((await create('admin', 'J16Gen1', { Nota: 'cadena larga' })).status).toBe(201);
  const cut = await settle(async () => (await asyncRuns('Siguiente', 'J16Gen5')).find((r) => r.status !== 'succeeded'));
  expect(cut.status).toBe('failed');
  expect(cut.lastError).toMatch(/se encadenaron más de 5 veces/);
  expect(await count('J16Gen6')).toBe(1); // solo el de la cadena corta
  expect(await count('J16Fin')).toBe(1);
});

test('H2 CA1 · CA2 · H5 CA1: la vista previa valida la tarea; llega en pausa y el modelador la muestra', async ({ page }) => {
  const admin = await as('admin');
  const preview = async (s: object) => ((await (await admin.post('/api/model/plan/preview', { data: { description: 'x',
    operations: [{ op: 'setSchedules', entity: 'J16Pedido', schedules: [{ ...SCHEDULE, ...s }] }] } })).json()) as { valid: boolean; errors: { message: string }[] })
    .errors.map((e) => e.message).join(' | ');
  expect(await preview({ where: "HASROLE('J16Cobranza')" })).toMatch(/resolverse en la base/);
  expect(await preview({ every: 'day', at: '7 am' })).toMatch(/HH:mm/);
  expect(await preview({ role: 'Nadie' })).toMatch(/rol 'Nadie' no existe/);
  expect(await preview({ batchSize: 5000 })).toMatch(/tamaño de lote/);
  expect(await preview({ actions: [{ type: 'transition', transition: 'cerrar' }] })).toMatch(/transición 'cerrar' no existe/);
  const mode = await (await admin.post('/api/model/plan/preview', { data: { description: 'x', operations: [{ op: 'setAutomations', entity: 'J16Pedido', merge: true,
    automations: [{ name: 'Otra', label: 'Otra', on: 'create', mode: 'despues', actions: [{ type: 'transition', transition: 'vencer' }] }] }] } })).json();
  expect(mode.errors.map((e: { message: string }) => e.message).join(' ')).toMatch(/modo 'despues' no válido/);

  const s = await schedule();
  expect(s).toMatchObject({ active: false, calendar: 'Diaria a las 07:00', role: 'J16Cobranza', batchSize: 2 });
  expect(s.nextRunAt ?? null).toBeNull();
  expect(await runs({ kind: 'schedule', entity: 'J16Pedido' })).toEqual([]);

  await page.goto('/admin/model?edit=J16Pedido&tab=integraciones');
  await expect(await openJson(page, 'Tareas programadas (JSON)')).toHaveValue(/"VencerPedidos"/);
  await page.getByRole('tab', { name: 'Automatizaciones' }).click();
  await expect(await openJson(page, 'Automatizaciones (JSON)')).toHaveValue(/"mode": "async"/);
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
});

test('H2 CA3 · CA4 · H3 CA1 · CA2 · H4 CA1 · CA2: ejecutar ahora desde Configuración › Automatizaciones; lote, alcance del rol, por registro y bitácora', async ({ page }) => {
  for (const [key, data] of [
    ['p1', { Cliente: rec.a.id, Region: 'Norte', Vence: day(-3) }],
    ['p2', { Cliente: rec.b.id, Region: 'Norte', Vence: day(-3) }],
    ['p3', { Cliente: rec.a.id, Region: 'Norte', Vence: day(-2) }],
    ['p4', { Cliente: rec.sur.id, Region: 'Sur', Vence: day(-3) }],
    ['p5', { Cliente: rec.a.id, Region: 'Norte', Vence: day(3) }],
  ] as const) {
    const r = await create('admin', 'J16Pedido', data);
    expect(r.status).toBe(201);
    rec[key] = r.body;
  }
  // El cliente B ya tiene dos avisos: el tercero viola su regla y ese pedido no se vence (los demás sí).
  sql(`UPDATE app.J16Cliente SET Avisos = 2 WHERE Id = ${rec.b.id}`);

  await page.goto('/admin/automations');
  await page.getByRole('tab', { name: 'Tareas programadas' }).click();
  const row = page.getByRole('row', { name: /Vencer pedidos/ });
  await expect(row).toContainText('Diaria a las 07:00');
  await expect(row).toContainText('En pausa');
  await expect(row.getByRole('switch', { name: 'Activa: Vencer pedidos' })).not.toBeChecked();
  await expectAccessible(page);
  await row.getByRole('button', { name: 'Ejecutar ahora Vencer pedidos' }).click();
  await expect(page.getByText('«Vencer pedidos» en cola')).toBeVisible();
  // Regresión (revisión exploratoria 016, F-04): la fila se quedaba en «Pendiente» hasta pulsar «Actualizar».
  await expect(row).toContainText('Con errores', { timeout: 10_000 });

  const first = await settle(lastSchedule);
  expect(first).toMatchObject({ status: 'partial', manual: true, processed: 2, succeeded: 1, failed: 1, more: true, runAs: 'Tarea programada (J16Cobranza)' });
  expect((await get('J16Pedido', rec.p1.id)).Estado).toBe('Vencido');
  expect((await get('J16Pedido', rec.p2.id)).Estado, 'su falla revirtió solo ese pedido').toBe('Abierto');
  expect((await get('J16Pedido', rec.p3.id)).Estado, 'el lote era de 2').toBe('Abierto');
  expect((await get('J16Pedido', rec.p4.id)).Estado, 'fuera del alcance del rol').toBe('Abierto');
  expect((await get('J16Pedido', rec.p5.id)).Estado, 'no ha vencido').toBe('Abierto');
  expect((await get('J16Cliente', rec.a.id)).Avisos).toBe(1);
  expect((await get('J16Cliente', rec.b.id)).Avisos).toBe(2);

  // Identidad: el cambio queda a nombre de la cuenta de sistema y la auditoría nombra la tarea.
  const history = await (await (await as('admin')).get(`/api/data/J16Pedido/${rec.p1.id}/history`)).json();
  expect(history.at(-1)).toMatchObject({ user: 'Tareas programadas', to: 'Vencido' });
  expect(history.at(-1).comment).toMatch(/Automática: Vencer pedidos/);
  const audit = await (await (await as('admin')).get('/api/audit', { params: { entity: 'J16Pedido', action: 'workflow.vencer', pageSize: '5' } })).json();
  expect(audit.items[0]).toMatchObject({ user: 'tarea:J16Pedido.VencerPedidos', recordId: String(rec.p1.id) });
  // La cuenta de sistema no es una persona: no inicia sesión ni aparece en usuarios o buscadores.
  const anon = await anonApi();
  expect((await anon.post('/api/auth/login', { data: { username: 'sistema.tareas', password: 'cualquier-cosa-larga-2026' } })).status()).toBe(401);
  await anon.dispose();
  const usersList = await (await (await as('admin')).get('/api/admin/users')).json();
  expect(usersList.map((u: { username: string }) => u.username)).not.toContain('sistema.tareas');
  const lookup = await (await (await as('admin')).get('/api/users/lookup', { params: { search: 'Tareas' } })).json();
  expect(lookup).toEqual([]);

  // Bitácora con el detalle de los errores por registro.
  await page.getByRole('button', { name: 'Ver bitácora' }).click();
  const logRow = page.getByRole('row', { name: /Vencer pedidos/ }).first();
  await expect(logRow).toContainText('Con errores');
  await expect(logRow).toContainText('2 procesados · 1 correcto · 1 con error');
  await expect(logRow).toContainText('Quedaron registros para la siguiente ocurrencia');
  await logRow.getByRole('button', { name: /Detalle de la ejecución/ }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Tarea programada (J16Cobranza)');
  await expect(dialog.getByRole('row', { name: new RegExp(`#${rec.p2.id}`) })).toContainText('El cliente ya tiene el máximo de avisos.');
  // Regresión (revisión exploratoria 016, F-03): el resumen repetía el mismo error que la tabla por registro.
  await expect(dialog.getByText(/máximo de avisos/)).toHaveCount(1);
  await expectAccessible(page);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
});

test('H2 CA2 · CA5: al activarla se agenda; al llegar la hora corre una vez aunque se hayan perdido ocurrencias', async ({ page }) => {
  await page.goto('/admin/automations');
  await page.getByRole('tab', { name: 'Tareas programadas' }).click();
  const row = page.getByRole('row', { name: /Vencer pedidos/ });
  // Como una persona: clic en el interruptor visible (su etiqueta), no en la casilla que tapa la pista.
  await row.getByRole('switch', { name: 'Activa: Vencer pedidos' }).locator('xpath=..').click();
  await expect(page.getByText('«Vencer pedidos» activada')).toBeVisible();
  await expect(row.getByRole('switch', { name: 'Activa: Vencer pedidos' })).toBeChecked();
  await expect(row).not.toContainText('En pausa');

  const s = await schedule();
  const next = new Date(s.nextRunAt!);
  expect(next.getTime()).toBeGreaterThan(Date.now());
  expect(next.getTime() - Date.now()).toBeLessThanOrEqual(24 * 3600_000);
  const before = (await runs({ kind: 'schedule', entity: 'J16Pedido' })).length;

  // «Llegó la hora» hace tres días (servidor apagado): corre una sola vez y agenda la siguiente en el futuro.
  sql("UPDATE meta.ScheduleState SET NextRunAt = DATEADD(day, -3, SYSUTCDATETIME()) WHERE EntityName = 'J16Pedido' AND ScheduleName = 'VencerPedidos'");
  const auto = await settle(async () => { const r = await lastSchedule(); return r && !r.manual ? r : undefined; });
  expect(auto).toMatchObject({ status: 'partial', processed: 2, succeeded: 1, failed: 1, more: false });
  await page.waitForTimeout(2500); // otras vueltas de la bandeja no la repiten
  expect((await runs({ kind: 'schedule', entity: 'J16Pedido' })).length).toBe(before + 1);
  expect(new Date((await schedule()).nextRunAt!).getTime()).toBeGreaterThan(Date.now());
  expect((await get('J16Pedido', rec.p3.id)).Estado).toBe('Vencido');
  expect((await get('J16Pedido', rec.p2.id)).Estado, 'sigue fallando y queda para la siguiente').toBe('Abierto');
  expect((await get('J16Pedido', rec.p4.id)).Estado).toBe('Abierto');
  expect((await get('J16Cliente', rec.a.id)).Avisos).toBe(2);
});

test('H2 CA3: dos ejecuciones de la misma tarea no se enciman', async () => {
  const admin = await as('admin');
  // Una persona tiene bloqueado el pedido que la tarea va a procesar: la ejecución espera en curso.
  const lock = holdLock(rec.p2.id, 4);
  await new Promise((r) => setTimeout(r, 700));
  const res = await admin.post('/api/admin/automations/schedules/J16Pedido/VencerPedidos/run');
  expect(res.status(), await res.text()).toBe(200);
  const again = await admin.post('/api/admin/automations/schedules/J16Pedido/VencerPedidos/run');
  expect(again.status()).toBe(409);
  expect((await again.json()).title).toMatch(/pendiente o en curso/);
  await lock;
  const done = await settle(lastSchedule);
  expect(done).toMatchObject({ processed: 1, failed: 1 });
});

test('H3 CA3: si el rol pierde la lectura del catálogo, la ocurrencia falla sin tocar nada', async ({ page }) => {
  const admin = await as('admin');
  const flags = { canManageModel: false, canManageUsers: false, canViewAudit: false };
  const setPerms = (permissions: object[]) => admin.put(`/api/admin/roles/${ids.J16Cobranza}`, { data: { name: 'J16Cobranza', description: 'E2E spec 016', ...flags, permissions } });
  expect((await setPerms([])).status()).toBe(200);
  const p6 = await create('admin', 'J16Pedido', { Cliente: rec.libre.id, Region: 'Norte', Vence: day(-3) });
  expect((await admin.post('/api/admin/automations/schedules/J16Pedido/VencerPedidos/run')).status()).toBe(200);
  const failed = await settle(lastSchedule);
  expect(failed.status).toBe('failed');
  expect(failed.lastError).toMatch(/El rol «J16Cobranza» no puede leer pedidos 016/);
  expect((await get('J16Pedido', p6.body.id)).Estado).toBe('Abierto');
  // Regresión (revisión exploratoria 016, F-01): la tarea decía «Fallida · 0 procesados» sin el motivo.
  await page.goto('/admin/automations');
  await page.getByRole('tab', { name: 'Tareas programadas' }).click();
  const row = page.getByRole('row', { name: /Vencer pedidos/ });
  await expect(row).toContainText('Fallida');
  await expect(row).toContainText('no puede leer pedidos 016');
  await expect(row).not.toContainText('0 procesados');
  await page.getByRole('tab', { name: 'Bitácora' }).click();
  const logRow = page.getByRole('row', { name: /Vencer pedidos/ }).first();
  await expect(logRow).toContainText('no puede leer pedidos 016');
  await expect(logRow).not.toContainText('0 procesados');
  expect((await setPerms([{ entity: 'J16Pedido', canRead: true, canCreate: false, canUpdate: false, canDelete: false }])).status()).toBe(200);
});

test('H4 CA4: solo el superusuario ve Automatizaciones en el menú y llega por la ruta', async ({ browser, page }) => {
  await page.goto('/');
  await page.getByRole('banner').getByRole('button', { name: 'Configuración' }).click(); // spec 024: área de configuración
  const nav = page.getByLabel('Navegación principal');
  await nav.getByRole('link', { name: 'Automatizaciones' }).click();
  await expect(page).toHaveURL(/\/admin\/automations$/);
  await expect(page.getByRole('heading', { name: 'Automatizaciones' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Tareas programadas' })).toHaveAttribute('aria-selected', 'true');
  // Integraciones vuelve a ser solo webhooks y credenciales (spec 018: también clientes OAuth).
  await page.goto('/admin/integrations');
  await expect(page.getByRole('tab')).toHaveText(['Webhooks', 'Claves de API', 'Clientes OAuth']);

  const ctx = await browser.newContext({ storageState: undefined });
  const other = await ctx.newPage();
  await uiLogin(other, users.capturista, PWD);
  await expect(other.getByRole('link', { name: 'Automatizaciones' })).toHaveCount(0);
  await other.goto('/admin/automations');
  await expect(other.getByRole('heading', { name: 'Automatizaciones' })).toHaveCount(0);
  await ctx.close();
});

test('H4 CA4: pausar, auditoría y solo el superusuario administra', async () => {
  const admin = await as('admin');
  const res = await admin.put('/api/admin/automations/schedules/J16Pedido/VencerPedidos', { data: { active: false } });
  expect(res.status()).toBe(200);
  expect(await res.json()).toMatchObject({ active: false, nextRunAt: null });
  const audit = await (await admin.get('/api/audit', { params: { entity: 'J16Pedido', action: 'automation.schedule', pageSize: '20' } })).json();
  const actions = audit.items.map((a: { after: { action: string } }) => a.after.action);
  expect(actions).toEqual(expect.arrayContaining(['activate', 'pause', 'run']));

  const capturista = await as('capturista');
  for (const [method, url] of [['get', '/api/admin/automations/schedules'], ['get', '/api/admin/automations/runs'],
    ['put', '/api/admin/automations/schedules/J16Pedido/VencerPedidos'], ['post', '/api/admin/automations/schedules/J16Pedido/VencerPedidos/run']] as const) {
    const r = method === 'put' ? await capturista.put(url, { data: { active: true } }) : method === 'post' ? await capturista.post(url) : await capturista.get(url);
    expect(r.status(), `${method} ${url}`).toBe(403);
  }
  expect((await admin.post('/api/admin/automations/schedules/J16Pedido/NoExiste/run')).status()).toBe(404);
});

// Regresión (revisión exploratoria 016, F-02): con tantas columnas, a 1280 px «Detalle» quedaba fuera de la tabla visible y
// había que desplazarla para ver el resultado; en las tareas, el interruptor y «Ejecutar ahora» igual en pantallas angostas.
test('H4 CA1 · CA2: a 1280 px los controles de las tareas y de la bitácora se ven sin desplazar la tabla', async ({ page }) => {
  await page.goto('/admin/automations');
  await page.getByRole('tab', { name: 'Tareas programadas' }).click();
  const row = page.getByRole('row', { name: /Vencer pedidos/ });
  await expectTopmost(row.getByRole('button', { name: 'Ejecutar ahora Vencer pedidos' }));
  await expectTopmost(row.getByRole('switch', { name: 'Activa: Vencer pedidos' }).locator('xpath=..'));
  await page.getByRole('tab', { name: 'Bitácora' }).click();
  const first = page.getByRole('row', { name: /Vencer pedidos/ }).first();
  await expectTopmost(first.getByRole('button', { name: /Detalle de la ejecución/ }));
  await expectTopmost(first.getByText(/no puede leer pedidos 016/));
});

test('H4 CA1 · CA2: en móvil las pestañas y la bitácora caben sin desplazamiento horizontal', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/admin/automations');
  await page.getByRole('tab', { name: 'Bitácora' }).click();
  await expect(page.getByRole('row', { name: /Vencer pedidos/ }).first()).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await page.getByLabel('Tipo').selectOption('async');
  await expect(page.getByRole('row', { name: /Registrar último pedido/ }).first()).toBeVisible();
  await expect(page.getByRole('row', { name: /Vencer pedidos/ })).toHaveCount(0);
  await expectAccessible(page);
});
