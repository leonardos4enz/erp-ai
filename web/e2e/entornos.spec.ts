// Spec 009 · Entornos: exportar lo hecho en el modelador como paquete, revisar el diff contra el entorno y aplicarlo
// como actualización versionada, con aprobación de otro administrador y paquetes firmados.
// La suite corre como «producción» (playwright.config: Environments__RequireApproval y una clave de firma nueva por corrida).
// Un solo servidor hace de origen y de destino: entre exportar y subir, el modelo se cambia a mano (como un entorno que divergió).
import { readFileSync } from 'node:fs';
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de entornos 2026';
const GROUP = 'Entornos E2E';
const SECRET_URL = 'https://example.com/hooks/token-secreto-env9';

type Pkg = {
  id: string; version: string; source?: { environment: string }; signature?: string; seed: unknown[];
  plan: { operations: { op: string; name?: string }[] };
  roles: { name: string; canManageModel: boolean; canManageUsers: boolean; permissions: { entity: string; canDelete: boolean }[] }[];
};

const api: Record<string, APIRequestContext> = {};
const as = async (who: 'admin' | 'aprobador' | 'catalogador') =>
  (api[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(`e2e-env-${who}`, PWD));

async function pageAs(browser: Browser, user: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

async function exportFile(body: object): Promise<Buffer> {
  const res = await (await as('admin')).post('/api/admin/environments/export', { data: body });
  expect(res.status(), await res.text()).toBe(200);
  return res.body();
}

async function upload(who: 'admin' | 'aprobador', file: Buffer | string) {
  return (await as(who)).post('/api/admin/environments/imports', {
    data: typeof file === 'string' ? Buffer.from(file) : file, headers: { 'Content-Type': 'application/octet-stream' },
  });
}

const detail = async (id: number) => (await (await as('admin')).get(`/api/admin/environments/imports/${id}`)).json();
const entity = async (name: string) => (await (await as('admin')).get(`/api/model/entities/${name}`)).json();
const audit = async (action: string) => (await (await as('admin')).get('/api/audit', { params: { action, pageSize: '50' } })).json();

let catalogo: Buffer; // catalogo-env 1.0.0, exportado por la UI en H1

test('preparar: roles, catálogos con proceso, seguridad, vistas y webhook, y un segundo administrador', async () => {
  const admin = await as('admin');
  const ids: Record<string, number> = {};
  for (const [name, canManageModel] of [['Catalogador9', false], ['Integracion9', false], ['Modelador9', true], ['Lector9', false]] as const) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 009', canManageModel, canManageUsers: false, canViewAudit: false, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    ids[name] = (await res.json()).id;
  }
  const text = (name: string, label: string, extra = {}) => ({ name, label, type: 'text', ...extra });
  await applyPlan(admin, { description: 'E2E 009: catálogos', operations: [
    { op: 'createEntity', name: 'EnvMarca', label: 'Marca E2E', labelPlural: 'Marcas E2E', group: GROUP,
      fields: [text('Clave', 'Clave', { required: true, unique: true }), text('Nombre', 'Nombre'), text('Pais', 'País')] },
    { op: 'createEntity', name: 'EnvProducto', label: 'Producto E2E', labelPlural: 'Productos E2E', group: GROUP,
      fields: [text('Clave', 'Clave', { required: true, unique: true }), text('Nombre', 'Nombre'),
        { name: 'Marca', label: 'Marca', type: 'reference', referenceEntity: 'EnvMarca' }, { name: 'Precio', label: 'Precio', type: 'decimal' }] },
    { op: 'setWorkflow', entity: 'EnvProducto', workflow: { initial: 'Borrador',
      states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Activo', label: 'Activo', color: 'success' }],
      transitions: [{ name: 'Activar', label: 'Activar', from: ['Borrador'], to: 'Activo', roles: ['Catalogador9'] }] } },
    { op: 'setSecurity', entity: 'EnvProducto', security: { fields: [{ field: 'Precio', read: ['Catalogador9'] }] } },
    { op: 'setWebhooks', entity: 'EnvProducto', webhooks: [{ name: 'ProductoActivo', label: 'Producto activo',
      events: [{ on: 'enterState', state: 'Activo' }], role: 'Integracion9', fields: ['Clave', 'Nombre'] }] },
  ] });
  const perms = (entity: string, write = false) => ({ entity, canRead: true, canCreate: write, canUpdate: write, canDelete: false });
  const roles: Record<string, object[]> = {
    Catalogador9: [perms('EnvProducto', true), perms('EnvMarca')], Integracion9: [perms('EnvProducto')],
    Modelador9: [perms('EnvMarca')], Lector9: [perms('EnvMarca')],
  };
  for (const [name, permissions] of Object.entries(roles)) {
    const res = await admin.put(`/api/admin/roles/${ids[name]}`, { data: { name, description: 'E2E spec 009', canManageModel: name === 'Modelador9',
      canManageUsers: false, canViewAudit: false, permissions } });
    expect(res.status(), await res.text()).toBe(200);
  }

  // Datos que nunca deben viajar: un registro confidencial y el destino (con token) y secreto de un webhook.
  const marca = await (await admin.post('/api/data/EnvMarca', { data: { Clave: 'M1', Nombre: 'Marca Uno' } })).json();
  expect((await admin.post('/api/data/EnvMarca', { data: { Clave: 'M2', Nombre: 'Confidencial-E2E-9' } })).status()).toBe(201);
  await applyPlan(admin, { description: 'E2E 009: vistas', operations: [{ op: 'setViews', entity: 'EnvProducto', views: [
    { name: 'Activos', label: 'Activos', state: 'Activo' },
    { name: 'DeMarcaUno', label: 'De Marca Uno', filters: [{ field: 'Marca', op: 'eq', value: marca.id, label: 'Marca Uno' }] },
  ] }] });
  const hook = await admin.put('/api/admin/webhooks/EnvProducto/ProductoActivo', { data: { url: SECRET_URL, active: true } });
  expect(hook.status(), await hook.text()).toBe(200);
  (globalThis as { envHookSecret?: string }).envHookSecret = (await hook.json()).secret;

  for (const [user, role] of [['aprobador', 'Administrador'], ['catalogador', 'Catalogador9']] as const) {
    const res = await admin.post('/api/admin/users', { data: { username: `e2e-env-${user}`, displayName: `E2E ${user}`, password: PWD,
      roleIds: [await roleId(admin, role)] } });
    expect(res.status(), await res.text()).toBe(201);
  }
});

test('H1 CA1 · CA2 · CA4: exportar desde la UI con vista previa, roles, advertencias, firma y huella', async ({ page }) => {
  await page.goto('/admin/environments');
  await expect(page.getByLabel('Este entorno')).toContainText('Producción E2E');
  await expect(page.getByLabel('Este entorno')).toContainText('Requiere aprobación');
  await expect(page.getByLabel('Este entorno')).toContainText('Firma obligatoria');
  await page.getByRole('tab', { name: 'Exportar' }).click();
  // Regresión (verificación 009, F-02): las partidas de un documento (Compras, instalado por compras.spec) van por su etiqueta.
  await expect(page.getByText('Con sus partidas: Partidas de orden de compra')).toBeVisible();
  await page.getByLabel('Identificador').fill('catalogo-env');
  await page.getByRole('textbox', { name: /^Nombre/ }).fill('Catálogo de productos');
  await expect(page.getByLabel('Versión')).toHaveValue('1.0.0');
  await page.getByRole('checkbox', { name: 'Productos E2E' }).check();
  await page.getByRole('checkbox', { name: 'Marcas E2E' }).check();
  await expect(page.getByRole('button', { name: 'Descargar paquete' })).toBeDisabled();
  await page.getByRole('button', { name: 'Revisar paquete' }).click();

  const preview = page.getByRole('region', { name: 'Vista previa del paquete' });
  await expect(preview).toContainText('El plan es válido');
  await expect(preview).toContainText('Se firma con la clave de este entorno');
  await expect(preview).toContainText("Crear catálogo 'Producto E2E'");
  await expect(preview).toContainText('Catalogador9');
  // Regresión (verificación 009, F-03): los permisos se leen por catálogo, no por nombre técnico.
  await expect(preview).toContainText('Productos E2E (leer, crear, editar)');
  await expect(preview).toContainText('Integracion9');
  await expect(preview).toContainText('El rol «Modelador9» administra el modelo');
  await expect(preview).toContainText('filtra por un registro concreto');
  await expectAccessible(page);

  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Descargar paquete' }).click()]);
  expect(download.suggestedFilename()).toBe('catalogo-env-1.0.0.json');
  catalogo = readFileSync((await download.path())!);
  const pkg = JSON.parse(catalogo.toString()) as Pkg;
  expect(pkg).toMatchObject({ id: 'catalogo-env', version: '1.0.0', source: { environment: 'Producción E2E' }, seed: [] });
  expect(pkg.signature).toMatch(/^hmac-sha256:[0-9a-f]{64}$/);
  expect(pkg.plan.operations.filter((o) => o.op === 'createEntity').map((o) => o.name).sort()).toEqual(['EnvMarca', 'EnvProducto']);
  // Mismo formato que modules/*.json; la capacidad de administrar el modelo no viaja.
  expect(pkg.roles.map((r) => r.name).sort()).toEqual(['Catalogador9', 'Integracion9', 'Lector9', 'Modelador9']);
  expect(pkg.roles.find((r) => r.name === 'Modelador9')!.canManageModel).toBe(false);

  // La exportación queda en el historial y en la auditoría.
  await expect(page.getByRole('region', { name: 'Exportaciones recientes' })).toContainText('catalogo-env');
  const exp = await audit('environment.export');
  expect(exp.items[0].after).toMatchObject({ id: 'catalogo-env', version: '1.0.0' });
});

test('H1 CA3: el paquete no lleva datos, usuarios ni secretos', async () => {
  const text = catalogo.toString();
  for (const leak of ['Confidencial-E2E-9', 'token-secreto-env9', 'example.com', (globalThis as { envHookSecret?: string }).envHookSecret!,
    'e2e-env-aprobador', ADMIN.password, 'PasswordHash', 'meta.'])
    expect(text, `no debe contener «${leak}»`).not.toContain(leak);
  // El webhook sí viaja (qué y con qué rol), sin destino.
  expect(text).toContain('ProductoActivo');
});

test('H5 CA3: un paquete alterado o sin firma se rechaza antes de guardarse, y el intento se audita', async () => {
  const before = (await audit('environment.rejected')).total;
  const pkg = JSON.parse(catalogo.toString()) as Pkg;
  pkg.roles.find((r) => r.name === 'Integracion9')!.permissions[0].canDelete = true; // escalar en el camino
  const tampered = await upload('admin', JSON.stringify(pkg, null, 2));
  expect(tampered.status()).toBe(400);
  expect((await tampered.json()).title).toContain('La firma del paquete no es válida');

  delete pkg.signature;
  const unsigned = await upload('admin', JSON.stringify(pkg));
  expect(unsigned.status()).toBe(400);
  expect((await unsigned.json()).title).toContain('no trae firma');

  expect((await upload('admin', 'esto no es json')).status()).toBe(400);
  expect((await audit('environment.rejected')).total).toBe(before + 3);
  const list = await (await (await as('admin')).get('/api/admin/environments/imports')).json();
  expect(list.filter((i: { packageId: string }) => i.packageId === 'catalogo-env')).toEqual([]);
});

test('H2 CA1 · CA2 · CA4 + H4 CA1: diff contra un entorno que divergió; quien sube no aprueba; otro administrador aprueba y aplica', async ({ page, browser }) => {
  const admin = await as('admin');
  // El entorno diverge del paquete: una etiqueta cambiada y un campo local.
  await applyPlan(admin, { description: 'E2E 009: cambios locales', operations: [
    { op: 'updateField', entity: 'EnvProducto', field: 'Nombre', label: 'Nombre corto' },
    { op: 'addField', entity: 'EnvProducto', field: { name: 'NotaLocal', label: 'Nota local', type: 'text' } },
  ] });

  await page.goto('/admin/environments');
  await page.getByLabel('Archivo del paquete').setInputFiles({ name: 'catalogo-env-1.0.0.json', mimeType: 'application/json', buffer: catalogo });
  const dialog = page.getByRole('dialog', { name: 'Catálogo de productos v1.0.0' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('Firmado y verificado');
  await expect(dialog).toContainText('Sin instalar → 1.0.0');
  await expect(dialog).toContainText("Actualizar campo 'Nombre' de 'Producto E2E'");
  await expect(dialog).toContainText("Archivar campo 'Nota local'");
  // Spec 014 (H4): el campo local se lista aparte, con su casilla, y no entre las advertencias.
  const archives = dialog.getByRole('region', { name: 'Campos de este entorno que se archivarán' });
  await expect(archives).toContainText('Nota local no viene en el paquete: se archiva');
  // Regresión (verificación 014, F-02): quien subió no puede aprobar ni aplicar, así que no se le pide confirmar.
  await expect(archives.getByRole('checkbox')).toHaveCount(0);
  // Los roles que ya existen y no reciben permisos no se listan: no cambian.
  await expect(dialog.getByRole('region', { name: 'Roles y permisos' })).toHaveCount(0);
  // Cuatro ojos: quien subió no ve «Aprobar» y no puede aplicar.
  await expect(dialog.getByRole('button', { name: 'Aprobar' })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Aplicar paquete' })).toBeDisabled();
  await expect(dialog).toContainText('lo apruebe un administrador distinto de quien lo subió');
  await expectAccessible(page);
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);

  const id = (await (await admin.get('/api/admin/environments/imports')).json())[0].id as number;
  const d = await detail(id);
  expect(d.status).toBe('pending');
  expect((await admin.post(`/api/admin/environments/imports/${id}/approve`, { data: { diffHash: d.diff.diffHash } })).status()).toBe(403);
  const early = await admin.post(`/api/admin/environments/imports/${id}/apply`, { data: { diffHash: d.diff.diffHash } });
  expect(early.status()).toBe(409);
  expect((await early.json()).title).toContain('apruebe');

  // Spec 014 (H4 CA2): sin confirmar los campos locales, ni aprobar ni aplicar (antes se aprobaba con solo un aviso).
  const unconfirmed = await (await as('aprobador')).post(`/api/admin/environments/imports/${id}/approve`, { data: { diffHash: d.diff.diffHash } });
  expect(unconfirmed.status()).toBe(409);
  expect((await unconfirmed.json()).title).toContain('1 campo que solo existe en este entorno');
  expect(d.diff.localArchives).toEqual([expect.stringContaining('Nota local')]);

  // Otro administrador revisa, confirma el campo local, aprueba con comentario y aplica.
  const other = await pageAs(browser, 'e2e-env-aprobador');
  await other.goto('/admin/environments');
  await other.getByRole('button', { name: 'Revisar Catálogo de productos v1.0.0' }).click();
  const review = other.getByRole('dialog', { name: 'Catálogo de productos v1.0.0' });
  await review.getByLabel('Comentario').fill('Revisado contra pruebas');
  await expect(review.getByRole('button', { name: 'Aprobar' })).toBeDisabled();
  await review.getByRole('checkbox', { name: 'Entiendo que este campo se archivará' }).check();
  await review.getByRole('button', { name: 'Aprobar' }).click();
  await expect(other.getByText('Paquete aprobado')).toBeVisible();
  await expect(review).toContainText('Aprobado por e2e-env-aprobador');
  await review.getByRole('button', { name: 'Aplicar paquete' }).click();
  await expect(review.getByText('Paquete aplicado')).toBeVisible(); // aviso del diálogo, además del toast
  await other.context().close();

  // El modelo quedó como el paquete; el campo local se archivó (sus datos se conservan).
  const def = await entity('EnvProducto');
  expect(def.fields.find((f: { name: string }) => f.name === 'Nombre').label).toBe('Nombre');
  expect(def.fields.some((f: { name: string; isArchived?: boolean }) => f.name === 'NotaLocal' && !f.isArchived)).toBe(false);
  const after = await detail(id);
  expect(after).toMatchObject({ status: 'applied', reviewedBy: 'e2e-env-aprobador', appliedBy: 'e2e-env-aprobador', reviewComment: 'Revisado contra pruebas' });
  const info = await (await admin.get('/api/admin/environments')).json();
  expect(info.packages).toContainEqual(expect.objectContaining({ id: 'catalogo-env', version: '1.0.0' }));
});

test('H3 CA1 · CA3 + H4 CA3: aplicado como versión, sin repetirse, con auditoría completa', async ({ page }) => {
  const again = await upload('admin', catalogo);
  expect(again.status()).toBe(400);
  expect((await again.json()).title).toContain('no es mayor que la aplicada (1.0.0)');

  const changes = await (await (await as('admin')).get('/api/model/changes')).json();
  expect(changes[0].description).toContain('Catálogo de productos 1.0.0 (paquete catalogo-env)');
  const apply = (await audit('environment.apply')).items[0];
  expect(apply.after).toMatchObject({ version: '1.0.0' });
  expect(apply.after.diffHash).toMatch(/^[0-9a-f]{64}$/);
  expect((await audit('environment.approve')).items[0].user).toBe('e2e-env-aprobador');
  expect((await audit('environment.upload')).items[0].user).toBe('admin');

  await page.goto('/admin/environments');
  const row = page.getByRole('row', { name: /Catálogo de productos/ });
  await expect(row).toContainText('Aplicado');
  // La siguiente exportación de ese paquete sugiere la versión siguiente.
  await page.getByRole('tab', { name: 'Exportar' }).click();
  await page.getByLabel('Identificador').fill('catalogo-env');
  await expect(page.getByLabel('Versión')).toHaveValue('1.1.0');
});

test('H4 CA2 + H5 CA2: la aprobación queda ligada al diff; un rol nuevo llega con sus permisos', async () => {
  const admin = await as('admin');
  // Regresión (verificación 009, F-01): exportar solo Marcas fallaba porque la vista previa revisaba Productos (que no viaja)
  // con solo los roles del paquete, y su webhook usa Integracion9.
  const marcas = await exportFile({ id: 'marcas-env', name: 'Marcas', version: '1.0.0', entities: ['EnvMarca'], dashboards: [] });
  const pkg = JSON.parse(marcas.toString()) as Pkg;
  expect(pkg.roles.map((r) => r.name)).toContain('Lector9');

  // En «este» entorno el rol Lector9 aún no existe (se renombra): el paquete lo creará.
  const roles = await (await admin.get('/api/admin/roles')).json();
  const lector = roles.find((r: { name: string }) => r.name === 'Lector9');
  expect((await admin.put(`/api/admin/roles/${lector.id}`, { data: { ...lector, name: 'Lector9-anterior' } })).status()).toBe(200);

  const up = await upload('admin', marcas);
  expect(up.status(), await up.text()).toBe(200);
  const { id } = await up.json();
  const d = await detail(id);
  const role = d.diff.roles.find((r: { name: string }) => r.name === 'Lector9');
  expect(role).toMatchObject({ isNew: true, permissions: [expect.objectContaining({ entity: 'EnvMarca', canRead: true })] });
  // Los roles existentes no reciben permisos sobre catálogos existentes.
  expect(d.diff.roles.find((r: { name: string }) => r.name === 'Catalogador9').permissions).toEqual([]);

  const aprobador = await as('aprobador');
  expect((await aprobador.post(`/api/admin/environments/imports/${id}/approve`, { data: { diffHash: d.diff.diffHash } })).status()).toBe(200);

  // El entorno cambia después de aprobar: la aprobación ya no sirve.
  await applyPlan(admin, { description: 'E2E 009: cambio tras aprobar', operations: [
    { op: 'addField', entity: 'EnvMarca', field: { name: 'Sitio', label: 'Sitio web', type: 'text' } }] });
  const changed = await detail(id);
  expect(changed.canApply).toBe(false);
  expect(changed.blockedReason).toContain('cambió desde la aprobación');
  const late = await admin.post(`/api/admin/environments/imports/${id}/apply`, { data: { diffHash: changed.diff.diffHash } });
  expect(late.status()).toBe(409);
  expect((await late.json()).title).toContain('cambió desde la aprobación');

  // Se rechaza con motivo y se vuelve a subir: ahora sí.
  expect((await admin.post(`/api/admin/environments/imports/${id}/reject`, { data: { comment: '' } })).status()).toBe(400);
  const rejected = await admin.post(`/api/admin/environments/imports/${id}/reject`, { data: { comment: 'El entorno cambió' } });
  expect((await rejected.json()).status).toBe('rejected');
  const again = await (await upload('admin', marcas)).json();
  const fresh = await detail(again.id);
  expect(fresh.diff.localArchives.join(' ')).toContain('Sitio web no viene en el paquete');
  expect(fresh.diff.warnings.join(' ')).not.toContain('Sitio web');
  expect((await aprobador.post(`/api/admin/environments/imports/${again.id}/approve`, { data: { diffHash: fresh.diff.diffHash, comment: 'ok', confirmArchive: true } })).status()).toBe(200);
  // Aplicar también lo pide (quien aplica puede no ser quien aprobó).
  const blind = await aprobador.post(`/api/admin/environments/imports/${again.id}/apply`, { data: { diffHash: fresh.diff.diffHash } });
  expect(blind.status()).toBe(409);
  const applied = await aprobador.post(`/api/admin/environments/imports/${again.id}/apply`, { data: { diffHash: fresh.diff.diffHash, confirmArchive: true } });
  expect(applied.status(), await applied.text()).toBe(200);
  expect((await audit('environment.apply')).items[0].after.localArchives).toEqual([expect.stringContaining('Sitio web')]);

  const now = await (await admin.get('/api/admin/roles')).json();
  const created = now.find((r: { name: string }) => r.name === 'Lector9');
  expect(created).toMatchObject({ canManageModel: false, canManageUsers: false });
  expect(created.permissions).toEqual([expect.objectContaining({ entity: 'EnvMarca', canRead: true, canCreate: false })]);
  expect((await audit('environment.apply')).items[0].after.rolesCreated).toEqual(['Lector9']);
});

test('H2 CA3: lo que el plan no puede expresar es conflicto y bloquea', async ({ page }) => {
  const admin = await as('admin');
  const marcas = await exportFile({ id: 'marcas-env', name: 'Marcas', version: '1.1.0', entities: ['EnvMarca'], dashboards: [] });
  // Spec 017: reactivar un campo archivado ya no es conflicto (se prueba en rollback.spec); quitar un proceso, sí.
  await applyPlan(admin, { description: 'E2E 009: proceso local', operations: [{ op: 'setWorkflow', entity: 'EnvMarca', workflow: {
    initial: 'Activa', states: [{ name: 'Activa', label: 'Activa' }], transitions: [] } }] });
  const { id } = await (await upload('admin', marcas)).json();
  const d = await detail(id);
  expect(d.diff.valid).toBe(false);
  expect(d.diff.conflicts.join(' ')).toContain('«Marca E2E»: el paquete no trae el proceso que tiene este entorno');
  expect(d.canApply).toBe(false);
  const approve = await (await as('aprobador')).post(`/api/admin/environments/imports/${id}/approve`, { data: { diffHash: d.diff.diffHash } });
  expect(approve.status()).toBe(409);

  await page.goto('/admin/environments');
  await page.getByRole('button', { name: 'Revisar Marcas v1.1.0' }).click();
  const dialog = page.getByRole('dialog', { name: 'Marcas v1.1.0' });
  await expect(dialog).toContainText('Conflictos: el plan no puede expresar estos cambios');
  await expect(dialog.getByRole('button', { name: 'Aplicar paquete' })).toBeDisabled();
  await dialog.getByLabel('Comentario').fill('Conflicto con el proceso');
  await dialog.getByRole('button', { name: 'Rechazar' }).click();
  await expect(dialog).toContainText('Rechazado por admin');
});

test('H4 CA4: solo el superusuario ve y usa Entornos', async ({ browser }) => {
  const catalogador = await as('catalogador');
  for (const path of ['/api/admin/environments', '/api/admin/environments/imports', '/api/admin/environments/exportable'])
    expect((await catalogador.get(path)).status(), path).toBe(403);
  expect((await catalogador.post('/api/admin/environments/imports', { data: catalogo, headers: { 'Content-Type': 'application/octet-stream' } })).status()).toBe(403);
  expect((await catalogador.post('/api/admin/environments/export', { data: { id: 'x-y', name: 'x', version: '1.0.0', entities: ['EnvMarca'], dashboards: [] } })).status()).toBe(403);

  const page = await pageAs(browser, 'e2e-env-catalogador');
  await expect(page.getByLabel('Navegación principal').getByRole('link', { name: 'Entornos', exact: true })).toHaveCount(0);
  await page.goto('/admin/environments');
  await expect(page.getByText('Sin acceso')).toBeVisible();
  await page.context().close();
});

test('Entornos en móvil: sin scroll horizontal y el diálogo cabe', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/admin/environments');
  await expect(page.getByRole('row', { name: /Catálogo de productos/ })).toBeVisible();
  const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(await overflow()).toBeLessThanOrEqual(0);
  // Regresión (verificación 009, F-04): las insignias no aplastan «Este entorno» en dos renglones, en ningún ancho de móvil.
  const label = page.getByLabel('Este entorno').getByText('Este entorno', { exact: true });
  for (let width = 320; width <= 760; width += 20) {
    await page.setViewportSize({ width, height: 844 });
    expect((await label.boundingBox())!.height, `a ${width} px`).toBeLessThan(30);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Revisar Catálogo de productos v1.0.0' }).click();
  const dialog = page.getByRole('dialog', { name: 'Catálogo de productos v1.0.0' });
  await expect(dialog).toBeVisible();
  const box = await dialog.boundingBox();
  expect(box!.width).toBeLessThanOrEqual(390);
  expect(await overflow()).toBeLessThanOrEqual(0);
  await page.keyboard.press('Escape');
  await page.getByRole('tab', { name: 'Exportar' }).click();
  await expect(page.getByLabel('Identificador')).toBeVisible();
  expect(await overflow()).toBeLessThanOrEqual(0);
});
