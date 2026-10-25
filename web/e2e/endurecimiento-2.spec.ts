// Spec 021 · Endurecimiento 2. Cada prueba es un hallazgo de la auditoría de solo lectura (2026-10-24) y se verificó
// fallando con el código anterior. Los catálogos de apoyo son del grupo «Pruebas 021» y se archivan al terminar.
// Spec 030: aquí quedan las pruebas que usan la página; las de API (H1 CA1–CA5, H2 CA1·CA2, H3, H4, H8 CA1 y CA3) están
// en tests/Erp.IntegrationTests/Hardening021Tests.cs.
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

// Al verificar contra el código anterior (E2E_INDEPENDENT=1) una falla no salta las pruebas siguientes: cada una recupera
// los ids por nombre (el proceso de Playwright se reinicia tras una falla y pierde el estado del módulo).
test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 021';
const PWD = 'frase larga de endurecimiento 021';
const users = { ua: 'e2e-e21-usuarios', ub: 'e2e-e21-usuarios2', su: 'e2e-e21-super', basico: 'e2e-e21-basico', aprobador: 'e2e-e21-aprobador' } as const;
type Who = keyof typeof users | 'admin';

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
const ids: Record<string, number> = {};

const userById = async (id: number) =>
  ((await (await (await as('admin')).get('/api/admin/users')).json()) as { id: number; roleIds: number[]; displayName: string }[]).find((u) => u.id === id)!;
const entity = async (name: string) => (await (await (await as('admin')).get(`/api/model/entities/${name}`)).json());
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
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await admin.get('/api/model/entities', { params: { includeChildren: 'true' } })).json()) as { name: string }[];
  const operations = ['E21Doc', 'E21Rev']
    .filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(admin, { description: 'E2E 021: limpieza', operations });
  await admin.dispose();
});

test('preparar: roles con y sin capacidades, administradores de usuarios, un segundo superusuario y un documento', async () => {
  const admin = await as('admin');
  // Spec 030 (R-028-3): tolera lo que ya existe (con E2E_INDEPENDENT=1 una falla vuelve a correr la preparación).
  const role = async (key: string, name: string, caps: { canManageModel?: boolean; canManageUsers?: boolean; canViewAudit?: boolean }) => {
    if (ids[key]) return;
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 021', canManageModel: false, canManageUsers: false,
      canViewAudit: false, ...caps, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    ids[key] = (await res.json()).id as number;
  };
  await role('usuarios', 'E21 Usuarios', { canManageUsers: true });
  await role('modelador', 'E21 Modelador', { canManageModel: true });
  await role('auditor', 'E21 Auditor', { canViewAudit: true });
  await role('basicoRol', 'E21 Básico', {});
  ids.superRol = await roleId(admin, 'Administrador');
  for (const [key, username] of Object.entries(users)) {
    if (ids[key]) continue;
    const roleIds = key === 'su' || key === 'aprobador' ? [ids.superRol] : key === 'basico' ? [ids.basicoRol] : [ids.usuarios];
    const res = await admin.post('/api/admin/users', { data: { username, displayName: `E21 ${key}`, password: PWD, roleIds } });
    expect(res.status(), await res.text()).toBe(201);
    ids[key] = (await res.json()).id;
  }
  if ((await admin.get('/api/model/entities/E21Doc')).status() === 404)
    await applyPlan(admin, { description: 'E2E 021: documento', operations: [
      { op: 'createEntity', group: GROUP, name: 'E21Doc', label: 'Documento 021', labelPlural: 'Documentos 021', fields: [
        { name: 'Clave', label: 'Clave', type: 'text', required: true }, { name: 'Notas', label: 'Notas', type: 'text' }] },
    ] });
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

// ---------------- H2: cambios de modelo sin pisarse ----------------

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
