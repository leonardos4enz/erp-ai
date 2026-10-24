// Spec 006 · Seguridad por campo y por registro. Corre después de compras.spec.ts e inventario.spec.ts
// (Compras 1.3.0 e Inventario 1.1.0 ya instalados, con sus usuarios). El motor se prueba con un catálogo
// propio (Oportunidades) declarado por plan; lo que ve una persona, por la UI.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de seguridad 2026';
const COMPRAS_PWD = 'frase larga de compras 2026'; // usuarios creados por compras.spec.ts
const USERS = {
  vend1: { role: 'Vendedor', unit: 'Norte' },
  vend2: { role: 'Vendedor', unit: 'Sur' },
  sup: { role: 'Supervisor de ventas', unit: 'Norte' },
  gerente: { role: 'Gerente de ventas', unit: null },
} as const;
type Who = keyof typeof USERS;
const username = (w: Who) => `e2e-sec-${w}`;

type Row = { id: number; _rowVersion: string; [k: string]: unknown };
const api: Partial<Record<Who | 'admin', APIRequestContext>> = {};
const as = async (w: Who | 'admin') =>
  (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(username(w), PWD));
const userIds: Partial<Record<Who, number>> = {};

async function pageAs(browser: Browser, user: string, pwd = PWD): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, user, pwd);
  return page;
}

async function json(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Row & { items: Row[]; total: number } };
}

const create = async (w: Who, entity: string, data: object) => json(await (await as(w)).post(`/api/data/${entity}`, { data }));
const get = async (w: Who, entity: string, id: number) => json(await (await as(w)).get(`/api/data/${entity}/${id}`));
const put = async (w: Who, row: Row, data: object, entity = 'Oportunidad') =>
  json(await (await as(w)).put(`/api/data/${entity}/${row.id}`, { data: { _rowVersion: row._rowVersion, ...data } }));
const transition = async (w: Who, row: Row, name: string, values?: object) =>
  json(await (await as(w)).post(`/api/data/Oportunidad/${row.id}/transition`, { data: { transition: name, _rowVersion: row._rowVersion, values } }));
const list = async (w: Who, params: Record<string, string | number> = {}) =>
  (await json(await (await as(w)).get('/api/data/Oportunidad', { params: { pageSize: 200, ...params } }))).body;

const roleNames = [...new Set(Object.values(USERS).map((u) => u.role))];
const SALES = ['Supervisor de ventas', 'Gerente de ventas'];

const security = {
  owner: 'Responsable',
  fields: [
    { field: 'Margen', read: SALES },
    { field: 'NotaInterna', read: SALES },
    { field: 'Prioridad', edit: ['Supervisor de ventas'] },
    { field: 'Descuento', editableIn: ['Abierta'] },
    { field: 'MotivoPerdida', editableIn: [] },
  ],
  records: [
    { roles: ['Vendedor'], scope: 'own' },
    { roles: ['Vendedor'], where: "Publica AND Estado IN ('Abierta', 'EnRevision')", access: 'read' },
    { roles: ['Supervisor de ventas'], scope: 'unit' },
    { roles: ['Gerente de ventas'], access: 'read' },
  ],
};

const ids: { o1: Row; o2: Row; pub: Row } = { o1: null!, o2: null!, pub: null! };

test('preparar: roles, catálogo con política de seguridad y usuarios con unidad', async () => {
  const admin = await as('admin');
  const flags = { canManageModel: false, canManageUsers: false, canViewAudit: false };
  for (const name of roleNames) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 006', ...flags, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
  }

  await applyPlan(admin, { description: 'Spec 006 · oportunidades', operations: [
    { op: 'createEntity', name: 'Oportunidad', label: 'Oportunidad', labelPlural: 'Oportunidades', icon: 'target', displayField: 'Folio', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'OP-', padding: 4 },
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Responsable', label: 'Responsable', type: 'user', defaultValue: 'CURRENTUSER()' },
      { name: 'Prioridad', label: 'Prioridad', type: 'choice', options: ['Normal', 'Alta'], defaultValue: "'Normal'" },
      { name: 'Margen', label: 'Margen', type: 'decimal', showInList: true },
      { name: 'NotaInterna', label: 'Nota interna', type: 'text' },
      { name: 'Descuento', label: 'Descuento', type: 'decimal' },
      { name: 'Publica', label: 'Pública', type: 'boolean', defaultValue: 'FALSE' },
      { name: 'MotivoPerdida', label: 'Motivo de pérdida', type: 'longtext' },
    ] },
    { op: 'createEntity', name: 'OportunidadPartida', label: 'Partida', labelPlural: 'Partidas', fields: [
      { name: 'Oportunidad', label: 'Oportunidad', type: 'parent', referenceEntity: 'Oportunidad' },
      { name: 'Concepto', label: 'Concepto', type: 'text', required: true },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal', defaultValue: '1' },
      { name: 'Costo', label: 'Costo', type: 'decimal' },
    ] },
    { op: 'addField', entity: 'Oportunidad', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'OportunidadPartida' } },
    { op: 'setWorkflow', entity: 'Oportunidad', workflow: {
      initial: 'Abierta',
      states: [
        { name: 'Abierta', label: 'Abierta' }, { name: 'EnRevision', label: 'En revisión', color: 'warning' },
        { name: 'Ganada', label: 'Ganada', color: 'success', editable: false }, { name: 'Perdida', label: 'Perdida', color: 'danger', editable: false },
      ],
      transitions: [
        { name: 'revisar', label: 'Enviar a revisión', from: ['Abierta'], to: 'EnRevision' },
        { name: 'ganar', label: 'Ganar', from: ['EnRevision'], to: 'Ganada', roles: ['Gerente de ventas'] },
        { name: 'perder', label: 'Perder', from: ['EnRevision'], to: 'Perdida', roles: ['Gerente de ventas', 'Vendedor'], requiredFields: ['MotivoPerdida'] },
      ],
    } },
    { op: 'setSecurity', entity: 'Oportunidad', security },
    { op: 'setSecurity', entity: 'OportunidadPartida', security: { fields: [
      { field: 'Costo', read: SALES }, { field: 'Cantidad', editableIn: ['Abierta'] },
    ] } },
    // Cotización: referencia a la oportunidad; al crearse, una automatización toca un campo oculto del destino.
    { op: 'createEntity', name: 'Cotizacion', label: 'Cotización', labelPlural: 'Cotizaciones', icon: 'file-text', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'COT-', padding: 4 },
      { name: 'Oportunidad', label: 'Oportunidad', type: 'reference', referenceEntity: 'Oportunidad', required: true },
      { name: 'NombreOportunidad', label: 'De la oportunidad', type: 'text', defaultValue: 'Oportunidad.Nombre' },
    ] },
    { op: 'setAutomations', entity: 'Cotizacion', automations: [
      { name: 'SubirMargen', label: 'Sumar un punto de margen', on: 'create', actions: [
        { type: 'update', target: 'Oportunidad', set: [{ field: 'Margen', value: 'COALESCE(Oportunidad.Margen, 0) + 1' }] },
      ] },
    ] },
  ] });

  const perms = (entity: string, write: boolean) => ({ entity, canRead: true, canCreate: write, canUpdate: write, canDelete: write });
  const roles = await (await admin.get('/api/admin/roles')).json();
  for (const name of roleNames) {
    const r = roles.find((x: { name: string }) => x.name === name);
    const write = name !== 'Gerente de ventas';
    const res = await admin.put(`/api/admin/roles/${r.id}`, { data: { name, description: r.description, ...flags,
      permissions: [perms('Oportunidad', write), perms('Cotizacion', true)] } });
    expect(res.status(), await res.text()).toBe(200);
  }
  for (const [w, u] of Object.entries(USERS) as [Who, (typeof USERS)[Who]][]) {
    const res = await admin.post('/api/admin/users', { data: {
      username: username(w), displayName: `${w} E2E`, password: PWD, roleIds: [await roleId(admin, u.role)], unit: u.unit,
    } });
    expect(res.status(), await res.text()).toBe(201);
    const body = await res.json();
    expect(body.unit).toBe(u.unit);
    userIds[w] = body.id;
  }
  // H2 CA6: la unidad viaja en /auth/me.
  expect((await (await (await as('vend1')).get('/api/auth/me')).json()).unit).toBe('Norte');
});

test('H1 CA5 · H5: la vista previa rechaza una política inválida', async () => {
  const res = await (await as('admin')).post('/api/model/plan/preview', { data: { description: 'x', operations: [
    { op: 'setSecurity', entity: 'Oportunidad', security: {
      fields: [{ field: 'Folio', read: ['Vendedor'] }, { field: 'Nombre', edit: ['Gerente de ventas'] }, { field: 'Descuento', editableIn: ['Cerrada'] }],
      records: [{ roles: ['Fantasma'], where: 'Responsable.Nombre = CURRENTUSER()' }],
    } },
  ] } });
  const errors = ((await res.json()).errors as { message: string }[]).map((e) => e.message).join(' | ');
  expect(errors).toContain('es el campo para mostrar');
  expect(errors).toContain('es obligatorio y sin valor por defecto');
  expect(errors).toContain("el estado 'Cerrada' no existe");
  expect(errors).toContain("el rol 'Fantasma' no existe");
  expect(errors).toContain('solo usa campos del propio catálogo');
});

test('H1 CA1 · CA2: un campo oculto no viaja por ninguna vía; uno de solo lectura conserva su valor', async () => {
  // El supervisor captura margen y costo; el vendedor es el responsable.
  const sup = await create('sup', 'Oportunidad', {
    Nombre: 'Portal Norte', Margen: 30, NotaInterna: 'clave-secreta-xyz', Prioridad: 'Alta',
    Partidas: [{ Concepto: 'Licencias', Cantidad: 2, Costo: 500 }],
  });
  expect(sup.status).toBe(201);
  expect(sup.body.Margen).toBe(30);
  expect(sup.body.Prioridad).toBe('Alta');
  // El supervisor reasigna al vendedor de su unidad (sigue dentro de su alcance).
  ids.o1 = (await put('sup', sup.body, { Responsable: userIds.vend1 })).body;
  expect(ids.o1.Responsable).toBe(userIds.vend1);

  // GET, partidas, listado y modelo: el vendedor no recibe Margen, NotaInterna ni Costo.
  const mine = (await get('vend1', 'Oportunidad', ids.o1.id)).body;
  expect(mine).not.toHaveProperty('Margen');
  expect(mine).not.toHaveProperty('NotaInterna');
  expect((mine.Partidas as Row[])[0]).not.toHaveProperty('Costo');
  expect((mine.Partidas as Row[])[0].Concepto).toBe('Licencias');
  const items = (await list('vend1')).items;
  expect(items.find((r) => r.id === ids.o1.id)).not.toHaveProperty('Margen');
  const def = await (await (await as('vend1')).get('/api/model/entities/Oportunidad')).json();
  expect(def.fields.map((f: { name: string }) => f.name)).not.toContain('Margen');
  expect(def.security ?? null).toBeNull();
  expect(def.fields.find((f: { name: string }) => f.name === 'Prioridad').access).toBe('read');

  // Ni buscando ni ordenando por el campo oculto se deduce su valor.
  expect((await list('vend1', { search: 'clave-secreta' })).total).toBe(0);
  expect((await list('sup', { search: 'clave-secreta' })).total).toBe(1);
  expect((await json(await (await as('vend1')).get('/api/data/Oportunidad', { params: { sort: 'Margen', dir: 'asc' } }))).status).toBe(200);

  // /compute y respuesta de guardado tampoco lo devuelven.
  const computed = await (await (await as('vend1')).post('/api/data/Oportunidad/compute', { data: { id: ids.o1.id, values: { Nombre: 'x' } } })).json();
  expect(computed.values).not.toHaveProperty('Margen');
  expect(computed.values.Partidas[0]).not.toHaveProperty('Costo');

  // CA2: el vendedor intenta cambiar prioridad (solo lectura), margen (oculto) y costo de la partida (oculto).
  const line = (mine.Partidas as Row[])[0];
  const saved = await put('vend1', mine, { Nombre: 'Portal Norte v2', Prioridad: 'Normal', Margen: 1, Partidas: [{ id: line.id, Concepto: 'Licencias', Costo: 1 }] });
  expect(saved.status).toBe(200);
  expect(saved.body).not.toHaveProperty('Margen');
  const truth = (await get('sup', 'Oportunidad', ids.o1.id)).body;
  expect(truth.Nombre).toBe('Portal Norte v2');
  expect(truth.Prioridad).toBe('Alta');
  expect(truth.Margen).toBe(30);
  expect((truth.Partidas as Row[])[0].Costo).toBe(500);
  ids.o1 = truth;
});

test('H1 CA3: campos editables solo en ciertos estados; el motivo se captura solo en su acción', async () => {
  // En «Abierta» el descuento se captura; el motivo de pérdida nunca en el formulario.
  let o = (await put('vend1', ids.o1, { Descuento: 5, MotivoPerdida: 'escrito a mano' })).body;
  expect(o.Descuento).toBe(5);
  expect(o.MotivoPerdida).toBeNull();

  o = (await transition('vend1', o, 'revisar')).body;
  expect(o.Estado).toBe('EnRevision');
  // En revisión: descuento y cantidad de partidas (editableIn: Abierta) se conservan aunque se envíen.
  const line = (o.Partidas as Row[])[0];
  o = (await put('vend1', o, { Descuento: 50, Partidas: [{ id: line.id, Concepto: 'Licencias', Cantidad: 99 }] })).body;
  expect(o.Descuento).toBe(5);
  expect((o.Partidas as Row[])[0].Cantidad).toBe(2);

  // La transición sí captura el motivo (F-25 resuelto con metadatos).
  const lost = await transition('vend1', o, 'perder', { MotivoPerdida: 'Eligió otro proveedor' });
  expect(lost.status).toBe(200);
  expect(lost.body.MotivoPerdida).toBe('Eligió otro proveedor');
  expect(lost.body).not.toHaveProperty('Margen'); // la respuesta de la transición también se filtra
});

test('H2 CA1 · CA4: propios, de mi unidad y todos; fuera de alcance responde 404 en todas las vías', async () => {
  ids.o2 = (await create('vend2', 'Oportunidad', { Nombre: 'Tienda Sur', Partidas: [{ Concepto: 'Instalación' }] })).body;
  expect(ids.o2.Responsable).toBe(userIds.vend2);

  // vend2 (Sur) no ve la de vend1 (Norte); el supervisor del Norte ve la de vend1 y no la de vend2.
  const v2 = await list('vend2');
  expect(v2.items.map((r) => r.id)).toEqual([ids.o2.id]);
  expect(v2.total).toBe(1);
  expect((v2 as unknown as { stateCounts: { count: number }[] }).stateCounts.reduce((a, s) => a + s.count, 0)).toBe(1);
  const sup = (await list('sup')).items.map((r) => r.id);
  expect(sup).toContain(ids.o1.id);
  expect(sup).not.toContain(ids.o2.id);
  expect((await list('gerente')).items.map((r) => r.id)).toEqual(expect.arrayContaining([ids.o1.id, ids.o2.id])); // lectura de todos

  const v2api = await as('vend2');
  const o1 = ids.o1.id;
  expect((await get('vend2', 'Oportunidad', o1)).status).toBe(404);
  expect((await v2api.get(`/api/data/Oportunidad/${o1}/history`)).status()).toBe(404);
  expect((await v2api.put(`/api/data/Oportunidad/${o1}`, { data: { _rowVersion: ids.o1._rowVersion, Nombre: 'x' } })).status()).toBe(404);
  expect((await v2api.delete(`/api/data/Oportunidad/${o1}`)).status()).toBe(404);
  expect((await v2api.post(`/api/data/Oportunidad/${o1}/transition`, { data: { transition: 'revisar', _rowVersion: ids.o1._rowVersion } })).status()).toBe(404);
  expect((await v2api.post('/api/data/Oportunidad/compute', { data: { id: o1, values: {} } })).status()).toBe(404);

  // Buscador y referencias: no se elige ni se lee (por fórmulas) un registro que no se ve.
  const options = await (await v2api.get('/api/data/Oportunidad/lookup')).json();
  expect(options.map((x: { id: number }) => x.id)).toEqual([ids.o2.id]);
  const peek = await (await v2api.post('/api/data/Cotizacion/compute', { data: { values: { Oportunidad: o1 } } })).json();
  expect(peek.values.NombreOportunidad ?? null).toBeNull();
  const stolen = await create('vend2', 'Cotizacion', { Oportunidad: o1 });
  expect(stolen.status).toBe(400);
  expect(JSON.stringify(stolen.body)).toContain('El registro seleccionado no existe.');
});

test('H2 CA2 · CA3: regla declarativa de lectura; leer no da para editar, pero sí para ejecutar transiciones', async () => {
  ids.pub = (await create('vend1', 'Oportunidad', { Nombre: 'Feria pública', Publica: true, Partidas: [{ Concepto: 'Stand' }] })).body;
  // vend2 la ve por la regla «Publica AND Estado IN (…)», pero no la modifica (403, no 404).
  expect((await get('vend2', 'Oportunidad', ids.pub.id)).status).toBe(200);
  const denied = await put('vend2', ids.pub, { Nombre: 'Mía' });
  expect(denied.status).toBe(403);
  expect(JSON.stringify(denied.body)).toContain('no puede modificarlo');
  expect((await (await as('vend2')).delete(`/api/data/Oportunidad/${ids.pub.id}`)).status()).toBe(403);

  // El gerente solo lee, pero ejecuta «ganar» (la transición exige su rol, no escritura).
  const inReview = (await transition('vend1', ids.pub, 'revisar')).body;
  const won = await transition('gerente', inReview, 'ganar');
  expect(won.status).toBe(200);
  expect(won.body.Estado).toBe('Ganada');
  expect(won.body.Margen ?? null).toBeNull(); // el gerente sí ve el margen (vacío)
  // Ganada ya no cumple la regla: vend2 deja de verla.
  expect((await get('vend2', 'Oportunidad', ids.pub.id)).status).toBe(404);
});

test('H2 CA5: no se crea ni se deja un registro fuera del propio alcance de escritura', async () => {
  const before = (await list('vend1')).total;
  const outside = await create('vend1', 'Oportunidad', { Nombre: 'Para otro', Responsable: userIds.vend2, Partidas: [] });
  expect(outside.status).toBe(403);
  expect((await list('vend1')).total).toBe(before);
  expect((await list('gerente', { search: 'Para otro' })).total).toBe(0); // se revirtió completa

  const mine = (await create('vend1', 'Oportunidad', { Nombre: 'Me la quedo' })).body;
  const moved = await put('vend1', mine, { Responsable: userIds.vend2 });
  expect(moved.status).toBe(403);
  expect((await get('vend1', 'Oportunidad', mine.id)).body.Responsable).toBe(userIds.vend1);
});

test('Las automatizaciones actúan con autoridad del motor: escriben un campo oculto para quien dispara', async () => {
  // vend2 no ve el margen ni puede capturarlo; el gerente sí lo ve.
  const g0 = Number((await get('gerente', 'Oportunidad', ids.o2.id)).body.Margen ?? 0);
  const cot = await create('vend2', 'Cotizacion', { Oportunidad: ids.o2.id });
  expect(cot.status).toBe(201);
  expect(cot.body.NombreOportunidad).toBe('Tienda Sur');
  expect((await get('gerente', 'Oportunidad', ids.o2.id)).body.Margen).toBe(g0 + 1);
  expect((await get('vend2', 'Oportunidad', ids.o2.id)).body).not.toHaveProperty('Margen');
});

test('H2 CA6: el administrador asigna la unidad en Seguridad y cambia lo que ve el supervisor', async ({ page }) => {
  expect((await list('sup')).items.map((r) => r.id)).not.toContain(ids.o2.id);
  await page.goto('/admin/security');
  await page.getByRole('row', { name: /e2e-sec-vend2/ }).getByRole('button', { name: 'Editar' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByLabel('Unidad')).toHaveValue('Sur');
  await dialog.getByLabel('Unidad').fill('Norte');
  await dialog.getByRole('button', { name: 'Guardar' }).click();
  await expect(page.getByText('Usuario actualizado')).toBeVisible();
  await expect(page.getByRole('row', { name: /e2e-sec-vend2/ })).toContainText('Norte');
  // El dueño ahora es de la unidad del supervisor (el cambio cerró las sesiones de vend2).
  expect((await list('sup')).items.map((r) => r.id)).toContain(ids.o2.id);
  api.vend2 = await apiAs(username('vend2'), PWD);
});

test('H1 CA4: el formulario y el listado no muestran lo oculto y explican lo bloqueado', async ({ browser }) => {
  const page = await pageAs(browser, username('vend1'));
  await page.goto('/c/Oportunidad');
  await expect(page.getByRole('columnheader', { name: 'Nombre' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Margen' })).toHaveCount(0);

  await page.goto('/c/Oportunidad/new');
  await expect(page.getByLabel(/^Nombre/)).toBeEditable();
  await expect(page.getByLabel(/^Margen/)).toHaveCount(0);
  await expect(page.getByLabel(/^Prioridad/)).toBeDisabled();
  await expect(page.getByText('Solo lectura para tu rol')).toBeVisible();
  await expect(page.getByLabel(/^Motivo de pérdida/)).toBeDisabled();
  // Regresión (F-02): la pista nombra la acción que lo captura.
  await expect(page.getByText('Se captura al ejecutar «Perder»')).toBeVisible();
  await expect(page.getByLabel(/^Descuento/)).toBeEditable();
  await expect(page.getByRole('columnheader', { name: 'Costo' })).toHaveCount(0);
  await expectAccessible(page);

  // En revisión, el descuento y la cantidad de partidas se bloquean por estado.
  const o = (await create('vend1', 'Oportunidad', { Nombre: 'Revisión UI', Partidas: [{ Concepto: 'Soporte' }] })).body;
  await transition('vend1', o, 'revisar');
  await page.goto(`/c/Oportunidad/${o.id}`);
  await expect(page.getByLabel(/^Descuento/)).toBeDisabled();
  await expect(page.getByText('No se edita en este estado')).toBeVisible();
  await expect(page.getByLabel('Cantidad, partida 1')).toBeDisabled();
  await expect(page.getByLabel('Concepto, partida 1')).toBeEditable();
  await page.context().close();
});

test('Spec 013 · H1: un registro que solo se puede ver se muestra en solo lectura y explica por qué', async ({ browser }) => {
  const o = (await create('vend1', 'Oportunidad', { Nombre: 'Expo solo lectura', Publica: true, Partidas: [{ Concepto: 'Mesa' }] })).body;
  // CA2: vend2 la ve sin campos editables, sin «Guardar cambios» ni «Eliminar», y con el motivo.
  const page = await pageAs(browser, username('vend2'));
  await page.goto(`/c/Oportunidad/${o.id}`);
  await expect(page.getByLabel(/^Nombre/)).toHaveValue('Expo solo lectura');
  await expect(page.getByLabel(/^Nombre/)).toBeDisabled();
  // Las partidas en solo lectura se muestran como texto, sin celdas capturables.
  await expect(page.getByRole('cell', { name: 'Mesa' })).toBeVisible();
  await expect(page.getByLabel('Concepto, partida 1')).toHaveCount(0);
  await expect(page.getByText('Puedes consultar este registro, pero tu rol no puede modificarlo.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guardar cambios' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Eliminar' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Agregar/ })).toHaveCount(0);
  // F-02: sin marcas de obligatorio ni pistas de edición campo por campo (el aviso general lo explica).
  await expect(page.locator('.field-required')).toHaveCount(0);
  await expect(page.getByLabel(/^Nombre/)).not.toHaveAttribute('required');
  await expect(page.getByText('Solo lectura para tu rol')).toHaveCount(0);
  await expect(page.getByText('Se captura al ejecutar «Perder»')).toHaveCount(0);
  await expectAccessible(page);
  await page.context().close();

  // CA3: el dueño la sigue editando.
  const owner = await pageAs(browser, username('vend1'));
  await owner.goto(`/c/Oportunidad/${o.id}`);
  await expect(owner.getByLabel(/^Nombre/)).toBeEditable();
  await expect(owner.getByRole('button', { name: 'Guardar cambios' })).toBeVisible();
  await expect(owner.getByRole('button', { name: 'Eliminar', exact: true })).toBeVisible();
  await owner.context().close();

  // CA1: la API dice qué puede hacer cada quien con el registro.
  const seen = (await get('vend2', 'Oportunidad', o.id)).body;
  expect(seen._access).toEqual({ update: false, delete: false, reason: expect.stringContaining('no puede modificarlo') });
  expect((await get('vend1', 'Oportunidad', o.id)).body._access).toEqual({ update: true, delete: true, reason: null });
});

test('H5: el modelador ve y edita la seguridad como JSON validado', async ({ page }) => {
  await page.goto('/admin/model?edit=Oportunidad');
  const box = page.getByLabel('Seguridad (JSON)');
  await expect(box).toHaveValue(/"owner": "Responsable"/);
  await expect(box).not.toHaveValue(/null/);
  await box.fill(JSON.stringify({ ...security, fields: [...security.fields, { field: 'NoExiste', read: ['Vendedor'] }] }, null, 2));
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan tiene errores')).toBeVisible();
  await expect(dialog.getByText(/campo 'NoExiste': no existe en Oportunidad/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Volver a editar' }).click();
  await box.fill(JSON.stringify({ ...security, fields: [...security.fields, { field: 'Publica', edit: ['Supervisor de ventas'] }] }, null, 2));
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText(/Definir la seguridad de 'Oportunidad': 6 regla\(s\) de campo, 4 permiso\(s\) por registro/)).toBeVisible();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  const def = await (await (await as('admin')).get('/api/model/entities/Oportunidad')).json();
  expect(def.security.fields).toHaveLength(6);
});

// ---------------- H3 · H4: módulos ----------------

test('H3 CA1: Existencias las administra el sistema (Inventario 1.1.0), ni el administrador las captura', async ({ page }) => {
  const admin = await as('admin');
  const forced = await admin.post('/api/data/Existencia', { data: {} });
  expect(forced.status()).toBe(403);
  expect(JSON.stringify(await forced.json())).toContain('los administra el sistema');
  const one = (await (await admin.get('/api/data/Existencia')).json()).items[0] as Row;
  expect((await admin.put(`/api/data/Existencia/${one.id}`, { data: { _rowVersion: one._rowVersion } })).status()).toBe(403);
  expect((await admin.delete(`/api/data/Existencia/${one.id}`)).status()).toBe(403);

  // Regresión (F-04, spec 005): al administrador se le ofrecía «Nuevo».
  await page.goto('/c/Existencia');
  await expect(page.getByRole('heading', { name: 'Existencias' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Nuevo' })).toHaveCount(0);
  await page.goto(`/c/Existencia/${one.id}`);
  await expect(page.getByText('Lo administra el sistema')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guardar cambios' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Eliminar' })).toHaveCount(0);
});

test('H4 CA1: el motivo de rechazo de la requisición se captura solo al rechazar (Compras 1.3.0, F-25)', async ({ browser }) => {
  const s = await apiAs('e2e-solicitante', COMPRAS_PWD);
  const dep = (await (await s.get('/api/data/Departamento/lookup')).json())[0].id;
  const almacen = (await (await s.get('/api/data/Almacen/lookup')).json())[0]?.id
    ?? (await (await (await as('admin')).get('/api/data/Almacen/lookup')).json())[0].id;
  const producto = (await (await s.get('/api/data/Producto/lookup')).json())[0].id;
  let req = await (await s.post('/api/data/Requisicion', { data: {
    Departamento: dep, Almacen: almacen, Justificacion: 'Spec 006', MotivoRechazo: 'antes de tiempo', Partidas: [{ Producto: producto, Cantidad: 1 }],
  } })).json();
  expect(req.MotivoRechazo).toBeNull();

  const page = await pageAs(browser, 'e2e-solicitante', COMPRAS_PWD);
  await page.goto(`/c/Requisicion/${req.id}`);
  await expect(page.getByLabel(/^Motivo de rechazo/)).toBeDisabled();
  await expect(page.getByText('Se captura al ejecutar «Rechazar»')).toBeVisible();
  await page.context().close();

  req = await (await s.post(`/api/data/Requisicion/${req.id}/transition`, { data: { transition: 'enviar', _rowVersion: req._rowVersion } })).json();
  const g = await apiAs('e2e-gerente', COMPRAS_PWD);
  const rejected = await (await g.post(`/api/data/Requisicion/${req.id}/transition`, { data: {
    transition: 'rechazar', _rowVersion: req._rowVersion, values: { MotivoRechazo: 'Sin presupuesto' },
  } })).json();
  expect(rejected.Estado).toBe('Rechazada');
  expect(rejected.MotivoRechazo).toBe('Sin presupuesto');
});

test('H4 CA2: cada solicitante ve solo sus requisiciones; el gerente ve todas', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/users', { data: {
    username: 'e2e-solicitante2', displayName: 'Otra solicitante E2E', password: COMPRAS_PWD, roleIds: [await roleId(admin, 'Solicitante')],
  } });
  expect(res.status()).toBe(201);
  const s1 = await apiAs('e2e-solicitante', COMPRAS_PWD);
  const s2 = await apiAs('e2e-solicitante2', COMPRAS_PWD);
  const mine = (await (await s1.get('/api/data/Requisicion', { params: { pageSize: 200 } })).json()) as { items: Row[]; total: number };
  expect(mine.total).toBeGreaterThan(0);
  expect((await (await s2.get('/api/data/Requisicion')).json()).total).toBe(0);
  expect((await s2.get(`/api/data/Requisicion/${mine.items[0].id}`)).status()).toBe(404);
  const all = (await (await (await apiAs('e2e-gerente', COMPRAS_PWD)).get('/api/data/Requisicion')).json()).total;
  expect(all).toBeGreaterThanOrEqual(mine.total);
});

test('H4 CA3: el almacenista no ve precios ni importes de la orden; el comprador sí', async ({ browser }) => {
  const c = await apiAs('e2e-comprador', COMPRAS_PWD);
  const a = await apiAs('e2e-almacen', COMPRAS_PWD);
  const oc = ((await (await c.get('/api/data/OrdenCompra', { params: { pageSize: 1 } })).json()).items as Row[])[0];
  const forBuyer = await (await c.get(`/api/data/OrdenCompra/${oc.id}`)).json();
  expect(forBuyer.TotalMXN).toBeGreaterThan(0);
  expect(forBuyer.Partidas[0].PrecioUnitario).toBeGreaterThan(0);

  const forWarehouse = await (await a.get(`/api/data/OrdenCompra/${oc.id}`)).json();
  for (const f of ['Subtotal', 'Descuento', 'IVA', 'Total', 'TotalMXN']) expect(forWarehouse).not.toHaveProperty(f);
  for (const f of ['PrecioUnitario', 'DescuentoPct', 'IvaPct', 'Importe', 'DescuentoImporte', 'IvaImporte', 'Total', 'CostoMXN'])
    expect(forWarehouse.Partidas[0]).not.toHaveProperty(f);
  expect(forWarehouse.Partidas[0].Cantidad).toBeGreaterThan(0);
  expect(forWarehouse).toHaveProperty('PendientePorRecibir');
  expect(((await (await a.get('/api/data/OrdenCompra')).json()).items as Row[])[0]).not.toHaveProperty('Total');

  const page = await pageAs(browser, 'e2e-almacen', COMPRAS_PWD);
  await page.goto(`/c/OrdenCompra/${oc.id}`);
  await expect(page.getByRole('columnheader', { name: 'Cantidad' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Precio unitario' })).toHaveCount(0);
  await expect(page.getByText('Total en moneda base')).toHaveCount(0); // Compras 1.6.0: antes «Total MXN»
  await page.context().close();
});
