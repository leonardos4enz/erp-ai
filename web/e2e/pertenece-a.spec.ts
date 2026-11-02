// Spec 051 · «pertenece a»: el constructor de condiciones arma «CURRENTUSER() IN Centro.Responsables» en un permiso por registro
// (H1 CA5) y la persona solo ve en la lista lo de los centros a su cargo (H2 CA1); una asignación de transición agrega a quien la
// ejecuta (H3 CA4). Autocontenido: crea sus catálogos (grupo «Pruebas 051») y los archiva al terminar.
import type { Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 051';
const ROLE = 'E2E Pertenencia';
const LECTOR = 'e2e51-lector';
const PWD = 'frase larga de pertenencia e2e 051';

async function folioOf(id: number) {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  return (await (await api.get(`/api/data/PeReq/${id}`)).json()).Folio as string;
}

test.beforeAll(async () => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  if ((await api.get('/api/model/entities/PeReq')).ok()) return;
  const roles = (await (await api.get('/api/admin/roles')).json()) as { name: string }[];
  if (!roles.some((r) => r.name === ROLE)) {
    const r = await api.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 051', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] } });
    expect(r.status(), await r.text()).toBe(200);
  }
  await applyPlan(api, {
    description: 'E2E 051: pertenece a',
    operations: [
      { op: 'createEntity', group: GROUP, name: 'PeCentro', label: 'Centro 051', labelPlural: 'Centros 051', displayField: 'Nombre',
        fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }, { name: 'Responsables', label: 'Responsables', type: 'multiuser' }] },
      { op: 'createEntity', group: GROUP, name: 'PeReq', label: 'Requisición 051', labelPlural: 'Requisiciones 051', displayField: 'Folio',
        fields: [
          { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'PE-', padding: 4 },
          { name: 'Nota', label: 'Nota', type: 'text' },
          { name: 'Centro', label: 'Centro', type: 'reference', referenceEntity: 'PeCentro', required: true },
          { name: 'Seguidores', label: 'Seguidores', type: 'multiuser', editable: false },
        ] },
      { op: 'setWorkflow', entity: 'PeReq', workflow: {
        initial: 'Borrador',
        states: [{ name: 'Borrador', label: 'Borrador', editable: true }, { name: 'Enviada', label: 'Enviada', editable: false }],
        transitions: [{ name: 'enviar', label: 'Enviar', from: ['Borrador'], to: 'Enviada' }],
      } },
    ],
  });
  const role = await roleId(api, ROLE);
  expect((await api.put(`/api/admin/roles/${role}`, { data: { name: ROLE, description: 'E2E spec 051', canManageModel: false, canManageUsers: false, canViewAudit: false,
    permissions: [
      { entity: 'PeCentro', canRead: true, canCreate: false, canUpdate: false, canDelete: false },
      { entity: 'PeReq', canRead: true, canCreate: false, canUpdate: false, canDelete: false },
    ] } })).status()).toBe(200);
  const users = (await (await api.get('/api/admin/users')).json()) as { id: number; username: string }[];
  let lector = users.find((u) => u.username === LECTOR)?.id;
  if (!lector) {
    const u = await api.post('/api/admin/users', { data: { username: LECTOR, displayName: 'Lectora 051', password: PWD, roleIds: [role] } });
    expect(u.status(), await u.text()).toBe(201);
    lector = (await u.json()).id;
  }
  const norte = await (await api.post('/api/data/PeCentro', { data: { Nombre: 'Norte 051', Responsables: [lector] } })).json();
  const sur = await (await api.post('/api/data/PeCentro', { data: { Nombre: 'Sur 051', Responsables: [] } })).json();
  for (const [Nota, Centro] of [['Del norte', norte.id], ['Del sur', sur.id]] as const)
    expect((await api.post('/api/data/PeReq', { data: { Nota, Centro } })).status()).toBe(201);
});

test.afterAll(async () => {
  if (process.env.E2E_INDEPENDENT) return;
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await api.get('/api/model/entities')).json()) as { name: string }[];
  const operations = ['PeReq', 'PeCentro'].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(api, { description: 'E2E 051: limpieza', operations });
});

async function applyFromDialog(page: Page) {
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
}

async function def(name: string) {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  return (await api.get(`/api/model/entities/${name}`)).json();
}

test('H1 CA5 · H2 CA1: el constructor arma «CURRENTUSER() IN Centro.Responsables» y la persona solo ve lo de sus centros', async ({ page, browser }) => {
  await page.goto('/admin/model?edit=PeReq&tab=seguridad');
  await page.getByRole('button', { name: 'Definir seguridad' }).click();
  await page.getByRole('button', { name: 'Agregar regla por registro' }).click();
  await page.getByLabel('Regla 1: alcance').selectOption('all');
  await page.getByRole('group', { name: 'Regla 1: roles' }).getByLabel(ROLE).check();
  await page.getByLabel('Regla 1: acceso').selectOption('read');
  const where = page.getByRole('group', { name: /^Regla 1: además, solo donde/ });
  await where.getByRole('button', { name: 'Agregar condición' }).click();
  await where.getByLabel('Condición 1: campo', { exact: true }).selectOption('Centro');
  await where.getByLabel('Condición 1: campo de Centro').selectOption('Responsables');
  // Un campo de varios valores se pregunta con «contiene»; con personas, el valor inicial es quien usa el sistema.
  await expect(where.getByLabel('Condición 1: operador')).toHaveValue('contains');
  await expect(where.getByLabel('Condición 1: comparar con')).toHaveValue('me');
  await expectAccessible(page);
  await applyFromDialog(page);
  expect((await def('PeReq')).security.records).toEqual([expect.objectContaining({
    scope: 'all', roles: [ROLE], access: 'read', where: 'CURRENTUSER() IN Centro.Responsables',
  })]);

  // La persona responsable de Norte ve su requisición y no la de Sur.
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const all = (await (await api.get('/api/data/PeReq', { params: { pageSize: 50 } })).json()).items as { id: number; Nota: string }[];
  const norte = await folioOf(all.find((r) => r.Nota === 'Del norte')!.id);
  const sur = await folioOf(all.find((r) => r.Nota === 'Del sur')!.id);
  const ctx = await browser.newContext({ storageState: undefined });
  const lector = await ctx.newPage();
  try {
    await uiLogin(lector, LECTOR, PWD);
    await lector.goto('/c/PeReq');
    await expect(lector.getByRole('row').filter({ hasText: norte })).toBeVisible();
    await expect(lector.getByRole('row').filter({ hasText: sur })).toBeHidden();
    // Por URL directa tampoco.
    const id = all.find((r) => r.Nota === 'Del sur')!.id;
    await lector.goto(`/c/PeReq/${id}`);
    await expect(lector.getByRole('alert').or(lector.getByText(/no existe|no se encontró/i)).first()).toBeVisible();
  } finally {
    await ctx.close();
  }
});

test('H3 CA4: una asignación de transición a un campo de varios valores pide agregar o quitar y agrega a quien la ejecuta', async ({ page }) => {
  await page.goto('/admin/model?edit=PeReq&tab=proceso');
  await page.getByRole('button', { name: /^Transición Enviar/ }).click();
  const panel = page.getByRole('complementary', { name: 'Propiedades' });
  await panel.getByRole('button', { name: 'Agregar asignación' }).click();
  await panel.getByLabel('Campo de la asignación 1').selectOption('Seguidores');
  const mode = panel.getByLabel('Qué hace la asignación 1 con Seguidores');
  await expect(mode).toHaveValue('add');
  await expect(mode.getByRole('option')).toHaveText(['Agregar a la lista', 'Quitar de la lista']);
  await panel.getByLabel('Valor de la asignación 1').fill('CURRENTUSER()');
  // Un campo simple no muestra el modo.
  await panel.getByLabel('Campo de la asignación 1').selectOption('Nota');
  await expect(panel.getByLabel(/^Qué hace la asignación 1/)).toHaveCount(0);
  await panel.getByLabel('Campo de la asignación 1').selectOption('Seguidores');
  await expectAccessible(page);
  // Regresión (revisión exploratoria 051, F-01): en móvil, con el modo, el valor quedaba sin ancho; ahora baja a su propia línea.
  await page.setViewportSize({ width: 390, height: 844 });
  expect((await panel.getByLabel('Valor de la asignación 1').boundingBox())!.width).toBeGreaterThan(200);
  await page.setViewportSize({ width: 1280, height: 860 });
  await applyFromDialog(page);
  expect((await def('PeReq')).workflow.transitions[0].set).toEqual([{ field: 'Seguidores', value: 'CURRENTUSER()', mode: 'add' }]);

  // Y funciona: enviar desde el registro agrega a quien la ejecuta.
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const created = await (await api.post('/api/data/PeReq', { data: { Nota: 'Para enviar', Centro: (await (await api.get('/api/data/PeCentro', { params: { pageSize: 50 } })).json()).items[0].id } })).json();
  await page.goto(`/c/PeReq/${created.id}`);
  // La espera se registra antes de actuar (034, --lento): se lee el registro cuando la transición ya respondió.
  const sent = page.waitForResponse((r) => r.url().endsWith(`/api/data/PeReq/${created.id}/transition`) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Enviar' }).click();
  const confirm = page.getByRole('dialog');
  if (await confirm.isVisible().catch(() => false)) await confirm.getByRole('button', { name: /^Enviar|Confirmar/ }).click();
  expect((await sent).status()).toBe(200);
  const after = await (await api.get(`/api/data/PeReq/${created.id}`)).json();
  expect(after.Seguidores__label).toEqual([(await (await api.get('/api/auth/me')).json()).displayName]);
});
