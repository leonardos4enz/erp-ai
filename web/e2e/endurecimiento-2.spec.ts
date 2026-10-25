// Spec 021 · Endurecimiento 2. Cada prueba es un hallazgo de la auditoría de solo lectura (2026-10-24) y se verificó
// fallando con el código anterior. Los catálogos de apoyo son del grupo «Pruebas 021» y se archivan al terminar.
import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { request, type APIRequestContext } from '@playwright/test';
import { ADMIN, DB, PORTS, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

// Al verificar contra el código anterior (E2E_INDEPENDENT=1) una falla no salta las pruebas siguientes: cada una recupera
// los ids por nombre (el proceso de Playwright se reinicia tras una falla y pierde el estado del módulo).
test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 021';
const PWD = 'frase larga de endurecimiento 021';
const users = { ua: 'e2e-e21-usuarios', ub: 'e2e-e21-usuarios2', su: 'e2e-e21-super', basico: 'e2e-e21-basico', aprobador: 'e2e-e21-aprobador' } as const;
type Who = keyof typeof users | 'admin';
type Row = Record<string, unknown> & { id: number; _rowVersion: string };
type Change = { id: number; kind: string; status: string; hash: string; canApprove: boolean; canReject: boolean };
type Run = { id: number; status: string; attempts: number; lastError?: string | null; recordId?: number };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
const ids: Record<string, number> = {};

/** SQL directo sobre la base desechable de la suite (nunca la de desarrollo). */
function sql(query: string) {
  const db = DB;
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  return execFileSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-h', '-1', '-W', '-Q',
    `SET NOCOUNT ON; ${query}`]).toString().trim();
}

const userById = async (id: number) =>
  ((await (await (await as('admin')).get('/api/admin/users')).json()) as { id: number; roleIds: number[]; displayName: string }[]).find((u) => u.id === id)!;
const putUser = async (w: Who, id: number, roleIds: number[], base: APIRequestContext | null = null) =>
  (base ?? await as(w)).put(`/api/admin/users/${id}`, { data: { displayName: `Usuario ${id}`, isActive: true, roleIds } });
const entity = async (name: string) => (await (await (await as('admin')).get(`/api/model/entities/${name}`)).json());
const runs = async (name: string): Promise<Run[]> =>
  (await (await (await as('admin')).get('/api/admin/automations/runs', { params: { name } })).json()) as Run[];
async function waitFor<T>(fn: () => Promise<T>, ok: (v: T) => boolean, what: string): Promise<T> {
  for (let i = 0; i < 60; i++) {
    const v = await fn();
    if (ok(v)) return v;
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`No se cumplió: ${what}`);
}

test.beforeEach(async () => {
  if (ids.usuarios) return;
  const admin = await as('admin');
  const roles = (await (await admin.get('/api/admin/roles')).json()) as { id: number; name: string }[];
  const byName = (n: string) => roles.find((r) => r.name === n)?.id;
  for (const [key, name] of [['usuarios', 'E21 Usuarios'], ['modelador', 'E21 Modelador'], ['auditor', 'E21 Auditor'], ['basicoRol', 'E21 Básico'], ['superRol', 'Administrador']])
    if (byName(name)) ids[key] = byName(name)!;
  const list = (await (await admin.get('/api/admin/users')).json()) as { id: number; username: string }[];
  for (const [key, username] of Object.entries(users)) { const u = list.find((x) => x.username === username); if (u) ids[key] = u.id; }
});

test.afterAll(async () => {
  if (process.env.E2E_INDEPENDENT) return; // cada falla reinicia el proceso y correría la limpieza a mitad de la suite
  try { sql("IF OBJECT_ID('meta.CK_E2E21_Falla') IS NOT NULL ALTER TABLE meta.AutomationRun DROP CONSTRAINT CK_E2E21_Falla"); } catch { /* sin restricción */ }
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await admin.get('/api/model/entities', { params: { includeChildren: 'true' } })).json()) as { name: string }[];
  const operations = ['E21Doc', 'E21Proceso', 'E21Contador', 'E21Fallo', 'E21Tarea', 'E21Rev']
    .filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(admin, { description: 'E2E 021: limpieza', operations });
  await admin.dispose();
});

test('preparar: roles con y sin capacidades, administradores de usuarios, un segundo superusuario', async () => {
  const admin = await as('admin');
  const role = async (name: string, caps: { canManageModel?: boolean; canManageUsers?: boolean; canViewAudit?: boolean }) => {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 021', canManageModel: false, canManageUsers: false,
      canViewAudit: false, ...caps, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    return (await res.json()).id as number;
  };
  ids.usuarios = await role('E21 Usuarios', { canManageUsers: true });
  ids.modelador = await role('E21 Modelador', { canManageModel: true });
  ids.auditor = await role('E21 Auditor', { canViewAudit: true });
  ids.basicoRol = await role('E21 Básico', {});
  ids.superRol = await roleId(admin, 'Administrador');
  for (const [key, username] of Object.entries(users)) {
    const roleIds = key === 'su' || key === 'aprobador' ? [ids.superRol] : key === 'basico' ? [ids.basicoRol] : [ids.usuarios];
    const res = await admin.post('/api/admin/users', { data: { username, displayName: `E21 ${key}`, password: PWD, roleIds } });
    expect(res.status(), await res.text()).toBe(201);
    ids[key] = (await res.json()).id;
  }
});

// ---------------- H1: nadie otorga lo que no tiene ----------------

test('H1 CA1: quien solo administra usuarios no da ni quita superusuario (tampoco a sí mismo)', async () => {
  const ua = await as('ua');
  const self = await putUser('ua', ids.ua, [ids.usuarios, ids.superRol]);
  expect(self.status(), await self.text()).toBe(403);
  expect(await self.text()).toContain('Solo un administrador del sistema');
  expect((await userById(ids.ua)).roleIds).toEqual([ids.usuarios]);

  const alta = await ua.post('/api/admin/users', { data: { username: 'e2e-e21-nuevo-su', displayName: 'No debe existir', password: PWD, roleIds: [ids.superRol] } });
  expect(alta.status()).toBe(403);
  const quitar = await putUser('ua', ids.su, [ids.basicoRol]);
  expect(quitar.status()).toBe(403);
  expect((await userById(ids.su)).roleIds).toEqual([ids.superRol]);
  // Lo que sí tiene lo reparte: un rol sin capacidades administrativas, o el suyo.
  expect((await putUser('ua', ids.basico, [ids.basicoRol, ids.usuarios])).status()).toBe(200);
  expect((await putUser('ua', ids.basico, [ids.basicoRol])).status()).toBe(200);
});

test('H1 CA2: tampoco reparte modelador ni auditoría si no los tiene; el superusuario sí', async () => {
  for (const role of [ids.modelador, ids.auditor]) {
    const res = await putUser('ua', ids.basico, [ids.basicoRol, role]);
    expect(res.status(), await res.text()).toBe(403);
  }
  expect((await userById(ids.basico)).roleIds).toEqual([ids.basicoRol]);
  expect((await putUser('admin', ids.basico, [ids.basicoRol, ids.modelador])).status()).toBe(200);
  // Quitarlo también es repartir.
  expect((await putUser('ua', ids.basico, [ids.basicoRol])).status()).toBe(403);
  expect((await putUser('admin', ids.basico, [ids.basicoRol])).status()).toBe(200);
});

test('H1 CA3: un rol no gana capacidades que quien lo edita no tiene; el rol superusuario solo lo edita un superusuario', async () => {
  const ua = await as('ua');
  const body = (caps: object) => ({ name: 'E21 Básico', description: 'E2E spec 021', canManageModel: false, canManageUsers: false, canViewAudit: false, ...caps, permissions: [] });
  expect((await ua.put(`/api/admin/roles/${ids.basicoRol}`, { data: body({ canManageModel: true }) })).status()).toBe(403);
  expect((await ua.post('/api/admin/roles', { data: { ...body({ canViewAudit: true }), name: 'E21 Auditor bis' } })).status()).toBe(403);
  expect((await ua.put(`/api/admin/roles/${ids.modelador}`, { data: { ...body({}), name: 'E21 Modelador' } })).status()).toBe(403);
  const sup = await ua.put(`/api/admin/roles/${ids.superRol}`, { data: { ...body({}), name: 'Administrador', description: 'cambiado por E21' } });
  expect(sup.status()).toBe(403);
  // Lo que sí tiene: describir un rol sin capacidades.
  expect((await ua.put(`/api/admin/roles/${ids.basicoRol}`, { data: { ...body({}), description: 'Descrito por E21' } })).status()).toBe(200);
});

test('H1 CA1 (UI): el administrador de usuarios ve por qué no puede dar superusuario', async ({ browser }) => {
  const page = await (await browser.newContext({ storageState: undefined })).newPage();
  await uiLogin(page, users.ua, PWD);
  await page.goto('/admin/security');
  await page.getByRole('row', { name: new RegExp(users.basico) }).getByRole('button', { name: 'Editar' }).click();
  const dialog = page.getByRole('dialog', { name: /^Editar / });
  await dialog.locator('label.checkbox').filter({ hasText: /^Administrador ·/ }).click();
  await dialog.getByRole('button', { name: 'Guardar' }).click();
  // Regresión (revisión exploratoria 021, F-01): el aviso quedaba arriba del diálogo, fuera de la vista tras guardar desde el pie.
  const alert = dialog.getByText('Solo un administrador del sistema puede otorgar o quitar el rol «Administrador».');
  await expect(alert).toBeInViewport();
  await expectAccessible(page);
  expect((await userById(ids.basico)).roleIds).toEqual([ids.basicoRol]);
  await page.context().close();
});

// ---------------- H1 CA4–CA5 y H8 CA1: instancia con aprobación de seguridad ----------------
const SEC = `http://localhost:${PORTS.seguridad021}`;
let sec: ChildProcess | undefined;
const secApi: Partial<Record<Who, APIRequestContext>> = {};
async function loginAt(base: string, username: string, password: string) {
  const anon = await request.newContext({ baseURL: base, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { 'X-ERP-Request': '1' } });
  const res = await anon.post('/api/auth/login', { data: { username, password } });
  expect(res.status(), `login de ${username} en ${base}`).toBe(200);
  await anon.dispose();
  const cookie = /erp\.auth=[^;]+/.exec(res.headers()['set-cookie'] ?? '')![0];
  return request.newContext({ baseURL: base, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { 'X-ERP-Request': '1', Cookie: cookie } });
}
const at = async (w: Who) => (secApi[w] ??= await loginAt(SEC, w === 'admin' ? ADMIN.username : users[w], w === 'admin' ? ADMIN.password : PWD));
const changes = async (w: Who): Promise<Change[]> => (await (await (await at(w)).get('/api/admin/security-changes')).json()).items;
const approve = async (w: Who, c: Change) => (await at(w)).post(`/api/admin/security-changes/${c.id}/approve`, { data: { hash: c.hash, comment: 'E2E 021' } });

test.describe('H1 CA4 · CA5 · H8 CA1: aprobación de seguridad', () => {
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
        Environments__Name: 'Producción E2E 021',
        Environments__RequireApproval: 'true',
        Environments__RequireSecurityApproval: 'true',
        Environments__AllowDirectChanges: 'true',
        Environments__SigningKey: process.env.E2E_SIGNING_KEY ?? '',
        Automations__PollSeconds: '300',
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

  test('H1 CA4: un cambio que otorga superusuario solo lo aprueba un superusuario; la solicitud sigue pendiente', async () => {
    const res = await putUser('admin', ids.basico, [ids.basicoRol, ids.superRol], await at('admin'));
    expect(res.status(), await res.text()).toBe(202);
    const change = (await res.json()).securityChange as Change;
    const seen = (await changes('ub')).find((c) => c.id === change.id)!;
    expect(seen.canApprove).toBe(false);
    const denied = await approve('ub', seen);
    expect(denied.status(), await denied.text()).toBe(403);
    expect((await changes('admin')).find((c) => c.id === change.id)?.status).toBe('pending');
    expect((await userById(ids.basico)).roleIds).toEqual([ids.basicoRol]);
    // Otro superusuario sí la aprueba (y luego se deshace: quitar no espera).
    const bySu = (await changes('su')).find((c) => c.id === change.id)!;
    expect(bySu.canApprove).toBe(true);
    expect((await approve('su', bySu)).status()).toBe(200);
    expect((await putUser('admin', ids.basico, [ids.basicoRol], await at('admin'))).status()).toBe(200);
  });

  test('H1 CA5: el beneficiario no aprueba su propio cambio', async () => {
    const res = await putUser('admin', ids.ub, [ids.usuarios, ids.basicoRol], await at('admin'));
    expect(res.status(), await res.text()).toBe(202);
    const change = (await res.json()).securityChange as Change;
    const mine = (await changes('ub')).find((c) => c.id === change.id)!;
    expect(mine.canApprove).toBe(false);
    expect((await approve('ub', mine)).status()).toBe(403);
    const other = (await changes('ua')).find((c) => c.id === change.id)!;
    expect(other.canApprove).toBe(true);
    expect((await approve('ua', other)).status()).toBe(200);
    expect((await userById(ids.ub)).roleIds.sort()).toEqual([ids.usuarios, ids.basicoRol].sort());
  });

  test('H8 CA1: una solicitud atorada en «applying» se puede volver a aprobar después de 10 minutos', async () => {
    const res = await putUser('admin', ids.basico, [ids.basicoRol, ids.usuarios], await at('admin'));
    expect(res.status(), await res.text()).toBe(202);
    const change = (await res.json()).securityChange as Change;
    // El proceso «cayó» a mitad de la aprobación hace 20 minutos.
    sql(`UPDATE meta.SecurityChange SET Status = 'applying' WHERE Id = ${change.id};
         IF COL_LENGTH('meta.SecurityChange', 'ClaimedAt') IS NOT NULL
           EXEC('UPDATE meta.SecurityChange SET ClaimedAt = DATEADD(minute, -20, SYSUTCDATETIME()) WHERE Id = ${change.id}')`);
    const seen = (await changes('ua')).find((c) => c.id === change.id)!;
    expect(seen.status).toBe('pending');
    const ok = await approve('ua', seen);
    expect(ok.status(), await ok.text()).toBe(200);
    expect((await userById(ids.basico)).roleIds.sort()).toEqual([ids.basicoRol, ids.usuarios].sort());
    expect((await putUser('admin', ids.basico, [ids.basicoRol], await at('admin'))).status()).toBe(200);
  });
});

// ---------------- H2: cambios de modelo sin pisarse ----------------

test('H2 CA1 · CA2: la vista previa da la huella; aplicar la exige y rechaza si el catálogo cambió', async () => {
  const admin = await as('admin');
  await applyPlan(admin, { description: 'E2E 021: documento', operations: [
    { op: 'createEntity', group: GROUP, name: 'E21Doc', label: 'Documento 021', labelPlural: 'Documentos 021', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true }, { name: 'Notas', label: 'Notas', type: 'text' }] },
  ] });
  const v1 = (await entity('E21Doc')).version as string;
  expect(v1).toMatch(/^[0-9a-f]{64}$/);
  const mine = { description: 'Mi layout', baseVersions: { E21Doc: v1 }, operations: [
    { op: 'setLayout', entity: 'E21Doc', layout: { sections: [{ name: 'Mia', label: 'Mía', fields: ['Clave', 'Notas'] }] } }] };
  const preview = await (await admin.post('/api/model/plan/preview', { data: mine })).json();
  expect(preview.valid).toBe(true);
  expect(preview.baseHash).toMatch(/^[0-9a-f]{64}$/);
  // Sin huella no se aplica.
  const noHash = await admin.post('/api/model/plan/apply', { data: mine });
  expect(noHash.status(), await noHash.text()).toBe(400);

  // Otra persona aplica su layout antes.
  await applyPlan(admin, { description: 'Layout ajeno', operations: [
    { op: 'setLayout', entity: 'E21Doc', layout: { sections: [{ name: 'Ajena', label: 'Ajena', fields: ['Notas', 'Clave'] }] } }] });
  const late = await admin.post('/api/model/plan/apply', { data: { ...mine, baseHash: preview.baseHash } });
  expect(late.status(), await late.text()).toBe(409);
  expect(await late.text()).toContain('cambió');
  expect((await entity('E21Doc')).layout.sections[0].label).toBe('Ajena');
  // La versión con la que se abrió también se comprueba en la vista previa.
  const stale = await (await admin.post('/api/model/plan/preview', { data: mine })).json();
  expect(stale.valid).toBe(false);
  expect(stale.errors.map((e: { message: string }) => e.message).join(' ')).toContain('cambió desde que lo abriste');
});

test('H2 CA1 (UI): el editor avisa si otra persona cambió el catálogo mientras lo editaba', async ({ page }) => {
  await page.goto('/admin/model?edit=E21Doc');
  await expect(page.getByLabel('Descripción')).toBeVisible();
  await applyPlan(await as('admin'), { description: 'Cambio ajeno', operations: [{ op: 'updateEntity', entity: 'E21Doc', description: 'Cambiada por otra persona' }] });
  await page.getByLabel('Descripción').fill('Mi descripción');
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog', { name: 'Revisar cambios al modelo' });
  await expect(dialog).toContainText('cambió desde que lo abriste');
  await expect(dialog.getByRole('button', { name: 'Aplicar cambios' })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Volver a editar' }).click();
  expect((await entity('E21Doc')).description).toBe('Cambiada por otra persona');
});

test('H8 CA4: cancelar con cambios sin aplicar pide confirmación; recargar avisa', async ({ page }) => {
  await page.goto('/admin/model?edit=E21Doc');
  await page.getByLabel('Descripción').fill('Borrador sin aplicar');
  // El navegador pregunta antes de cerrar o recargar si el evento queda cancelado.
  const prevented = () => page.evaluate(() => {
    const e = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(e);
    return e.defaultPrevented;
  });
  expect(await prevented()).toBe(true);
  await page.getByRole('button', { name: 'Cancelar' }).click();
  const confirm = page.getByRole('dialog', { name: '¿Descartar los cambios?' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: 'Seguir editando' }).click();
  await expect(page.getByLabel('Descripción')).toHaveValue('Borrador sin aplicar');
  await page.getByRole('button', { name: 'Cancelar' }).click();
  await page.getByRole('dialog', { name: '¿Descartar los cambios?' }).getByRole('button', { name: 'Descartar cambios' }).click();
  await expect(page).toHaveURL(/\/admin\/model$/);
  await expect.poll(prevented).toBe(false); // la URL cambia antes de que React desmonte el editor
});

test('H8 CA3: no se quita un estado del proceso en el que hay registros', async () => {
  const admin = await as('admin');
  const workflow = (states: string[]) => ({ initial: 'Abierto', states: states.map((name) => ({ name, label: name })),
    transitions: states.includes('Pausado') ? [{ name: 'pausar', label: 'Pausar', from: ['Abierto'], to: 'Pausado' }] : [] });
  await applyPlan(admin, { description: 'E2E 021: proceso', operations: [
    { op: 'createEntity', group: GROUP, name: 'E21Proceso', label: 'Proceso 021', labelPlural: 'Procesos 021', fields: [{ name: 'Clave', label: 'Clave', type: 'text', required: true }] },
    { op: 'setWorkflow', entity: 'E21Proceso', workflow: workflow(['Abierto', 'Pausado']) },
  ] });
  const r = (await (await admin.post('/api/data/E21Proceso', { data: { Clave: 'P1' } })).json()) as Row;
  const moved = await admin.post(`/api/data/E21Proceso/${r.id}/transition`, { data: { transition: 'pausar', _rowVersion: r._rowVersion } });
  expect(moved.status(), await moved.text()).toBe(200);
  const plan = { description: 'Quitar Pausado', operations: [{ op: 'setWorkflow', entity: 'E21Proceso', workflow: workflow(['Abierto']) }] };
  const preview = await (await admin.post('/api/model/plan/preview', { data: plan })).json();
  expect(preview.valid).toBe(false);
  expect(preview.errors.map((e: { message: string }) => e.message).join(' ')).toContain('1 registro(s) están en el estado «Pausado»');
});

// ---------------- H3: un trabajo se ejecuta una vez ----------------

test('H3 CA4: un trabajo con el lease vencido y sin intentos no se vuelve a ejecutar', async () => {
  const admin = await as('admin');
  const counter = (entity: string) => ({ op: 'createEntity', group: GROUP, name: entity, label: entity, labelPlural: entity, fields: [
    { name: 'Clave', label: 'Clave', type: 'text', required: true }, { name: 'Veces', label: 'Veces', type: 'integer', defaultValue: '0' }] });
  const auto = (entity: string) => ({ op: 'setAutomations', entity, automations: [{ name: 'Contar', label: `Contar ${entity}`, on: 'create', mode: 'async',
    actions: [{ type: 'update', set: [{ field: 'Veces', value: 'Veces + 1' }] }] }] });
  await applyPlan(admin, { description: 'E2E 021: bandeja', operations: [counter('E21Contador'), auto('E21Contador'), counter('E21Fallo')] });
  const rec = (await (await admin.post('/api/data/E21Contador', { data: { Clave: 'C1' } })).json()) as Row;
  await waitFor(async () => (await (await admin.get(`/api/data/E21Contador/${rec.id}`)).json()).Veces, (v) => v === 1, 'primera ejecución');

  // Otra instancia lo reclamó, gastó sus tres intentos y cayó: el lease venció.
  const id = sql(`INSERT meta.AutomationRun (Kind, EntityName, Name, RecordId, Event, TriggeredBy, RunAsName, Generation, Status, Attempts, LeaseUntil, StartedAt)
    OUTPUT inserted.Id VALUES ('async', 'E21Contador', 'Contar', ${rec.id}, 'create', 1, 'admin', 1, 'running', 3, DATEADD(minute, -1, SYSUTCDATETIME()), SYSUTCDATETIME())`);
  const run = await waitFor(async () => (await runs('Contar')).find((r) => r.id === Number(id))!, (r) => r.status !== 'running', 'que deje de estar en curso');
  expect(run.status).toBe('failed');
  expect(run.lastError).toMatch(/se interrumpió 3 veces/i);
  expect((await (await admin.get(`/api/data/E21Contador/${rec.id}`)).json()).Veces).toBe(1);
});

test('H3 CA1: los efectos y el estado «correcto» se guardan juntos, o ninguno', async () => {
  const admin = await as('admin');
  // Marcar el éxito falla (simula que el proceso cae justo después de confirmar el trabajo). Una restricción y no un trigger:
  // el reclamo de la bandeja usa UPDATE … OUTPUT, que no admite triggers en la tabla.
  sql(`ALTER TABLE meta.AutomationRun ADD CONSTRAINT CK_E2E21_Falla CHECK (NOT (EntityName = 'E21Fallo' AND Status = 'succeeded'))`);
  await applyPlan(admin, { description: 'E2E 021: trabajo que falla al marcar', operations: [
    { op: 'setAutomations', entity: 'E21Fallo', automations: [{ name: 'ContarFallo', label: 'Contar fallo', on: 'create', mode: 'async',
      actions: [{ type: 'update', set: [{ field: 'Veces', value: 'Veces + 1' }] }] }] }] });
  const rec = (await (await admin.post('/api/data/E21Fallo', { data: { Clave: 'F1' } })).json()) as Row;
  const run = await waitFor(async () => (await runs('ContarFallo')).find((r) => r.recordId === rec.id), (r) => !!r && !['pending', 'running'].includes(r.status), 'que termine');
  sql('ALTER TABLE meta.AutomationRun DROP CONSTRAINT CK_E2E21_Falla');
  expect(run!.status).toBe('failed');
  // Si el trabajo «falló», no dejó efectos (antes: Veces = 1 con el estado fallido, y reintentarlo lo duplicaba).
  expect((await (await admin.get(`/api/data/E21Fallo/${rec.id}`)).json()).Veces).toBe(0);
});

// ---------------- H4: tareas atadas a lo que se activó ----------------

test('H4 CA1 · CA2: cambiar el contenido de una tarea activa o volver a declararla la deja en pausa', async () => {
  const admin = await as('admin');
  const svc = await admin.post('/api/admin/roles', { data: { name: 'E21 Servicio', description: 'E2E spec 021', canManageModel: false, canManageUsers: false,
    canViewAudit: false, permissions: [] } });
  expect(svc.status(), await svc.text()).toBe(200);
  const schedule = (where: string) => ({ name: 'Marcar', label: 'Marcar tareas', every: 'day', at: '07:00', where, role: 'E21 Servicio', batchSize: 5,
    actions: [{ type: 'update', set: [{ field: 'Marcas', value: 'Marcas + 1' }] }] });
  await applyPlan(admin, { description: 'E2E 021: tarea', operations: [
    { op: 'createEntity', group: GROUP, name: 'E21Tarea', label: 'Tarea 021', labelPlural: 'Tareas 021', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true }, { name: 'Activo', label: 'Activo', type: 'boolean', defaultValue: 'TRUE' },
      { name: 'Marcas', label: 'Marcas', type: 'integer', defaultValue: '0' }] },
    { op: 'setSchedules', entity: 'E21Tarea', schedules: [schedule('Activo')] },
  ] });
  const state = async () => ((await (await admin.get('/api/admin/automations/schedules')).json()) as { entity: string; name: string; active: boolean }[])
    .find((s) => s.entity === 'E21Tarea')!;
  const activate = async () => expect((await admin.put('/api/admin/automations/schedules/E21Tarea/Marcar', { data: { active: true } })).status()).toBe(200);

  await activate();
  expect((await state()).active).toBe(true);
  // Cambiar solo la etiqueta no la pausa.
  await applyPlan(admin, { description: 'Etiqueta', operations: [{ op: 'setSchedules', entity: 'E21Tarea', schedules: [{ ...schedule('Activo'), label: 'Marcar tareas (renombrada)' }] }] });
  expect((await state()).active).toBe(true);
  // Cambiar la consulta sí.
  await applyPlan(admin, { description: 'Consulta', operations: [{ op: 'setSchedules', entity: 'E21Tarea', schedules: [schedule('NOT Activo')] }] });
  expect((await state()).active).toBe(false);
  const audit = (await (await admin.get('/api/audit', { params: { action: 'automation.schedule', pageSize: '5' } })).json()) as { items: { recordId: string; after: { action: string; reason?: string } }[] };
  expect(audit.items[0]).toMatchObject({ recordId: 'Marcar', after: { action: 'pause' } });

  // Quitarla y volver a declararla igual: llega en pausa.
  await activate();
  await applyPlan(admin, { description: 'Quitar', operations: [{ op: 'setSchedules', entity: 'E21Tarea', schedules: [] }] });
  await applyPlan(admin, { description: 'Volver a declarar', operations: [{ op: 'setSchedules', entity: 'E21Tarea', schedules: [schedule('NOT Activo')] }] });
  expect((await state()).active).toBe(false);
});

test('H4 CA3: una ocurrencia encolada de una tarea que cambió se cancela al ejecutarse', async () => {
  const admin = await as('admin');
  expect((await admin.put('/api/admin/automations/schedules/E21Tarea/Marcar', { data: { active: true } })).status()).toBe(200);
  // La huella guardada ya no es la del contenido actual (p. ej. un cambio que llegó entre encolar y ejecutar).
  sql(`UPDATE meta.ScheduleState SET ContentHash = REPLICATE('0', 64) WHERE EntityName = 'E21Tarea' AND ScheduleName = 'Marcar';
       INSERT meta.AutomationRun (Kind, EntityName, Name, RunAsRole, RunAsName, Manual, Status) VALUES ('schedule', 'E21Tarea', 'Marcar', 'E21 Servicio', 'Tarea E21', 0, 'pending')`);
  const run = await waitFor(async () => (await runs('Marcar'))[0], (r) => !['pending', 'running'].includes(r.status), 'que termine');
  expect(run.status).toBe('canceled');
  expect(run.lastError).toContain('cambió');
  expect((await admin.put('/api/admin/automations/schedules/E21Tarea/Marcar', { data: { active: false } })).status()).toBe(200);
});

// ---------------- H5: la reversión dice qué seguridad cambia ----------------

test('H5 CA1 · CA2: revertir un paquete que agregó seguridad la muestra y exige confirmarla', async ({ page }) => {
  const admin = await as('admin');
  const aprobador = await as('aprobador');
  await applyPlan(admin, { description: 'E2E 021: antes', operations: [
    { op: 'createEntity', group: GROUP, name: 'E21Rev', label: 'Revisión 021', labelPlural: 'Revisiones 021', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true }, { name: 'Secreto', label: 'Secreto', type: 'text' }] },
  ] });
  // v1: oculta el secreto a todos salvo al administrador. Se exporta y se vuelve a «antes».
  await applyPlan(admin, { description: 'E2E 021: v1', operations: [
    { op: 'setSecurity', entity: 'E21Rev', security: { fields: [{ field: 'Secreto', read: ['Administrador'] }] } }] });
  const exported = await admin.post('/api/admin/environments/export', { data: { id: 'e21-rev', name: 'Revisiones 021', version: '1.0.0', entities: ['E21Rev'], dashboards: [] } });
  expect(exported.status(), await exported.text()).toBe(200);
  await applyPlan(admin, { description: 'E2E 021: de vuelta', operations: [{ op: 'setSecurity', entity: 'E21Rev', security: null }] });

  const post = (who: APIRequestContext, p: string, data: object = {}) => who.post(`/api/admin/environments/imports/${p}`, { data });
  const detail = async (id: number) => (await admin.get(`/api/admin/environments/imports/${id}`)).json();
  const up = await admin.post('/api/admin/environments/imports', { data: await exported.body(), headers: { 'Content-Type': 'application/octet-stream' } });
  expect(up.status(), await up.text()).toBe(200);
  const applyId = (await up.json()).id as number;
  const d = await detail(applyId);
  expect((await post(aprobador, `${applyId}/approve`, { diffHash: d.diff.diffHash })).status()).toBe(200);
  expect((await post(admin, `${applyId}/apply`, { diffHash: d.diff.diffHash })).status()).toBe(200);

  const rev = await post(admin, `${applyId}/revert`);
  expect(rev.status(), await rev.text()).toBe(200);
  const revertId = (await rev.json()).id as number;
  const r = await detail(revertId);
  expect(r.diff.securityChanges.join(' ')).toContain('Quita la seguridad por campo y por registro de «Revisión 021»');
  const noConfirm = await post(aprobador, `${revertId}/approve`, { diffHash: r.diff.diffHash });
  expect(noConfirm.status()).toBe(409);
  expect(await noConfirm.text()).toContain('seguridad');
  expect((await post(aprobador, `${revertId}/approve`, { diffHash: r.diff.diffHash, confirmSecurity: true })).status()).toBe(200);
  expect((await post(admin, `${revertId}/apply`, { diffHash: r.diff.diffHash })).status()).toBe(409);

  // Desde la pantalla: la lista y la casilla.
  await page.goto('/admin/environments');
  await page.getByRole('button', { name: 'Revisar Reversión de Revisiones 021 v1.0.0' }).click();
  const review = page.getByRole('dialog', { name: 'Reversión de Revisiones 021 v1.0.0' });
  await expect(review).toContainText('Quita la seguridad por campo y por registro de «Revisión 021»');
  const apply = review.getByRole('button', { name: 'Aplicar reversión' });
  await expect(apply).toBeDisabled();
  await review.getByText('Entiendo que la reversión cambia la seguridad').click();
  await apply.click();
  await expect(review).toContainText('Reversión aplicada');
  expect((await entity('E21Rev')).security ?? null).toBeNull();
});
