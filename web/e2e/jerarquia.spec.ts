// Spec 015 · Jerarquía de roles, equipos y unidades de negocio. Los catálogos del grupo «Pruebas 015» se archivan al
// terminar; unidades, equipos y usuarios son datos del entorno y llevan el prefijo J15 / e2e-j15.
import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { request, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { ADMIN, DB, PORTS, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 015';
const PWD = 'frase larga de jerarquia 2026';
const users = { ana: 'e2e-j15-ana', beto: 'e2e-j15-beto', cris: 'e2e-j15-cris', gina: 'e2e-j15-gina', sur: 'e2e-j15-sur', admin2: 'e2e-j15-admin2' } as const;
type Who = keyof typeof users | 'admin';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };
type Unit = { id: number; name: string; parentId: number | null; depth: number; users: number; teams: number };
type Change = { id: number; kind: string; summary: string; status: string; hash: string; canApprove: boolean; canReject: boolean };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
async function json(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Row & { items: Row[]; total: number } };
}
const list = async (w: Who, entity = 'J15Caso') => (await json(await (await as(w)).get(`/api/data/${entity}`, { params: { pageSize: '100' } }))).body;
const titles = async (w: Who) => (await list(w)).items.map((r) => r.Titulo).sort();
const create = async (w: Who, data: object, entity = 'J15Caso') => json(await (await as(w)).post(`/api/data/${entity}`, { data }));

const ids: Record<string, number> = {};
const unit: Record<string, Unit> = {};
const rec: Record<string, Row> = {};

async function units(): Promise<Unit[]> {
  return (await (await as('admin')).get('/api/admin/units')).json();
}

test.afterAll(async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await admin.get('/api/model/entities')).json()) as { name: string }[];
  const operations = ['J15Nota', 'J15Caso'].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(admin, { description: 'E2E 015: limpieza', operations });
  await admin.dispose();
});

test('preparar: árbol de unidades, roles con herencia, catálogo con equipos dueños, usuarios', async () => {
  const admin = await as('admin');
  // H1 CA1: J15 Norte › (J15 Monterrey, J15 Saltillo); J15 Sur aparte.
  for (const [name, parent] of [['J15 Norte', null], ['J15 Monterrey', 'J15 Norte'], ['J15 Saltillo', 'J15 Norte'], ['J15 Sur', null]] as const) {
    const res = await admin.post('/api/admin/units', { data: { name, parentId: parent ? unit[parent].id : null } });
    expect(res.status(), await res.text()).toBe(201);
    unit[name] = await res.json();
  }
  expect(unit['J15 Monterrey'].depth).toBe(1);
  // Nombre repetido y unidad superior inexistente.
  expect((await admin.post('/api/admin/units', { data: { name: 'j15 norte', parentId: null } })).status()).toBe(400);
  expect((await admin.post('/api/admin/units', { data: { name: 'J15 Otra', parentId: 999999 } })).status()).toBe(400);

  await applyPlan(admin, { description: 'Spec 015 · catálogos', operations: [
    { op: 'createEntity', group: GROUP, name: 'J15Caso', label: 'Caso 015', labelPlural: 'Casos 015', displayField: 'Titulo', fields: [
      { name: 'Titulo', label: 'Título', type: 'text', required: true },
      { name: 'Dueno', label: 'Dueño', type: 'user', defaultValue: 'CURRENTUSER()' },
      { name: 'Revisor', label: 'Revisor', type: 'user' },
      { name: 'Nota', label: 'Nota interna', type: 'text' },
    ] },
    { op: 'createEntity', group: GROUP, name: 'J15Nota', label: 'Nota 015', labelPlural: 'Notas 015', displayField: 'Texto', fields: [
      { name: 'Texto', label: 'Texto', type: 'text', required: true },
      { name: 'Caso', label: 'Caso', type: 'reference', referenceEntity: 'J15Caso' },
    ] },
    { op: 'setReports', entity: 'J15Caso', reports: [{ name: 'Conteo', label: 'Casos', measures: [{ fn: 'count', label: 'Casos' }] }] },
  ] });

  // H3: Consulta (lee) ← Agente (crea y edita) ← Gerente. Auditor solo audita (capacidad directa).
  const role = async (name: string, permissions: object[], inheritsRoleIds: number[] = [], canViewAudit = false) => {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 015', canManageModel: false, canManageUsers: false, canViewAudit, permissions, inheritsRoleIds } });
    expect(res.status(), await res.text()).toBe(200);
    return (await res.json()).id as number;
  };
  ids.consulta = await role('J15 Consulta', ['J15Caso', 'J15Nota'].map((entity) => ({ entity, canRead: true, canCreate: false, canUpdate: false, canDelete: false })));
  ids.agente = await role('J15 Agente', ['J15Caso', 'J15Nota'].map((entity) => ({ entity, canRead: false, canCreate: true, canUpdate: true, canDelete: false })), [ids.consulta]);
  ids.gerente = await role('J15 Gerente', [], [ids.agente]);
  ids.auditor = await role('J15 Auditor', [], [], true);

  await applyPlan(admin, { description: 'Spec 015 · seguridad', operations: [
    { op: 'setSecurity', entity: 'J15Caso', security: {
      owner: 'Dueno', teamOwners: true,
      fields: [{ field: 'Nota', read: ['J15 Consulta'] }],
      records: [{ roles: ['J15 Agente'], scope: 'own' }, { roles: ['J15 Gerente'], scope: 'unitTree' }],
    } },
  ] });

  const person = async (w: keyof typeof users, roles: number[], u: string | null) => {
    const res = await admin.post('/api/admin/users', { data: { username: users[w], displayName: `${w} J15`, password: PWD, roleIds: roles, unit: u } });
    expect(res.status(), await res.text()).toBe(201);
    ids[w] = (await res.json()).id;
  };
  await person('ana', [ids.agente], 'J15 Monterrey');
  await person('beto', [ids.agente], 'J15 Saltillo');
  await person('cris', [ids.agente], 'J15 Monterrey');
  await person('gina', [ids.gerente, ids.auditor], 'J15 Norte');
  await person('sur', [ids.gerente], 'J15 Sur');
  await person('admin2', [await roleId(admin, 'Administrador')], null);
});

test('H1 CA2: la unidad de un usuario es una de las dadas de alta', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/users', { data: { username: 'e2e-j15-x', displayName: 'X', password: PWD, roleIds: [], unit: 'J15 Inexistente' } });
  expect(res.status()).toBe(400);
  expect(JSON.stringify(await res.json())).toContain('La unidad no existe');
});

test('H3 CA1: un rol recibe permisos, roles y reglas de campo de los roles de los que hereda', async () => {
  // El Agente no tiene «leer» propio: lo hereda de Consulta. El Gerente hereda de ambos (dos niveles).
  const me = await (await (await as('ana')).get('/api/auth/me')).json();
  expect(me.roles).toEqual(expect.arrayContaining(['J15 Agente', 'J15 Consulta']));
  expect(me.entities.J15Caso).toMatchObject({ read: true, create: true, update: true });
  const gina = await (await (await as('gina')).get('/api/auth/me')).json();
  expect(gina.roles).toEqual(expect.arrayContaining(['J15 Gerente', 'J15 Agente', 'J15 Consulta', 'J15 Auditor']));
  expect(gina.canViewAudit).toBe(true);
  expect(gina.canManageUsers).toBe(false);

  rec.a = (await create('ana', { Titulo: 'Caso de Ana', Nota: 'visible por herencia' })).body;
  expect(rec.a.Dueno).toBe(ids.ana);
  // La regla de campo «Nota: read J15 Consulta» aplica al Agente por herencia.
  expect(rec.a.Nota).toBe('visible por herencia');
  rec.b = (await create('beto', { Titulo: 'Caso de Beto' })).body;
  rec.c = (await create('sur', { Titulo: 'Caso de Sur' })).body;
  expect(rec.c.id).toBeTruthy();
});

test('H3 CA2 · CA3: sin capacidades heredadas, sin ciclos', async () => {
  const admin = await as('admin');
  const roles = (await (await admin.get('/api/admin/roles')).json()) as { id: number; name: string; inheritsRoleIds: number[] }[];
  expect(roles.find((r) => r.id === ids.gerente)!.inheritsRoleIds).toEqual([ids.agente]);
  const put = (id: number, data: object) => admin.put(`/api/admin/roles/${id}`, { data: { description: 'E2E spec 015', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [], ...data } });

  const fromAdmin = await put(ids.gerente, { name: 'J15 Gerente', inheritsRoleIds: [ids.agente, await roleId(admin, 'Administrador')] });
  expect(fromAdmin.status()).toBe(400);
  expect(await fromAdmin.text()).toContain('capacidades administrativas');
  const fromAuditor = await put(ids.gerente, { name: 'J15 Gerente', inheritsRoleIds: [ids.auditor] });
  expect(fromAuditor.status()).toBe(400);
  // Consulta es heredada por Agente: no puede ganar capacidades.
  const flags = await put(ids.consulta, { name: 'J15 Consulta', canViewAudit: true,
    permissions: ['J15Caso', 'J15Nota'].map((entity) => ({ entity, canRead: true, canCreate: false, canUpdate: false, canDelete: false })) });
  expect(flags.status()).toBe(400);
  // Ciclo: Consulta heredaría de Gerente, que hereda de Agente, que hereda de Consulta.
  const cycle = await put(ids.consulta, { name: 'J15 Consulta', inheritsRoleIds: [ids.gerente],
    permissions: ['J15Caso', 'J15Nota'].map((entity) => ({ entity, canRead: true, canCreate: false, canUpdate: false, canDelete: false })) });
  expect(cycle.status()).toBe(400);
  expect(await cycle.text()).toContain('ciclo');
  const self = await put(ids.consulta, { name: 'J15 Consulta', inheritsRoleIds: [ids.consulta] });
  expect(self.status()).toBe(400);
});

test('H1 CA3: «mi unidad y las que cuelgan de ella» ve toda la rama, y solo esa', async () => {
  expect(await titles('gina')).toEqual(['Caso de Ana', 'Caso de Beto']);
  expect((await list('gina')).total).toBe(2);
  expect(await titles('sur')).toEqual(['Caso de Sur']);
  // Propios (el Agente) sigue igual: cada quien lo suyo.
  expect(await titles('ana')).toEqual(['Caso de Ana']);
  expect((await json(await (await as('gina')).get(`/api/data/J15Caso/${rec.c.id}`))).status).toBe(404);
});

test('H1 CA4: mover una unidad cambia lo que ven sus superiores en la siguiente petición', async () => {
  const admin = await as('admin');
  const moved = await admin.put(`/api/admin/units/${unit['J15 Saltillo'].id}`, { data: { parentId: unit['J15 Sur'].id } });
  expect(moved.status(), await moved.text()).toBe(200);
  expect(await titles('gina')).toEqual(['Caso de Ana']);
  expect(await titles('sur')).toEqual(['Caso de Beto', 'Caso de Sur']);
  // Sin ciclos: J15 Norte no puede quedar debajo de J15 Monterrey (que cuelga de ella).
  const cycle = await admin.put(`/api/admin/units/${unit['J15 Norte'].id}`, { data: { parentId: unit['J15 Monterrey'].id } });
  expect(cycle.status()).toBe(400);
  expect((await admin.put(`/api/admin/units/${unit['J15 Saltillo'].id}`, { data: { parentId: unit['J15 Norte'].id } })).status()).toBe(200);
  expect(await titles('gina')).toEqual(['Caso de Ana', 'Caso de Beto']);
  const tree = await units();
  expect(tree.find((u) => u.name === 'J15 Saltillo')).toMatchObject({ parentId: unit['J15 Norte'].id, depth: 1, users: 1 });
});

test('H2 CA1 · CA2: un equipo es dueño en el campo declarado, y solo ahí', async () => {
  const admin = await as('admin');
  const team = await admin.post('/api/admin/teams', { data: { name: 'J15 Cola Monterrey', unit: 'J15 Monterrey', memberIds: [ids.ana, ids.beto] } });
  expect(team.status(), await team.text()).toBe(201);
  ids.team = (await team.json()).id;
  // No es un usuario: no aparece en la lista de usuarios ni en el buscador normal.
  const people = (await (await admin.get('/api/admin/users')).json()) as { id: number }[];
  expect(people.map((u) => u.id)).not.toContain(ids.team);
  const lookup = async (teams: boolean) => (await (await as('ana')).get('/api/users/lookup', { params: { search: 'J15 Cola', ...(teams ? { teams: 'true' } : {}) } })).json();
  expect(await lookup(false)).toEqual([]);
  expect(await lookup(true)).toEqual([{ id: ids.team, label: 'J15 Cola Monterrey (equipo)' }]);
  const entity = await (await (await as('ana')).get('/api/model/entities/J15Caso')).json();
  expect(entity.fields.find((f: { name: string }) => f.name === 'Dueno').acceptsTeams).toBe(true);
  expect(entity.fields.find((f: { name: string }) => f.name === 'Revisor').acceptsTeams ?? null).toBeNull();

  rec.t = (await create('ana', { Titulo: 'Caso del equipo', Dueno: ids.team })).body;
  expect(rec.t.Dueno).toBe(ids.team);
  expect(rec.t.Dueno__label).toBe('J15 Cola Monterrey');
  // Un equipo en otro campo de usuario no existe.
  const revisor = await create('ana', { Titulo: 'Revisor equipo', Revisor: ids.team });
  expect(revisor.status).toBe(400);
  expect(JSON.stringify(revisor.body)).toContain('El usuario seleccionado no existe');
});

test('H2 CA3 · CA4: los miembros trabajan lo del equipo; quien no es miembro no lo ve ni puede asignárselo', async () => {
  expect(await titles('beto')).toEqual(['Caso de Beto', 'Caso del equipo']);
  const edited = await json(await (await as('beto')).put(`/api/data/J15Caso/${rec.t.id}`, { data: { ...rec.t, Titulo: 'Caso del equipo (editado)' } }));
  expect(edited.status, JSON.stringify(edited.body)).toBe(200);
  rec.t = edited.body;
  // Cris es de la misma unidad pero no del equipo: alcance «propios».
  expect(await titles('cris')).toEqual([]);
  expect((await json(await (await as('cris')).get(`/api/data/J15Caso/${rec.t.id}`))).status).toBe(404);
  expect((await create('cris', { Titulo: 'Para un equipo ajeno', Dueno: ids.team })).status).toBe(403);
  // El gerente de la región ve lo del equipo: el equipo es de J15 Monterrey.
  expect(await titles('gina')).toEqual(['Caso de Ana', 'Caso de Beto', 'Caso del equipo (editado)']);

  // Quitar a Beto del equipo aplica de inmediato, sin cerrar su sesión.
  const admin = await as('admin');
  expect((await admin.put(`/api/admin/teams/${ids.team}`, { data: { name: 'J15 Cola Monterrey', unit: 'J15 Monterrey', isActive: true, memberIds: [ids.ana] } })).status()).toBe(200);
  expect(await titles('beto')).toEqual(['Caso de Beto']);
  expect((await json(await (await as('beto')).get(`/api/data/J15Caso/${rec.t.id}`))).status).toBe(404);
  // Un equipo inactivo no da acceso a sus miembros.
  expect((await admin.put(`/api/admin/teams/${ids.team}`, { data: { name: 'J15 Cola Monterrey', unit: 'J15 Monterrey', isActive: false, memberIds: [ids.ana] } })).status()).toBe(200);
  expect(await titles('ana')).toEqual(['Caso de Ana']);
  expect((await admin.put(`/api/admin/teams/${ids.team}`, { data: { name: 'J15 Cola Monterrey', unit: 'J15 Monterrey', isActive: true, memberIds: [ids.ana, ids.beto] } })).status()).toBe(200);
  expect(await titles('ana')).toEqual(['Caso de Ana', 'Caso del equipo (editado)']);
});

test('H5 CA1: buscadores, reportes, bitácora y etiquetas usan la misma regla', async () => {
  const gina = await as('gina');
  // Buscador del catálogo.
  const lookup = (await (await gina.get('/api/data/J15Caso/lookup')).json()) as { label: string }[];
  expect(lookup.map((x) => x.label).sort()).toEqual(['Caso de Ana', 'Caso de Beto', 'Caso del equipo (editado)']);
  // Reporte.
  const report = (await (await gina.get('/api/reports/J15Caso/Conteo')).json()) as { totals: Record<string, number> };
  expect(Object.values(report.totals)[0]).toBe(3);
  // Bitácora: los eventos de la rama, no los de J15 Sur.
  const audit = (await (await gina.get('/api/audit', { params: { entity: 'J15Caso', pageSize: '100' } })).json()) as { items: { action: string; recordId: string }[] };
  // Spec 034 (R-032-6): solo eventos de datos. En `model.change` el recordId es el id del ChangeSet, no de un caso: tras
  // h5-h6-seguridad.spec.ts uno coincidía con el id del caso de Sur y la prueba fallaba sin fuga.
  const datos = audit.items.filter((x) => ['create', 'update', 'delete', 'automation.create', 'automation.update'].includes(x.action) || x.action.startsWith('workflow.'));
  const seen = new Set(datos.map((x) => Number(x.recordId)));
  expect(seen.has(rec.a.id) && seen.has(rec.b.id) && seen.has(rec.t.id)).toBe(true);
  expect(seen.has(rec.c.id)).toBe(false);
  // Etiqueta de una referencia a un caso fuera del alcance.
  const nota = (await create('sur', { Texto: 'Nota sobre el caso de Sur', Caso: rec.c.id }, 'J15Nota')).body;
  const viaAdmin = await json(await (await as('admin')).get(`/api/data/J15Nota/${nota.id}`));
  expect(viaAdmin.body.Caso__label).toBe('Caso de Sur');
  // gina lee la nota (J15Nota no restringe por registro) pero no el caso: «(sin acceso)».
  const viaGina = await json(await gina.get(`/api/data/J15Nota/${nota.id}`));
  expect(viaGina.body.Caso__label).toBe('(sin acceso)');
});

// ---------------- UI ----------------

async function pageAs(browser: Browser, user: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined, viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

test('H1 CA1 (UI): unidades en árbol, alta y movimiento', async ({ page }) => {
  await page.goto('/admin/security');
  await page.getByRole('tab', { name: 'Unidades' }).click();
  await expect(page.getByRole('row', { name: /J15 Monterrey/ })).toBeVisible();
  await page.getByRole('button', { name: 'Nueva unidad' }).click();
  let dialog = page.getByRole('dialog');
  await dialog.getByLabel(/^Nombre/).fill('J15 Torreón');
  await dialog.getByLabel('Unidad superior').selectOption('J15 Norte');
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Crear unidad' }).click();
  await expect(page.getByText('Unidad creada')).toBeVisible();
  await page.getByRole('button', { name: 'Mover J15 Torreón' }).click();
  dialog = page.getByRole('dialog');
  // No se ofrece moverla debajo de sí misma.
  await expect(dialog.getByLabel('Unidad superior').locator('option', { hasText: 'J15 Torreón' })).toHaveCount(0);
  await dialog.getByLabel('Unidad superior').selectOption('J15 Sur');
  await dialog.getByRole('button', { name: 'Mover' }).click();
  await expect(page.getByText('Unidad movida')).toBeVisible();
  expect((await units()).find((u) => u.name === 'J15 Torreón')?.parentId).toBe(unit['J15 Sur'].id);
  await expectAccessible(page);
});

test('H1 CA2 (UI): la unidad del usuario se elige de una lista con sangría', async ({ page }) => {
  await page.goto('/admin/security');
  await page.getByRole('row', { name: /e2e-j15-cris/ }).getByRole('button', { name: 'Editar' }).click();
  const select = page.getByRole('dialog').getByLabel('Unidad');
  await expect(select).toHaveValue('J15 Monterrey');
  await expect(select.locator('option', { hasText: 'J15 Saltillo' })).toHaveText(/^  J15 Saltillo$/);
  await page.getByRole('dialog').getByRole('button', { name: 'Cancelar' }).click();
});

test('H2 CA1 (UI): equipos con miembros y unidad', async ({ page }) => {
  await page.goto('/admin/security');
  await page.getByRole('tab', { name: 'Equipos' }).click();
  await expect(page.getByRole('row', { name: /J15 Cola Monterrey/ })).toContainText('ana J15');
  await page.getByRole('button', { name: 'Nuevo equipo' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/^Nombre/).fill('J15 Mesa de ayuda');
  await dialog.getByLabel('Unidad').selectOption('J15 Sur');
  await dialog.getByRole('checkbox', { name: /sur J15/ }).check();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Crear equipo' }).click();
  await expect(page.getByText('Equipo creado')).toBeVisible();
  await expect(page.getByRole('row', { name: /J15 Mesa de ayuda/ })).toContainText('sur J15');
});

test('H3 CA1 (UI): «Hereda de» en el rol, sin ofrecer roles con capacidades', async ({ page }) => {
  await page.goto('/admin/security');
  await page.getByRole('tab', { name: 'Roles y permisos' }).click();
  await expect(page.getByRole('row', { name: /J15 Gerente/ })).toContainText('Hereda de J15 Agente');
  await page.getByRole('row', { name: /J15 Gerente/ }).getByRole('button', { name: 'Editar' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('checkbox', { name: /^J15 Agente/ })).toBeChecked();
  await expect(dialog.getByRole('checkbox', { name: /^J15 Auditor/ })).toHaveCount(0);
  await expect(dialog.getByRole('checkbox', { name: /^Administrador/ })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Cancelar' }).click();
});

test('H2 CA2 (UI): el campo dueño busca personas y equipos', async ({ browser }) => {
  const page = await pageAs(browser, users.ana);
  await page.goto('/c/J15Caso/new');
  await page.getByLabel(/^Título/).fill('Desde la UI para el equipo');
  await page.getByLabel(/^Dueño/).fill('J15 Cola');
  const option = page.getByRole('listbox').getByRole('option').first();
  await expect(option).toHaveAccessibleName('J15 Cola Monterrey (equipo)');
  // Como en compras.spec.ts (F-20): la opción debe verse completa para una persona.
  expect(await option.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return hit === el || el.contains(hit);
  })).toBe(true);
  await option.click();
  await page.getByRole('button', { name: 'Crear', exact: true }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  expect(await titles('beto')).toContain('Desde la UI para el equipo');
  await page.context().close();
});

// ---------------- H4: instancia con aprobación de cambios de seguridad ----------------
const SEC = `http://localhost:${PORTS.seguridad015}`;
let sec: ChildProcess | undefined;
const secApi: Partial<Record<'admin' | 'admin2', APIRequestContext>> = {};

async function loginAt(base: string, username: string, password: string) {
  const anon = await request.newContext({ baseURL: base, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { 'X-ERP-Request': '1' } });
  const res = await anon.post('/api/auth/login', { data: { username, password } });
  await anon.dispose();
  if (res.status() !== 200) return null;
  const cookie = /erp\.auth=[^;]+/.exec(res.headers()['set-cookie'] ?? '')![0];
  return request.newContext({ baseURL: base, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { 'X-ERP-Request': '1', Cookie: cookie } });
}
const at = async (w: 'admin' | 'admin2') => (secApi[w] ??= (await loginAt(SEC, w === 'admin' ? ADMIN.username : users.admin2, w === 'admin' ? ADMIN.password : PWD))!);
const pending = async (w: 'admin' | 'admin2'): Promise<Change[]> => (await (await (await at(w)).get('/api/admin/security-changes')).json()).items;
const userById = async (id: number) => ((await (await (await at('admin')).get('/api/admin/users')).json()) as { id: number; roleIds: number[]; unit: string | null }[]).find((u) => u.id === id)!;
const approve = async (w: 'admin' | 'admin2', c: Change, hash = c.hash) => (await at(w)).post(`/api/admin/security-changes/${c.id}/approve`, { data: { hash, comment: 'E2E 015' } });

test.describe('H4 · aprobación de cambios de seguridad', () => {
  test.beforeAll(async () => {
    const root = path.resolve(import.meta.dirname, '..', '..');
    const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';
    sec = spawn('dotnet', ['run', '--no-build', '--no-launch-profile', '--project', path.join(root, 'src', 'Erp.Api'), '--urls', SEC], {
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        ConnectionStrings__Erp: `Server=${server};Database=${DB};Integrated Security=true;Encrypt=true;TrustServerCertificate=true`,
        Database__AutoCreate: 'false',
        Security__LoginAttemptsPerMinute: '1000',
        Security__RequestsPerMinute: '5000',
        Environments__Name: 'Producción E2E (seguridad con aprobación)',
        Environments__RequireApproval: 'true',
        Environments__RequireSecurityApproval: 'true',
        // AllowDirectChanges es del modelo: no debe reabrir los cambios de seguridad.
        Environments__AllowDirectChanges: 'true',
        Environments__SigningKey: process.env.E2E_SIGNING_KEY ?? '',
        Spa__Path: path.join(root, 'web', 'dist'),
      },
      stdio: 'ignore',
    });
    for (let i = 0; i < 120; i++) {
      try { if ((await fetch(`${SEC}/api/health`)).ok) return; } catch { /* aún no arranca */ }
      await new Promise((r) => setTimeout(r, 500));
    }
    throw new Error(`La instancia con aprobación de seguridad no arrancó en ${SEC}`);
  });
  test.afterAll(() => { sec?.kill(); });

  test('H4 CA1 · CA3 · CA4: agregar un rol espera; quien lo pidió no aprueba; otro aprueba con la huella', async () => {
    const admin = await at('admin');
    expect((await (await admin.get('/api/admin/security-changes')).json()).required).toBe(true);
    const cris = await userById(ids.cris);
    const res = await admin.put(`/api/admin/users/${ids.cris}`, { data: { displayName: 'cris J15', isActive: true, roleIds: [...cris.roleIds, ids.auditor], unit: cris.unit } });
    expect(res.status(), await res.text()).toBe(202);
    const change = (await res.json()).securityChange as Change;
    expect(change).toMatchObject({ kind: 'user.update', status: 'pending', canApprove: false, canReject: true });
    expect((await userById(ids.cris)).roleIds).toEqual([ids.agente]);

    expect((await approve('admin', change)).status()).toBe(403);
    const seen = (await pending('admin2')).find((c) => c.id === change.id)!;
    expect(seen.canApprove).toBe(true);
    expect((await approve('admin2', seen, '0'.repeat(64))).status()).toBe(409);
    const ok = await approve('admin2', seen);
    expect(ok.status(), await ok.text()).toBe(200);
    expect((await ok.json()).status).toBe('applied');
    expect((await userById(ids.cris)).roleIds.sort()).toEqual([ids.agente, ids.auditor].sort());
    // No se aprueba dos veces.
    expect((await approve('admin2', seen)).status()).toBe(409);

    const audit = (await (await admin.get('/api/audit', { params: { pageSize: '50' } })).json()) as { items: { action: string; recordId: string; user: string }[] };
    expect(audit.items.find((a) => a.action === 'security.request' && a.recordId === String(change.id))?.user).toBe(ADMIN.username);
    expect(audit.items.find((a) => a.action === 'security.approve' && a.recordId === String(change.id))?.user).toBe(users.admin2);
    expect(audit.items.some((a) => a.action === 'security.user' && a.recordId === String(ids.cris) && a.user === users.admin2)).toBe(true);
  });

  test('H4 CA2: reducir accesos se aplica de inmediato', async () => {
    const admin = await at('admin');
    const res = await admin.put(`/api/admin/users/${ids.cris}`, { data: { displayName: 'cris J15', isActive: true, roleIds: [ids.agente], unit: 'J15 Monterrey' } });
    expect(res.status(), await res.text()).toBe(200);
    expect((await userById(ids.cris)).roleIds).toEqual([ids.agente]);
    // Quitar un miembro, llevar una unidad a la raíz, describir un rol: inmediato.
    expect((await admin.put(`/api/admin/teams/${ids.team}`, { data: { name: 'J15 Cola Monterrey', unit: 'J15 Monterrey', isActive: true, memberIds: [ids.ana] } })).status()).toBe(200);
    const torreon = (await units()).find((u) => u.name === 'J15 Torreón')!;
    expect((await admin.put(`/api/admin/units/${torreon.id}`, { data: { parentId: null } })).status()).toBe(200);
    expect((await admin.post('/api/admin/units', { data: { name: 'J15 Hoja', parentId: unit['J15 Sur'].id } })).status()).toBe(201);
  });

  test('H4 CA1: roles, equipos, unidades y altas de usuario también esperan', async () => {
    const admin = await at('admin');
    const roles = (await (await admin.get('/api/admin/roles')).json()) as { id: number; name: string; permissions: object[]; inheritsRoleIds: number[] }[];
    const consulta = roles.find((r) => r.id === ids.consulta)!;
    const base = { name: consulta.name, description: 'E2E spec 015', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: consulta.permissions, inheritsRoleIds: [] };
    expect((await admin.put(`/api/admin/roles/${ids.consulta}`, { data: { ...base, description: 'Solo descripción' } })).status()).toBe(200);
    const renamed = await admin.put(`/api/admin/roles/${ids.consulta}`, { data: { ...base, name: 'J15 Consulta ampliada' } });
    expect(renamed.status()).toBe(202);
    const perm = await admin.put(`/api/admin/roles/${ids.consulta}`, { data: { ...base, permissions: [{ entity: 'J15Caso', canRead: true, canCreate: false, canUpdate: false, canDelete: false }, { entity: 'J15Nota', canRead: true, canCreate: true, canUpdate: false, canDelete: false }] } });
    expect(perm.status()).toBe(202);
    expect((await admin.put(`/api/admin/teams/${ids.team}`, { data: { name: 'J15 Cola Monterrey', unit: 'J15 Monterrey', isActive: true, memberIds: [ids.ana, ids.beto] } })).status()).toBe(202);
    expect((await admin.put(`/api/admin/units/${unit['J15 Sur'].id}`, { data: { parentId: unit['J15 Norte'].id } })).status()).toBe(202);
    const created = await admin.post('/api/admin/users', { data: { username: 'e2e-j15-nuevo', displayName: 'Nuevo J15', password: PWD, roleIds: [], unit: null } });
    expect(created.status()).toBe(202);
    expect(JSON.stringify(await created.json())).not.toContain(PWD);
    // Nada se aplicó: la persona no existe y no inicia sesión.
    expect(await loginAt(SEC, 'e2e-j15-nuevo', PWD)).toBeNull();
    expect((await units()).find((u) => u.name === 'J15 Sur')!.parentId).toBeNull();
    const list = await pending('admin2');
    const signup = list.find((c) => c.kind === 'user.create')!;
    expect(signup.summary).toBe('Alta del usuario e2e-j15-nuevo');
    expect((await (await at('admin2')).get('/api/admin/security-changes')).ok()).toBe(true);
    // La contraseña viaja solo como hash; quien revisa ve que se cambia.
    const raw = JSON.stringify(list);
    expect(raw).not.toContain(PWD);
    expect(raw).not.toContain('passwordHash');
    expect((await approve('admin2', signup)).status()).toBe(200);
    expect(await loginAt(SEC, 'e2e-j15-nuevo', PWD)).not.toBeNull();
    // Rechazar (quien solicitó, para cancelar) lo demás.
    for (const c of await pending('admin'))
      if (c.summary.includes('J15')) expect((await (await at('admin')).post(`/api/admin/security-changes/${c.id}/reject`, { data: { comment: 'E2E' } })).status()).toBe(200);
    expect((await pending('admin')).filter((c) => c.summary.includes('J15'))).toEqual([]);
  });

  test('H4 CA3: si el destino cambió desde la solicitud, la aprobación se rechaza', async () => {
    const admin = await at('admin');
    const beto = await userById(ids.beto);
    const req = await admin.put(`/api/admin/users/${ids.beto}`, { data: { displayName: 'beto J15', isActive: true, roleIds: [...beto.roleIds, ids.auditor], unit: beto.unit } });
    expect(req.status()).toBe(202);
    const change = (await req.json()).securityChange as Change;
    // Mientras tanto se le quita el rol que tenía (reducir: inmediato).
    expect((await admin.put(`/api/admin/users/${ids.beto}`, { data: { displayName: 'beto J15', isActive: true, roleIds: [], unit: beto.unit } })).status()).toBe(200);
    const stale = await approve('admin2', (await pending('admin2')).find((c) => c.id === change.id)!);
    expect(stale.status()).toBe(409);
    expect(await stale.text()).toContain('cambió desde la solicitud');
    // Sigue pendiente (no se aplicó nada) y se puede rechazar.
    expect((await pending('admin2')).find((c) => c.id === change.id)?.status).toBe('pending');
    expect((await (await at('admin2')).post(`/api/admin/security-changes/${change.id}/reject`, { data: {} })).status()).toBe(200);
    expect((await userById(ids.beto)).roleIds).toEqual([]);
    expect((await admin.put(`/api/admin/users/${ids.beto}`, { data: { displayName: 'beto J15', isActive: true, roleIds: [ids.agente], unit: beto.unit } })).status()).toBe(202);
  });

  test('H4 CA5: la clave de API solicitada se muestra a quien la pidió y funciona solo al aprobarse', async () => {
    const admin = await at('admin');
    const res = await admin.post('/api/admin/api-keys', { data: { name: 'J15 integración', roleIds: [ids.consulta] } });
    expect(res.status(), await res.text()).toBe(202);
    const { secret, securityChange } = (await res.json()) as { secret: string; securityChange: Change };
    expect(secret).toMatch(/^erp_[0-9a-f]{12}_/);
    const bearer = () => request.newContext({ baseURL: SEC, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { Authorization: `Bearer ${secret}` } });
    let svc = await bearer();
    expect((await svc.get('/api/auth/me')).status()).toBe(401);
    await svc.dispose();
    // Las claves las aprueba otro superusuario; quien administra usuarios sin serlo no las ve.
    const seen = (await pending('admin2')).find((c) => c.id === securityChange.id)!;
    expect(JSON.stringify(seen)).not.toContain('secretHash');
    expect((await approve('admin2', seen)).status()).toBe(200);
    svc = await bearer();
    const me = await svc.get('/api/auth/me');
    expect(me.status()).toBe(200);
    expect((await me.json()).roles).toEqual(['J15 Consulta']);
    await svc.dispose();
    // Rotar no amplía privilegios (y es la respuesta a una filtración): inmediato.
    const key = ((await (await admin.get('/api/admin/api-keys')).json()) as { id: number; name: string }[]).find((k) => k.name === 'J15 integración')!;
    expect((await admin.post(`/api/admin/api-keys/${key.id}/rotate`, { data: { graceHours: 0 } })).status()).toBe(200);
    expect((await admin.put(`/api/admin/api-keys/${key.id}`, { data: { name: 'J15 integración', roleIds: [ids.consulta, ids.agente] } })).status()).toBe(202);
    expect((await admin.post(`/api/admin/api-keys/${key.id}/revoke`)).status()).toBe(204);
  });

  test('Spec 018 H1 CA2: el cliente OAuth solicitado emite tokens solo al aprobarse; ganar alcances espera, quitarlos no', async () => {
    const admin = await at('admin');
    const res = await admin.post('/api/admin/oauth-clients', { data: { name: 'J15 BI', roleIds: [ids.consulta], scopes: ['odata.read'], tokenMinutes: 5 } });
    expect(res.status(), await res.text()).toBe(202);
    const { secret, securityChange } = (await res.json()) as { secret: string; securityChange: Change & { after: { clientId: string } } };
    const clientId = securityChange.after.clientId;
    expect(clientId).toMatch(/^erpcl_[0-9a-f]{14}$/);
    const anon = await request.newContext({ baseURL: SEC, storageState: { cookies: [], origins: [] } });
    const token = (scope?: string) => anon.post('/oauth/token', { form: { grant_type: 'client_credentials', client_id: clientId, client_secret: secret, ...(scope ? { scope } : {}) } });
    expect((await token()).status()).toBe(401); // no existe hasta que se aprueba
    const seen = (await pending('admin2')).find((c) => c.id === securityChange.id)!;
    expect(seen.kind).toBe('oauthclient.create');
    expect(JSON.stringify(seen)).not.toContain('secretHash');
    expect((await approve('admin', seen)).status()).toBe(403); // quien lo pidió no aprueba
    expect((await approve('admin2', seen)).status()).toBe(200);
    expect((await token()).status()).toBe(200);

    const client = ((await (await admin.get('/api/admin/oauth-clients')).json()) as { id: number; name: string }[]).find((c) => c.name === 'J15 BI')!;
    const save = (scopes: string[]) => admin.put(`/api/admin/oauth-clients/${client.id}`, { data: { name: 'J15 BI', roleIds: [ids.consulta], scopes, tokenMinutes: 5 } });
    expect((await save(['odata.read', 'data.write'])).status()).toBe(202);
    expect((await token('data.write')).status()).toBe(400); // aún no lo tiene
    expect((await save([])).status()).toBe(400);
    expect((await admin.post(`/api/admin/oauth-clients/${client.id}/revoke`)).status()).toBe(204);
    expect((await token()).status()).toBe(401);
    // La solicitud pendiente sobre un cliente revocado ya no se aplica.
    const widen = (await pending('admin2')).find((c) => c.kind === 'oauthclient.update')!;
    expect((await approve('admin2', widen)).status()).toBe(409);
    expect((await (await at('admin2')).post(`/api/admin/security-changes/${widen.id}/reject`, { data: {} })).status()).toBe(200);
    await anon.dispose();
  });

  test('H4 CA3 (UI): otro administrador revisa el antes y el después y aprueba desde Solicitudes', async ({ browser }) => {
    const ctx = await browser.newContext({ storageState: undefined, baseURL: SEC, viewport: { width: 1280, height: 800 } });
    const page = await ctx.newPage();
    // El primero solicita desde la UI: el mensaje dice que espera aprobación.
    await page.goto('/login');
    await page.getByLabel('Usuario').fill(ADMIN.username);
    await page.getByLabel('Contraseña', { exact: true }).fill(ADMIN.password);
    await page.getByRole('button', { name: 'Entrar' }).click();
    await expect(page.getByLabel('Navegación principal')).toBeVisible();
    await page.goto('/admin/security');
    await page.getByRole('row', { name: /e2e-j15-cris/ }).getByRole('button', { name: 'Editar' }).click();
    await page.getByRole('dialog').getByRole('checkbox', { name: /^J15 Auditor/ }).check();
    await page.getByRole('dialog').getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText(/Solicitud enviada: otro administrador debe aprobarla/)).toBeVisible();
    await ctx.clearCookies();

    await page.goto('/login');
    await page.getByLabel('Usuario').fill(users.admin2);
    await page.getByLabel('Contraseña', { exact: true }).fill(PWD);
    await page.getByRole('button', { name: 'Entrar' }).click();
    await expect(page.getByLabel('Navegación principal')).toBeVisible();
    await page.goto('/admin/security');
    await page.getByRole('tab', { name: 'Solicitudes' }).click();
    await expect(page.getByText('Este entorno exige aprobación para ampliar accesos')).toBeVisible();
    const card = page.getByRole('region', { name: 'Cambios al usuario e2e-j15-cris' });
    await expect(card.getByRole('row', { name: /Roles/ })).toContainText('J15 Agente, J15 Auditor');
    // Regresión (revisión exploratoria 015, F-02): sin empresas en la carga es «sin cambio», no «quitarlas»; tampoco se
    // listaba «Usuario — → —» en una edición.
    const beto = page.getByRole('region', { name: 'Cambios al usuario e2e-j15-beto' });
    await expect(beto.getByRole('row', { name: /Roles/ })).toContainText('J15 Agente');
    await expect(beto.getByRole('row', { name: /Empresas/ })).toHaveCount(0);
    await expect(beto.getByRole('row', { name: /^Usuario/ })).toHaveCount(0);
    await expectAccessible(page);
    await card.getByRole('button', { name: 'Aprobar' }).click();
    await page.getByRole('dialog').getByLabel('Comentario').fill('Revisado por la UI');
    await page.getByRole('dialog').getByRole('button', { name: 'Aprobar y aplicar' }).click();
    await expect(page.getByText('Solicitud aprobada y aplicada')).toBeVisible();
    await expect(card).toHaveCount(0);
    expect((await userById(ids.cris)).roleIds.sort()).toEqual([ids.agente, ids.auditor].sort());
    await ctx.close();
  });
});
