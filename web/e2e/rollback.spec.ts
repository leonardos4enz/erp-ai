// Spec 017 · Rollback de entornos: instantánea al aplicar un paquete, «revertir a la versión anterior» como diff inverso con
// la misma compuerta (vista previa, aprobación de otro administrador, auditoría), y reactivar campos y catálogos archivados.
// La suite corre como «producción» (aprobación obligatoria y firma). Un solo servidor hace de origen y de destino: se arma el
// estado «v1», se exporta, y el modelo se regresa a mano al estado «antes» (lo nuevo queda archivado) antes de subirlo.
import { execFileSync } from 'node:child_process';
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, DB, apiAs, applyPlan, expect, expectAccessible, test, uiLogin, roleId } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de reversiones 2026';
const GROUP = 'Rollback E2E';
const APPROVER = 'e2e-rb-aprobador';

const api: Record<string, APIRequestContext> = {};
const as = async (who: 'admin' | 'aprobador') =>
  (api[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(APPROVER, PWD));

/** SQL directo sobre la base desechable de la suite (nunca la de desarrollo). */
function sql(query: string) {
  const db = DB;
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  return execFileSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-h', '-1', '-W', '-Q',
    `SET NOCOUNT ON; ${query}`]).toString().trim();
}

async function pageAs(browser: Browser, user: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

const detail = async (id: number) => (await (await as('admin')).get(`/api/admin/environments/imports/${id}`)).json();
const audit = async (action: string) => (await (await as('admin')).get('/api/audit', { params: { action, pageSize: '20' } })).json();
const post = async (who: 'admin' | 'aprobador', path: string, data: object = {}) => (await as(who)).post(`/api/admin/environments/imports/${path}`, { data });
const upload = async (file: Buffer) => (await as('admin')).post('/api/admin/environments/imports', { data: file, headers: { 'Content-Type': 'application/octet-stream' } });
const field = (entity: { fields: { name: string; label: string; isArchived?: boolean }[] }, name: string) => entity.fields.find((f) => f.name === name);
const model = async (name: string) => {
  const all = await (await (await as('admin')).get('/api/model/entities', { params: { includeArchived: 'true', includeChildren: 'true' } })).json();
  return all.find((e: { name: string }) => e.name === name);
};

let paquete: Buffer;      // rb-contratos 1.0.0
let proveedorId: number;  // registro de RbProveedor con Notas
let contratoId: number;   // registro de RbContrato
let applyId: number;      // aplicación del paquete
let revertId: number;     // solicitud de reversión vigente

test('preparar: estado «antes», estado «v1», exportar y volver a «antes»; un segundo administrador', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/users', { data: { username: APPROVER, displayName: 'E2E aprobador 017', password: PWD,
    roleIds: [await roleId(admin, 'Administrador')] } });
  expect(res.status(), await res.text()).toBe(201);

  // Antes: un proveedor con notas.
  await applyPlan(admin, { description: 'E2E 017: antes', operations: [
    { op: 'createEntity', name: 'RbProveedor', label: 'Proveedor RB', labelPlural: 'Proveedores RB', group: GROUP,
      fields: [{ name: 'Clave', label: 'Clave', type: 'text', required: true, unique: true }, { name: 'Nombre', label: 'Nombre', type: 'text' },
        { name: 'Notas', label: 'Notas', type: 'text' }] },
  ] });
  proveedorId = (await (await admin.post('/api/data/RbProveedor', { data: { Clave: 'P1', Nombre: 'Proveedor Uno', Notas: 'nota antigua' } })).json()).id;

  // v1: cambia el proveedor (etiqueta, teléfono, sin notas) y crea Contratos con partidas.
  await applyPlan(admin, { description: 'E2E 017: v1', operations: [
    { op: 'updateField', entity: 'RbProveedor', field: 'Nombre', label: 'Razón social' },
    { op: 'archiveField', entity: 'RbProveedor', field: 'Notas' },
    { op: 'addField', entity: 'RbProveedor', field: { name: 'Telefono', label: 'Teléfono', type: 'text' } },
    { op: 'createEntity', name: 'RbContrato', label: 'Contrato RB', labelPlural: 'Contratos RB', group: GROUP,
      fields: [{ name: 'Clave', label: 'Clave', type: 'text', required: true, unique: true },
        { name: 'Proveedor', label: 'Proveedor', type: 'reference', referenceEntity: 'RbProveedor' }] },
    { op: 'createEntity', name: 'RbContratoLinea', label: 'Línea de contrato RB', labelPlural: 'Líneas de contrato RB',
      fields: [{ name: 'Contrato', label: 'Contrato', type: 'parent', referenceEntity: 'RbContrato' }, { name: 'Concepto', label: 'Concepto', type: 'text' }] },
    { op: 'addField', entity: 'RbContrato', field: { name: 'Lineas', label: 'Líneas', type: 'lines', childEntity: 'RbContratoLinea' } },
  ] });
  const role = await admin.post('/api/admin/roles', { data: { name: 'Gestor17', description: 'E2E spec 017', canManageModel: false, canManageUsers: false,
    canViewAudit: false, permissions: [{ entity: 'RbContrato', canRead: true, canCreate: true, canUpdate: false, canDelete: false }] } });
  expect(role.status(), await role.text()).toBe(200);
  contratoId = (await (await admin.post('/api/data/RbContrato', { data: { Clave: 'C1', Proveedor: proveedorId } })).json()).id;

  const exported = await admin.post('/api/admin/environments/export', { data: { id: 'rb-contratos', name: 'Contratos', version: '1.0.0',
    entities: ['RbProveedor', 'RbContrato'], dashboards: [] } });
  expect(exported.status(), await exported.text()).toBe(200);
  paquete = await exported.body();

  // De vuelta a «antes»: lo nuevo queda archivado (con su registro) y las notas reaparecen (H4: restoreField).
  await applyPlan(admin, { description: 'E2E 017: de vuelta a antes', operations: [
    { op: 'archiveField', entity: 'RbContrato', field: 'Lineas' },
    { op: 'archiveEntity', entity: 'RbContratoLinea' },
    { op: 'archiveEntity', entity: 'RbContrato' },
    { op: 'restoreField', entity: 'RbProveedor', field: 'Notas' },
    { op: 'archiveField', entity: 'RbProveedor', field: 'Telefono' },
    { op: 'updateField', entity: 'RbProveedor', field: 'Nombre', label: 'Nombre' },
  ] });
  const p = await (await admin.get(`/api/data/RbProveedor/${proveedorId}`)).json();
  expect(p.Notas).toBe('nota antigua'); // la columna conservó el dato
});

test('H1 · H4 CA4: el paquete reactiva lo archivado y al aplicarlo guarda las instantáneas antes y después', async () => {
  const res = await upload(paquete);
  expect(res.status(), await res.text()).toBe(200);
  applyId = (await res.json()).id;
  const d = await detail(applyId);
  expect(d.diff.valid, JSON.stringify(d.diff)).toBe(true);
  expect(d.diff.conflicts).toEqual([]);
  const steps = d.diff.steps.map((s: { description: string }) => s.description).join(' | ');
  expect(steps).toContain("Reactivar catálogo 'Contrato RB'");
  expect(steps).toContain("Reactivar catálogo 'Línea de contrato RB'");
  expect(steps).toContain("Reactivar campo 'Teléfono' de 'Proveedor RB'");
  expect(steps).toContain("Reactivar campo 'Líneas' de 'Contrato RB'");
  expect(d.diff.localArchives.join(' ')).toContain('Notas no viene en el paquete');
  expect(d.snapshot ?? null).toBeNull(); // aún no se aplica

  expect((await post('aprobador', `${applyId}/approve`, { diffHash: d.diff.diffHash, confirmArchive: true })).status()).toBe(200);
  const applied = await post('admin', `${applyId}/apply`, { diffHash: d.diff.diffHash, confirmArchive: true });
  expect(applied.status(), await applied.text()).toBe(200);

  const after = await detail(applyId);
  expect(after).toMatchObject({ status: 'applied', kind: 'package', canRevert: true });
  expect(after.snapshot.beforeHash).toMatch(/^[0-9a-f]{64}$/);
  expect(after.snapshot.afterHash).toMatch(/^[0-9a-f]{64}$/);
  expect(after.snapshot.beforeHash).not.toBe(after.snapshot.afterHash);
  expect((await audit('environment.apply')).items[0].after.snapshot).toEqual({ before: after.snapshot.beforeHash, after: after.snapshot.afterHash });
  // Solo definiciones: la instantánea no lleva registros.
  expect(sql(`SELECT COUNT(*) FROM meta.ModelSnapshot WHERE ImportId = ${applyId}`)).toBe('2');
  expect(sql(`SELECT COUNT(*) FROM meta.ModelSnapshot WHERE ImportId = ${applyId} AND ContentJson LIKE '%nota antigua%'`)).toBe('0');

  // El contrato y su registro volvieron; Notas se archivó (sus datos se conservan).
  const admin = await as('admin');
  expect((await (await admin.get(`/api/data/RbContrato/${contratoId}`)).json()).Clave).toBe('C1');
  expect((await (await admin.get(`/api/data/RbProveedor/${proveedorId}`)).json()).Notas).toBeUndefined();
});

test('H2 CA1 · CA2 · CA7: revertir desde el diálogo crea una solicitud con el diff inverso', async ({ page }) => {
  await page.goto('/admin/environments');
  await page.getByRole('button', { name: 'Revisar Contratos v1.0.0' }).click();
  const dialog = page.getByRole('dialog', { name: 'Contratos v1.0.0', exact: true });
  await expect(dialog).toContainText('Instantánea antes de aplicar');
  await dialog.getByRole('button', { name: 'Revertir la instalación' }).click();

  const review = page.getByRole('dialog', { name: 'Reversión de Contratos v1.0.0' });
  await expect(review).toContainText('1.0.0 → Sin instalar');
  await expect(review).toContainText('Pedida por admin');
  await expect(review).toContainText("Archivar catálogo 'Contrato RB'");
  await expect(review).toContainText("Archivar catálogo 'Línea de contrato RB'");
  await expect(review).toContainText("Reactivar campo 'Notas' de 'Proveedor RB'");
  await expect(review).toContainText("Archivar campo 'Teléfono'");
  await expect(review).toContainText('Se archivan los catálogos que creó el paquete');
  // H2 CA7: los roles del paquete se quedan.
  await expect(review).toContainText('Los roles del paquete (Gestor17) y sus permisos se conservan');
  // Quien la pidió no la aprueba (cuatro ojos).
  await expect(review.getByRole('button', { name: 'Aprobar' })).toHaveCount(0);
  await expect(review.getByRole('button', { name: 'Aplicar reversión' })).toBeDisabled();
  await expect(review).toContainText('requiere que la apruebe un administrador distinto de quien la pidió');
  await expectAccessible(page);
  // Regresión (revisión exploratoria 017, F-02): el enlace a la aplicación se alinea con su etiqueta «Deshace».
  const link = review.getByRole('button', { name: `Ver la aplicación #${applyId}` });
  const labelBox = (await review.getByText('Deshace', { exact: true }).boundingBox())!;
  // Se mide dónde empieza el texto (no la caja del botón: el relleno lo corría a la derecha).
  const textX = await link.evaluate((el) => { const r = document.createRange(); r.selectNodeContents(el); return r.getBoundingClientRect().x; });
  expect(Math.abs(textX - labelBox.x)).toBeLessThanOrEqual(1);
  await link.click();
  await expect(page.getByRole('dialog', { name: 'Contratos v1.0.0', exact: true })).toContainText('Ya hay una reversión de este paquete en revisión');
  await page.keyboard.press('Escape');

  await expect(page.getByRole('row', { name: /Reversión de Contratos/ })).toContainText('1.0.0 → sin instalar');
  const list = await (await (await as('admin')).get('/api/admin/environments/imports')).json();
  revertId = list.find((x: { kind: string; revertOf: number }) => x.kind === 'revert' && x.revertOf === applyId).id;
  const r = await detail(revertId);
  expect(r).toMatchObject({ status: 'pending', kind: 'revert', targetVersion: null, canApprove: false, canApply: false });
  expect(r.diff.roles).toEqual([]); // revertir nunca otorga roles ni permisos
  expect(r.snapshot.beforeHash).toBe((await detail(applyId)).snapshot.beforeHash);
  // Una sola reversión en revisión por paquete.
  expect((await post('admin', `${applyId}/revert`)).status()).toBe(409);
  expect((await audit('environment.revertRequest')).items[0].after).toMatchObject({ import: revertId, revertOf: applyId, version: '1.0.0' });
});

test('H3 CA1 · H2 CA5: la aprueba otra persona; si el entorno cambia, se advierte y se revisa de nuevo', async () => {
  const r = await detail(revertId);
  // Quien la pidió no la aprueba.
  expect((await post('admin', `${revertId}/approve`, { diffHash: r.diff.diffHash })).status()).toBe(403);
  expect((await post('aprobador', `${revertId}/approve`, { diffHash: r.diff.diffHash, comment: 'ok' })).status()).toBe(200);

  // Deriva: alguien agrega un campo al proveedor después de aplicar.
  await applyPlan(await as('admin'), { description: 'E2E 017: deriva', operations: [
    { op: 'addField', entity: 'RbProveedor', field: { name: 'Extra', label: 'Dato extra', type: 'text' } },
  ] });
  const stale = await post('admin', `${revertId}/apply`, { diffHash: r.diff.diffHash });
  expect(stale.status()).toBe(409);
  expect(await stale.text()).toContain('El entorno cambió');
  const now = await detail(revertId);
  expect(now.canApply).toBe(false);
  expect(now.blockedReason).toContain('El entorno cambió desde la aprobación');
  expect(now.diff.warnings.join(' ')).toContain('«Proveedores RB» cambió en este entorno después de aplicar el paquete');
  expect(now.diff.localArchives).toEqual(['«Proveedor RB» › Dato extra se agregó en este entorno después del paquete: se archiva (sus datos se conservan).']);

  // Se rechaza con motivo y se pide otra vez.
  expect((await post('aprobador', `${revertId}/reject`, { comment: 'El entorno cambió' })).status()).toBe(200);
  const again = await post('admin', `${applyId}/revert`);
  expect(again.status(), await again.text()).toBe(200);
  revertId = (await again.json()).id;
  const fresh = await detail(revertId);
  // Los campos agregados después se confirman aparte (014).
  expect((await post('aprobador', `${revertId}/approve`, { diffHash: fresh.diff.diffHash })).status()).toBe(409);
  expect((await post('aprobador', `${revertId}/approve`, { diffHash: fresh.diff.diffHash, confirmArchive: true })).status()).toBe(200);
});

test('H2 CA3 · CA6 · H3 CA2: aplicar la reversión desde la UI archiva lo creado, reactiva lo archivado y regresa la versión', async ({ page }) => {
  await page.goto('/admin/environments');
  await page.getByRole('button', { name: 'Revisar Reversión de Contratos v1.0.0' }).first().click();
  const review = page.getByRole('dialog', { name: 'Reversión de Contratos v1.0.0' });
  await expect(review).toContainText('Aprobado por ' + APPROVER);
  const apply = review.getByRole('button', { name: 'Aplicar reversión' });
  await expect(apply).toBeDisabled(); // falta confirmar el campo agregado después
  await review.getByText('Entiendo que este campo se archivará').click();
  await apply.click();
  await expect(review).toContainText('Reversión aplicada');
  await expect(review).toContainText('El paquete dejó de estar instalado');

  const admin = await as('admin');
  // Lo creado se archivó con su registro (no se borró); lo archivado volvió con su dato.
  expect((await admin.get(`/api/data/RbContrato/${contratoId}`)).status()).toBe(404);
  expect(sql(`SELECT Clave FROM app.RbContrato WHERE Id = ${contratoId}`)).toBe('C1');
  expect((await model('RbContrato')).isArchived).toBe(true);
  expect((await model('RbContratoLinea')).isArchived).toBe(true);
  const proveedor = await model('RbProveedor');
  expect(field(proveedor, 'Notas')?.isArchived).toBeFalsy();
  expect(field(proveedor, 'Telefono')?.isArchived).toBe(true);
  expect(field(proveedor, 'Extra')?.isArchived).toBe(true);
  expect(field(proveedor, 'Nombre')?.label).toBe('Nombre');
  expect((await (await admin.get(`/api/data/RbProveedor/${proveedorId}`)).json()).Notas).toBe('nota antigua');

  // Versión: el paquete ya no figura como instalado; la aplicación queda Revertida y apunta a su reversión.
  const info = await (await admin.get('/api/admin/environments')).json();
  expect(info.packages.map((p: { id: string }) => p.id)).not.toContain('rb-contratos');
  expect(await detail(applyId)).toMatchObject({ status: 'reverted', revertedBy: revertId, canRevert: false });
  const done = await detail(revertId);
  expect(done).toMatchObject({ status: 'applied', previousVersion: '1.0.0', reviewedBy: APPROVER, appliedBy: 'admin' });
  expect(done.changeSetId).toBeGreaterThan(0);
  const entry = (await audit('environment.revert')).items[0];
  expect(entry.before).toEqual({ version: '1.0.0' });
  expect(entry.after).toMatchObject({ import: revertId, revertOf: applyId, version: null, changeSetId: done.changeSetId });
  expect(entry.after.localArchives).toEqual([expect.stringContaining('Dato extra')]);
  // Los roles del paquete siguen ahí (H2 CA7).
  expect((await (await admin.get('/api/admin/roles')).json()).map((r: { name: string }) => r.name)).toContain('Gestor17');

  await page.keyboard.press('Escape');
  await expect(page.getByRole('row', { name: /Contratos.*1\.0\.0.*Revertido/ })).toBeVisible();
});

test('H2 CA1: lo revertido y la reversión ya no se revierten; volver a subir el paquete lo reactiva todo', async () => {
  expect(await (await post('admin', `${applyId}/revert`)).text()).toContain('Solo se revierte un paquete aplicado');
  expect(await (await post('admin', `${revertId}/revert`)).text()).toContain('Una reversión no se revierte');

  const res = await upload(paquete);
  expect(res.status(), await res.text()).toBe(200);
  const id = (await res.json()).id;
  const d = await detail(id);
  expect(d.diff.valid).toBe(true);
  const steps = d.diff.steps.map((s: { description: string }) => s.description).join(' | ');
  expect(steps).toContain("Reactivar catálogo 'Contrato RB'");
  expect((await post('aprobador', `${id}/approve`, { diffHash: d.diff.diffHash, confirmArchive: true })).status()).toBe(200);
  expect((await post('admin', `${id}/apply`, { diffHash: d.diff.diffHash, confirmArchive: true })).status()).toBe(200);
  expect((await (await (await as('admin')).get(`/api/data/RbContrato/${contratoId}`)).json()).Clave).toBe('C1');
  expect(await detail(id)).toMatchObject({ status: 'applied', canRevert: true });
  applyId = id;
});

test('H2 CA4: lo que el plan no puede expresar es conflicto y bloquea la reversión', async ({ page }) => {
  // Un proceso agregado después de aplicar: la versión anterior no lo trae y un proceso no se quita.
  await applyPlan(await as('admin'), { description: 'E2E 017: proceso local', operations: [{ op: 'setWorkflow', entity: 'RbProveedor', workflow: {
    initial: 'Activo', states: [{ name: 'Activo', label: 'Activo' }], transitions: [] } }] });
  const res = await post('admin', `${applyId}/revert`);
  expect(res.status(), await res.text()).toBe(200);
  const id = (await res.json()).id;
  const d = await detail(id);
  expect(d.diff.valid).toBe(false);
  expect(d.diff.conflicts.join(' ')).toContain('«Proveedor RB»: la versión anterior no trae el proceso que tiene este entorno');
  expect((await post('aprobador', `${id}/approve`, { diffHash: d.diff.diffHash })).status()).toBe(409);

  await page.goto('/admin/environments');
  await page.getByRole('button', { name: 'Revisar Reversión de Contratos v1.0.0' }).first().click();
  const review = page.getByRole('dialog', { name: 'Reversión de Contratos v1.0.0' });
  await expect(review).toContainText('Conflictos: el plan no puede expresar estos cambios');
  // Regresión (revisión exploratoria 017, F-01): no pide corregir «puntos marcados» que no existen.
  await expect(review).toContainText('No se puede aplicar por los conflictos');
  await expect(review).not.toContainText('Corrige los puntos marcados');
  await expect(review.getByRole('button', { name: 'Aplicar reversión' })).toBeDisabled();
  await review.getByLabel('Comentario').fill('Conflicto con el proceso');
  await review.getByRole('button', { name: 'Rechazar' }).click();
  await expect(review).toContainText('Rechazado por admin');
});

test('H3 CA3: una instantánea alterada en la base no se ofrece ni se aplica', async () => {
  const r = await post('admin', `${applyId}/revert`);
  expect(r.status(), await r.text()).toBe(200);
  const pending = (await r.json()).id;
  const before = await detail(pending);
  expect(before.diff.diffHash).toMatch(/^[0-9a-f]{64}$/);
  // Alguien con acceso a la base cambia la instantánea (p. ej. para «revertir» a otra definición).
  // Sin comillas dobles en el argumento: en Windows sqlcmd las parte (NCHAR(34) es «"»).
  sql(`UPDATE meta.ModelSnapshot SET ContentJson = REPLACE(ContentJson, NCHAR(34) + N'Proveedores RB' + NCHAR(34), NCHAR(34) + N'Proveedores X' + NCHAR(34)) WHERE ImportId = ${applyId} AND Moment = 'before'`);
  const tampered = await detail(pending);
  expect(tampered.canApply).toBe(false);
  expect(tampered.diff ?? null).toBeNull();
  expect(tampered.blockedReason).toContain('no coincide con su huella');
  expect((await post('aprobador', `${pending}/approve`, { diffHash: before.diff.diffHash })).status()).toBe(409);
  expect((await detail(applyId)).revertBlockedReason).toContain('no coincide con su huella');
  expect((await post('aprobador', `${pending}/reject`, { comment: 'Instantánea alterada' })).status()).toBe(200);
});

test('H4 CA3: el modelador reactiva un campo y un catálogo archivados con vista previa', async ({ page }) => {
  const admin = await as('admin');
  await applyPlan(admin, { description: 'E2E 017: nota', operations: [
    { op: 'createEntity', name: 'RbNota', label: 'Nota RB', labelPlural: 'Notas RB', group: GROUP,
      fields: [{ name: 'Titulo', label: 'Título', type: 'text', required: true }, { name: 'Comentario', label: 'Comentario', type: 'text' }] },
  ] });
  const nota = (await (await admin.post('/api/data/RbNota', { data: { Titulo: 'Primera', Comentario: 'se conserva' } })).json()).id;
  await applyPlan(admin, { description: 'E2E 017: archivar comentario', operations: [{ op: 'archiveField', entity: 'RbNota', field: 'Comentario' }] });

  await page.goto('/admin/model');
  await page.getByRole('row', { name: /Notas RB/ }).getByRole('button', { name: 'Editar' }).click();
  await page.getByRole('button', { name: 'Reactivar campo Comentario' }).click();
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  let dialog = page.getByRole('dialog', { name: 'Revisar cambios al modelo' });
  await expect(dialog).toContainText("Reactivar campo 'Comentario' de 'Nota RB'");
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(dialog).toBeHidden();
  expect((await (await admin.get(`/api/data/RbNota/${nota}`)).json()).Comentario).toBe('se conserva');

  await applyPlan(admin, { description: 'E2E 017: archivar notas', operations: [{ op: 'archiveEntity', entity: 'RbNota' }] });
  await page.goto('/admin/model');
  await page.getByRole('row', { name: /Notas RB/ }).getByRole('button', { name: 'Reactivar Notas RB' }).click();
  dialog = page.getByRole('dialog', { name: 'Revisar cambios al modelo' });
  await expect(dialog).toContainText("Reactivar catálogo 'Nota RB' (vuelve con los registros que conservó)");
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('row', { name: /Notas RB/ })).toContainText('Activo');
  expect((await (await admin.get(`/api/data/RbNota/${nota}`)).json()).Titulo).toBe('Primera');
});

test('Reversión en móvil: el diálogo cabe y no hay scroll horizontal', async ({ browser }) => {
  const page = await pageAs(browser, APPROVER);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/admin/environments');
  await page.getByRole('button', { name: 'Revisar Reversión de Contratos v1.0.0' }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Reversión de Contratos v1.0.0' });
  await expect(dialog).toBeVisible();
  expect((await dialog.boundingBox())!.width).toBeLessThanOrEqual(390);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await page.context().close();
});
