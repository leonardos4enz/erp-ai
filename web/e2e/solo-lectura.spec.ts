// Spec 013 · Formulario en solo lectura fuera del alcance de escritura. El caso de seguridad.spec.ts (registro ajeno de
// solo lectura) no cambia de alcance; aquí el autor escribe su solicitud solo en «Borrador» y, al enviarla, el formulario
// pasa a solo lectura con la respuesta de la transición, sin recargar. El catálogo del grupo «Pruebas 013» se archiva al
// terminar (no altera el menú de las suites siguientes ni las capturas de referencia).
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const GROUP = 'Pruebas 013';
const PWD = 'frase larga de solo lectura 2026';
const ROLE = 'Ro Autor';
const USER = 'e2e-ro-autor';

let autor: APIRequestContext;

test.afterAll(async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await admin.get('/api/model/entities')).json()) as { name: string }[];
  if (model.some((e) => e.name === 'RoSolicitud'))
    await applyPlan(admin, { description: 'E2E 013: limpieza', operations: [{ op: 'archiveEntity', entity: 'RoSolicitud' }] });
  await admin.dispose();
});

test('preparar: solicitud que su autor solo escribe en Borrador', async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const res = await admin.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 013', canManageModel: false, canManageUsers: false,
    canViewAudit: false, permissions: [] } });
  expect(res.status(), await res.text()).toBe(200);
  await applyPlan(admin, { description: 'Spec 013 · catálogo', operations: [
    { op: 'createEntity', group: GROUP, name: 'RoSolicitud', label: 'Solicitud 013', labelPlural: 'Solicitudes 013', displayField: 'Titulo', fields: [
      { name: 'Titulo', label: 'Título', type: 'text', required: true },
    ] },
    { op: 'setWorkflow', entity: 'RoSolicitud', workflow: {
      initial: 'Borrador',
      // «Enviada» sigue siendo editable por estado: lo que la bloquea es el alcance de escritura.
      states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Enviada', label: 'Enviada', color: 'warning' }],
      transitions: [{ name: 'enviar', label: 'Enviar', from: ['Borrador'], to: 'Enviada' }],
    } },
    { op: 'setSecurity', entity: 'RoSolicitud', security: { records: [
      { roles: [ROLE], scope: 'own', where: "Estado = 'Borrador'" },
      { roles: [ROLE], scope: 'own', access: 'read' },
    ] } },
  ] });
  const roles = await (await admin.get('/api/admin/roles')).json();
  const r = roles.find((x: { name: string }) => x.name === ROLE);
  const put = await admin.put(`/api/admin/roles/${r.id}`, { data: { name: ROLE, description: r.description, canManageModel: false, canManageUsers: false,
    canViewAudit: false, permissions: [{ entity: 'RoSolicitud', canRead: true, canCreate: true, canUpdate: true, canDelete: true }] } });
  expect(put.status(), await put.text()).toBe(200);
  const user = await admin.post('/api/admin/users', { data: { username: USER, displayName: 'Autor 013', password: PWD, roleIds: [await roleId(admin, ROLE)] } });
  expect(user.status(), await user.text()).toBe(201);
  await admin.dispose();
  autor = await apiAs(USER, PWD);
});

test('Spec 013 · H1 CA4: al salir del alcance de escritura por una transición, el formulario pasa a solo lectura sin recargar', async ({ browser }) => {
  const created = await autor.post('/api/data/RoSolicitud', { data: { Titulo: 'Material de oficina' } });
  expect(created.status(), await created.text()).toBe(201);
  const s = await created.json();
  expect(s._access).toEqual({ update: true, delete: true, reason: null });

  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, USER, PWD);
  await page.goto(`/c/RoSolicitud/${s.id}`);
  await expect(page.getByLabel(/^Título/)).toBeEditable();
  await expect(page.getByRole('button', { name: 'Guardar cambios' })).toBeVisible();

  await page.getByRole('button', { name: 'Enviar' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Enviar' }).click();
  await expect(page.getByText('Estado: Enviada')).toBeVisible();
  // Sin recargar: la respuesta de la transición trae el nuevo `_access`.
  await expect(page.getByLabel(/^Título/)).toBeDisabled();
  await expect(page.getByText('Puedes consultar este registro, pero tu rol no puede modificarlo.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guardar cambios' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Eliminar', exact: true })).toHaveCount(0);
  await ctx.close();

  // El servidor coincide (y sigue rechazando).
  const sent = await (await autor.get(`/api/data/RoSolicitud/${s.id}`)).json();
  expect(sent._access).toEqual({ update: false, delete: false, reason: expect.stringContaining('no puede modificarlo') });
  expect((await autor.put(`/api/data/RoSolicitud/${s.id}`, { data: { _rowVersion: sent._rowVersion, Titulo: 'Otro' } })).status()).toBe(403);
});
