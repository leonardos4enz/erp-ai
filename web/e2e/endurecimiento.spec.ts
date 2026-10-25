// Spec 012 · Endurecimiento. Cada prueba es un hallazgo de la auditoría de solo lectura (2026-10-23) y se verificó
// fallando con el código anterior. El motor se prueba con catálogos propios del grupo «Pruebas 012», que se archivan al
// terminar (no alteran el menú de las suites siguientes ni las capturas de referencia).
import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { request, type APIRequestContext } from '@playwright/test';
import { ADMIN, DB, PORTS, apiAs, applyPlan, expect, roleId, test } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 012';
const PWD = 'frase larga de endurecimiento 2026';
const SECRET = 'S3CRETO-012-precio';
const OTHER = { code: 'E2E-EH2', name: 'Otra empresa 012' };
const users = { ana: 'e2e-eh-ana', beto: 'e2e-eh-beto', aud: 'e2e-eh-auditor' } as const;
type Who = keyof typeof users | 'admin';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };
type Company = { id: number; code: string; name: string; baseCurrency: string; isDefault: boolean; isActive: boolean };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
async function json(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Row & { items: Row[]; total: number; title?: string; values?: Row } };
}
const create = async (w: Who, entity: string, data: object) => {
  const r = await json(await (await as(w)).post(`/api/data/${entity}`, { data }));
  expect(r.status, JSON.stringify(r.body)).toBe(201);
  return r.body;
};
const get = async (w: Who, entity: string, id: number) => (await json(await (await as(w)).get(`/api/data/${entity}/${id}`))).body;
const transition = async (w: Who, entity: string, row: Row, name: string, values?: object) =>
  json(await (await as(w)).post(`/api/data/${entity}/${row.id}/transition`, { data: { transition: name, _rowVersion: row._rowVersion, values } }));
const switchTo = async (w: Who, companyId: number) =>
  expect((await (await as(w)).post('/api/auth/company', { data: { companyId } })).status()).toBe(200);

const company: { main: Company; other: Company } = { main: null!, other: null! };
const rec: Record<string, Row> = {};

test.afterAll(async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  await admin.post('/api/auth/company', { data: { companyId: company.main?.id } });
  for (const c of (await (await admin.get('/api/admin/companies')).json()) as Company[])
    if (c.code === OTHER.code && c.isActive) await admin.put(`/api/admin/companies/${c.id}`, { data: { name: c.name, baseCurrency: c.baseCurrency, isActive: false } });
  const model = (await (await admin.get('/api/model/entities', { params: { includeChildren: 'true' } })).json()) as { name: string }[];
  const has = (n: string) => model.some((e) => e.name === n);
  // Un documento y sus partidas se referencian entre sí: primero el campo de partidas, luego la partida y el documento.
  const operations = [
    ...(['EhRecepcion', 'EhOrden'] as const).filter(has).flatMap((doc) => [
      { op: 'archiveField', entity: doc, field: 'Partidas' },
      { op: 'archiveEntity', entity: `${doc}Partida` },
      { op: 'archiveEntity', entity: doc },
    ]),
    ...['EhPedido', 'EhTarea', 'EhCliente', 'EhPrivado'].filter(has).map((entity) => ({ op: 'archiveEntity', entity })),
  ];
  if (operations.length) await applyPlan(admin, { description: 'E2E 012: limpieza', operations });
  await admin.dispose();
});

test('preparar: catálogos con seguridad por registro, campo y empresa; roles, usuarios y otra empresa', async () => {
  const admin = await as('admin');
  const companies = (await (await admin.get('/api/admin/companies')).json()) as Company[];
  company.main = companies.find((c) => c.isDefault)!;
  const existing = companies.find((c) => c.code === OTHER.code);
  company.other = existing
    ? await (await admin.put(`/api/admin/companies/${existing.id}`, { data: { name: OTHER.name, baseCurrency: 'MXN', isActive: true } })).json()
    : await (await admin.post('/api/admin/companies', { data: { code: OTHER.code, name: OTHER.name, baseCurrency: 'MXN' } })).json();
  expect(company.other.code).toBe(OTHER.code);

  for (const name of ['Eh Editor', 'Eh Auditor']) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 012', canManageModel: false, canManageUsers: false,
      canViewAudit: name === 'Eh Auditor', permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
  }
  await applyPlan(admin, { description: 'Spec 012 · catálogos', operations: [
    { op: 'createEntity', group: GROUP, name: 'EhTarea', label: 'Tarea 012', labelPlural: 'Tareas 012', displayField: 'Titulo', perCompany: true, fields: [
      { name: 'Titulo', label: 'Título', type: 'text', required: true },
      { name: 'Publica', label: 'Pública', type: 'boolean', defaultValue: 'FALSE' },
      { name: 'Secreto', label: 'Secreto', type: 'text' },
      { name: 'Motivo', label: 'Motivo', type: 'text' },
    ] },
    { op: 'setWorkflow', entity: 'EhTarea', workflow: {
      initial: 'Abierta',
      states: [{ name: 'Abierta', label: 'Abierta' }, { name: 'Revisada', label: 'Revisada' }, { name: 'Cerrada', label: 'Cerrada', editable: false }],
      transitions: [
        { name: 'cerrar', label: 'Cerrar', from: ['Abierta', 'Revisada'], to: 'Cerrada', requiredFields: ['Motivo'] },
        { name: 'revisar', label: 'Revisar', from: ['Abierta'], to: 'Revisada', roles: ['Eh Editor'] },
      ],
    } },
    { op: 'setSecurity', entity: 'EhTarea', security: {
      fields: [{ field: 'Secreto', read: ['Eh Editor'] }],
      records: [
        { roles: ['Eh Editor'], scope: 'own' },
        { roles: ['Eh Editor'], access: 'read' },
        { roles: ['Eh Auditor'], where: 'Publica', access: 'read' },
      ],
    } },
    { op: 'createEntity', group: GROUP, name: 'EhPrivado', label: 'Privado 012', labelPlural: 'Privados 012', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
    ] },
    { op: 'createEntity', group: GROUP, name: 'EhCliente', label: 'Cliente 012', labelPlural: 'Clientes 012', displayField: 'Nombre', perCompany: true, fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
    ] },
    { op: 'createEntity', group: GROUP, name: 'EhPedido', label: 'Pedido 012', labelPlural: 'Pedidos 012', displayField: 'Clave', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true },
      { name: 'Cliente', label: 'Cliente', type: 'reference', referenceEntity: 'EhCliente' },
      { name: 'Tarea', label: 'Tarea', type: 'reference', referenceEntity: 'EhTarea' },
    ] },
  ] });

  const perm = (entity: string, write: boolean) => ({ entity, canRead: true, canCreate: write, canUpdate: write, canDelete: false });
  const roles = await (await admin.get('/api/admin/roles')).json();
  for (const [name, permissions] of [
    ['Eh Editor', [perm('EhTarea', true), perm('EhPedido', true), perm('EhCliente', true)]],
    ['Eh Auditor', [perm('EhTarea', false), perm('EhPedido', false)]],
  ] as const) {
    const r = roles.find((x: { name: string }) => x.name === name);
    const res = await admin.put(`/api/admin/roles/${r.id}`, { data: { name, description: r.description, canManageModel: false, canManageUsers: false,
      canViewAudit: name === 'Eh Auditor', permissions } });
    expect(res.status(), await res.text()).toBe(200);
  }
  for (const [w, role] of [['ana', 'Eh Editor'], ['beto', 'Eh Editor'], ['aud', 'Eh Auditor']] as const) {
    const res = await admin.post('/api/admin/users', { data: { username: users[w], displayName: `${w} 012`, password: PWD,
      roleIds: [await roleId(admin, role)], companyIds: [company.main.id] } });
    expect(res.status(), await res.text()).toBe(201);
  }

  rec.publica = await create('ana', 'EhTarea', { Titulo: 'Tarea pública 012', Publica: true, Secreto: SECRET });
  rec.privada = await create('ana', 'EhTarea', { Titulo: 'Tarea privada 012', Publica: false, Secreto: SECRET });
  rec.nada = await create('admin', 'EhPrivado', { Nombre: 'Registro sin permiso 012' });
  await switchTo('admin', company.other.id);
  rec.otra = await create('admin', 'EhTarea', { Titulo: 'Tarea de otra empresa 012', Publica: true });
  rec.clienteOtra = await create('admin', 'EhCliente', { Nombre: 'Cliente de otra empresa 012' });
  await switchTo('admin', company.main.id);
  rec.clienteMain = await create('admin', 'EhCliente', { Nombre: 'Cliente principal 012' });
});

test('H1 CA1 · CA2 · CA3: la bitácora respeta catálogo, empresa, alcance por registro y campos ocultos', async () => {
  const aud = await as('aud');
  const audit = async (params: Record<string, string>) => (await json(await aud.get('/api/audit', { params: { pageSize: '200', ...params } }))).body;

  const tareas = await audit({ entity: 'EhTarea' });
  const ids = tareas.items.map((i) => String(i.recordId));
  expect(ids).toContain(String(rec.publica.id));
  // Regresión (auditoría 012, hallazgo 1): antes llegaban la tarea no pública, la de otra empresa y el campo oculto.
  expect(ids).not.toContain(String(rec.privada.id));
  expect(ids).not.toContain(String(rec.otra.id));
  expect(JSON.stringify(tareas.items)).not.toContain(SECRET);
  expect(tareas.total).toBe(tareas.items.length);
  const created = tareas.items.find((i) => i.action === 'create' && i.recordId === String(rec.publica.id)) as unknown as { after: Record<string, unknown> };
  expect(created.after).toMatchObject({ Titulo: 'Tarea pública 012' });
  expect(created.after).not.toHaveProperty('Secreto');

  // Un catálogo que no puede leer: nada.
  expect((await audit({ entity: 'EhPrivado' })).total).toBe(0);
  // Sin filtro: tampoco aparecen en la lista general, pero sí los eventos que no son de datos (sesiones).
  const all = await audit({});
  expect(JSON.stringify(all.items)).not.toContain('Registro sin permiso 012');
  expect(JSON.stringify(all.items)).not.toContain(SECRET);
  expect(all.items.some((i) => i.action === 'login')).toBe(true);

  // El superusuario ve todo, como antes (H1 CA4).
  const admin = (await json(await (await as('admin')).get('/api/audit', { params: { entity: 'EhTarea', pageSize: '200' } }))).body;
  expect(JSON.stringify(admin.items)).toContain(SECRET);
  expect(admin.items.map((i) => String(i.recordId))).toEqual(expect.arrayContaining([String(rec.privada.id), String(rec.otra.id)]));
});

test('H2 CA1 · CA2: una transición sin roles exige poder modificar ese registro; con roles, basta verlo', async () => {
  const tarea = await create('ana', 'EhTarea', { Titulo: 'Tarea de Ana 012' });
  const seen = await get('beto', 'EhTarea', tarea.id);
  expect(seen.Titulo).toBe('Tarea de Ana 012');
  const cerrar = (seen._workflow as { transitions: { name: string; allowed: boolean; reason?: string }[] }).transitions.find((t) => t.name === 'cerrar')!;
  expect(cerrar.allowed).toBe(false);
  expect(cerrar.reason).toContain('no puede modificarlo');

  // Regresión (auditoría 012, hallazgo 2): Beto solo edita sus tareas y aun así cerraba la de Ana capturando «Motivo».
  const denied = await transition('beto', 'EhTarea', seen, 'cerrar', { Motivo: 'cerrada por otro' });
  expect(denied.status).toBe(403);
  expect((await get('ana', 'EhTarea', tarea.id)).Estado).toBe('Abierta');

  // Con roles declarados (autorización de proceso) sigue bastando con ver el registro.
  const revisada = await transition('beto', 'EhTarea', seen, 'revisar');
  expect(revisada.status, JSON.stringify(revisada.body)).toBe(200);
  expect(revisada.body.Estado).toBe('Revisada');
  // Y Ana sí cierra la suya.
  const propia = await transition('ana', 'EhTarea', revisada.body, 'cerrar', { Motivo: 'listo' });
  expect(propia.status, JSON.stringify(propia.body)).toBe(200);
});

test('H6 CA1 · CA2: la etiqueta de una referencia fuera de alcance no revela el nombre', async () => {
  // Pedidos (catálogo compartido): uno apunta a un cliente de otra empresa; otros, a una tarea no pública y a una pública.
  const admin = await as('admin');
  await switchTo('admin', company.other.id);
  const pedido = await create('admin', 'EhPedido', { Clave: 'PED-012', Cliente: rec.clienteOtra.id });
  expect(pedido.Cliente__label).toBe('Cliente de otra empresa 012');
  await switchTo('admin', company.main.id);
  const privado = await create('admin', 'EhPedido', { Clave: 'PED-012-B', Tarea: rec.privada.id });
  const publico = await create('admin', 'EhPedido', { Clave: 'PED-012-C', Tarea: rec.publica.id });

  // Regresión (auditoría 012, hallazgo 6): desde la empresa principal se leía el nombre del cliente de otra empresa.
  const fromMain = await get('admin', 'EhPedido', pedido.id);
  expect(fromMain.Cliente).toBe(rec.clienteOtra.id);
  expect(fromMain.Cliente__label).toBe('(sin acceso)');
  const listed = (await json(await admin.get('/api/data/EhPedido', { params: { pageSize: 200 } }))).body.items.find((r) => r.id === pedido.id)!;
  expect(listed.Cliente__label).toBe('(sin acceso)');
  // El cálculo en vivo ya descartaba elegir un registro que no se ve; tampoco devuelve su nombre.
  const computed = (await json(await admin.post('/api/data/EhPedido/compute', { data: { values: { Clave: 'X', Cliente: rec.clienteOtra.id } } }))).body;
  expect(JSON.stringify(computed.values)).not.toContain('Cliente de otra empresa 012');
  const kept = (await json(await admin.post('/api/data/EhPedido/compute', { data: { id: pedido.id, values: { Clave: 'X' } } }))).body;
  expect(kept.values!.Cliente__label).toBe('(sin acceso)');

  // Por permisos por registro: el auditor ve la tarea pública, no la privada.
  const audView = (await json(await (await as('aud')).get('/api/data/EhPedido', { params: { pageSize: 200 } }))).body.items;
  expect(audView.find((r) => r.id === privado.id)!.Tarea__label).toBe('(sin acceso)');
  expect(audView.find((r) => r.id === publico.id)!.Tarea__label).toBe('Tarea pública 012');
  expect(audView.find((r) => r.id === pedido.id)!.Cliente__label).toBe('(sin acceso)');

  // CA2: el buscador ya filtraba (empresa y alcance); se deja como prueba.
  const lookup = (await (await (await as('aud')).get('/api/data/EhTarea/lookup')).json()) as { label: string }[];
  expect(lookup.map((l) => l.label)).toEqual(['Tarea pública 012']);
  const clientes = (await (await admin.get('/api/data/EhCliente/lookup')).json()) as { label: string }[];
  expect(clientes.map((l) => l.label)).toEqual(['Cliente principal 012']);
});

test('H7 CA2: no se cambia la moneda base de una empresa con registros por empresa', async () => {
  const admin = await as('admin');
  const put = (baseCurrency: string) =>
    admin.put(`/api/admin/companies/${company.other.id}`, { data: { name: OTHER.name, baseCurrency, isActive: true } });
  // Regresión (auditoría 012, menor): con tareas y clientes registrados en MXN se podía pasar a USD.
  const res = await put('USD');
  expect(res.status()).toBe(409);
  expect(await res.text()).toContain('moneda base');
  expect((await put('MXN')).status()).toBe(200); // sin cambio de moneda sí se edita
});

// ---------------- H3: concurrencia ----------------
async function orderWithReception(qty: number, receptions: number[]) {
  const orden = await create('admin', 'EhOrden', { Clave: `OC-${Date.now()}`, Partidas: [{ Concepto: 'Tornillos', Cantidad: qty }] });
  const partida = (orden.Partidas as Row[])[0];
  const recs: Row[] = [];
  for (const q of receptions)
    recs.push(await create('admin', 'EhRecepcion', { Orden: orden.id, Partidas: [{ PartidaOrden: partida.id, Cantidad: q }] }));
  return { orden, partida, recs };
}

test('preparar H3: orden con partidas, recepción con regla «no exceder lo pendiente» y acumulado al confirmar', async () => {
  await applyPlan(await as('admin'), { description: 'Spec 012 · recepciones', operations: [
    { op: 'createEntity', group: GROUP, name: 'EhOrden', label: 'Orden 012', labelPlural: 'Órdenes 012', displayField: 'Clave', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true },
    ] },
    { op: 'createEntity', name: 'EhOrdenPartida', label: 'Partida 012', labelPlural: 'Partidas 012', displayField: 'Concepto', fields: [
      { name: 'Orden', label: 'Orden', type: 'parent', referenceEntity: 'EhOrden' },
      { name: 'Concepto', label: 'Concepto', type: 'text', required: true },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal', required: true },
      { name: 'Recibida', label: 'Recibida', type: 'decimal', editable: false, defaultValue: '0' },
      { name: 'Pendiente', label: 'Pendiente', type: 'formula', resultType: 'decimal', formula: 'Cantidad - Recibida' },
    ] },
    { op: 'addField', entity: 'EhOrden', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'EhOrdenPartida' } },
    { op: 'setWorkflow', entity: 'EhOrden', workflow: {
      initial: 'Abierta', states: [{ name: 'Abierta', label: 'Abierta' }, { name: 'Cancelada', label: 'Cancelada', editable: false }],
      transitions: [{ name: 'cancelar', label: 'Cancelar', from: ['Abierta'], to: 'Cancelada' }],
    } },
    { op: 'createEntity', group: GROUP, name: 'EhRecepcion', label: 'Recepción 012', labelPlural: 'Recepciones 012', fields: [
      { name: 'Orden', label: 'Orden', type: 'reference', referenceEntity: 'EhOrden', required: true },
    ] },
    { op: 'createEntity', name: 'EhRecepcionPartida', label: 'Partida de recepción 012', labelPlural: 'Partidas de recepción 012', fields: [
      { name: 'Recepcion', label: 'Recepción', type: 'parent', referenceEntity: 'EhRecepcion' },
      { name: 'PartidaOrden', label: 'Partida de la orden', type: 'reference', referenceEntity: 'EhOrdenPartida', required: true },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal', required: true },
    ] },
    { op: 'addField', entity: 'EhRecepcion', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'EhRecepcionPartida' } },
    { op: 'setRules', entity: 'EhRecepcionPartida', rules: [
      { name: 'NoExceder', field: 'Cantidad', assert: 'Cantidad <= PartidaOrden.Pendiente', message: 'Excede lo pendiente de la orden.' },
    ] },
    { op: 'setWorkflow', entity: 'EhRecepcion', workflow: {
      initial: 'Borrador', states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Confirmada', label: 'Confirmada', editable: false }],
      transitions: [{ name: 'confirmar', label: 'Confirmar', from: ['Borrador'], to: 'Confirmada', when: "Orden.Estado = 'Abierta'",
        whenMessage: 'La orden ya no está abierta.' }],
    } },
    { op: 'setAutomations', entity: 'EhRecepcion', automations: [
      { name: 'Acumular', label: 'Acumular lo recibido', on: 'enterState', state: 'Confirmada', actions: [
        { type: 'rollup', lines: 'Partidas', target: 'PartidaOrden', field: 'Recibida', sum: 'Cantidad', filter: "Recepcion.Estado = 'Confirmada'" },
      ] },
    ] },
  ] });
});

test('H3 CA1: confirmaciones simultáneas contra la misma partida no reciben más de lo pedido', async () => {
  const { orden, recs } = await orderWithReception(10, Array(10).fill(10));
  // Regresión (auditoría 012, hallazgo 3): todas validaban «pendiente 10» antes de que la primera bloqueara la orden.
  const results = await Promise.all(recs.map((r) => transition('admin', 'EhRecepcion', r, 'confirmar')));
  const ok = results.filter((r) => r.status === 200);
  expect(results.map((r) => r.status).sort(), JSON.stringify(results.filter((r) => r.status !== 200 && r.status !== 400).map((r) => r.body))).toEqual(
    [200, ...Array(9).fill(400)]);
  expect(ok).toHaveLength(1);
  const after = await get('admin', 'EhOrden', orden.id);
  expect((after.Partidas as Row[])[0]).toMatchObject({ Recibida: 10, Pendiente: 0 });
});

test('H3 CA3: cancelar la orden mientras se confirma una recepción: gana una sola', async () => {
  for (let round = 0; round < 6; round++) {
    const { orden, recs } = await orderWithReception(5, [5]);
    const [conf, canc] = await Promise.all([transition('admin', 'EhRecepcion', recs[0], 'confirmar'), transition('admin', 'EhOrden', orden, 'cancelar')]);
    // Regresión (auditoría 012, hallazgo 3): la recepción se confirmaba y acumulaba sobre una orden recién cancelada.
    expect([conf.status, canc.status].filter((s) => s === 200), `ronda ${round}: ${conf.status}/${canc.status}`).toHaveLength(1);
    const final = await get('admin', 'EhOrden', orden.id);
    const recibida = (final.Partidas as Row[])[0].Recibida;
    if (final.Estado === 'Cancelada') expect(recibida).toBe(0);
    else expect(recibida).toBe(5);
  }
});

test('H3 CA2 + H7 CA1: Compras 1.7.0 protege lo recibido y guarda la moneda del costo último', async () => {
  const admin = await as('admin');
  const modules = (await (await admin.get('/api/admin/modules')).json()) as { id: string; installedVersion?: string; version: string }[];
  const compras = modules.find((m) => m.id === 'compras')!;
  expect(compras.installedVersion).toBe(compras.version); // 1.7.0 o posterior (la 014 agrega 1.8.0)
  const partida = await (await admin.get('/api/model/entities/OrdenCompraPartida')).json();
  expect(partida.rules.map((r: { name: string }) => r.name)).toEqual(['CantidadPositiva', 'PrecioNoNegativo', 'DescuentoRango', 'IvaRango', 'RecibidoNoExcede']);
  const producto = await (await admin.get('/api/model/entities/Producto')).json();
  expect(producto.fields.map((f: { name: string }) => f.name)).toContain('MonedaCosto');
  const recepcion = await (await admin.get('/api/model/entities/Recepcion')).json();
  const acumular = recepcion.automations.find((a: { name: string }) => a.name === 'AcumularRecibido');
  expect(JSON.stringify(acumular)).toContain('BASECURRENCY()');
});

// ---------------- H4: segunda instancia «producción» sin AllowDirectChanges ----------------
const PROD = `http://localhost:${PORTS.produccion}`;
let prod: ChildProcess | undefined;

test.describe('H4 · producción', () => {
  test.beforeAll(async () => {
    const root = path.resolve(import.meta.dirname, '..', '..');
    const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';
    prod = spawn('dotnet', ['run', '--no-build', '--no-launch-profile', '--project', path.join(root, 'src', 'Erp.Api'), '--urls', PROD], {
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        ConnectionStrings__Erp: `Server=${server};Database=${DB};Integrated Security=true;Encrypt=true;TrustServerCertificate=true`,
        Database__AutoCreate: 'false',
        Security__LoginAttemptsPerMinute: '1000',
        Environments__Name: 'Producción E2E (sin atajos)',
        Environments__RequireApproval: 'true',
        Environments__SigningKey: process.env.E2E_SIGNING_KEY ?? '',
        Assistant__BaseUrl: `http://127.0.0.1:${PORTS.asistente}`,
      },
      stdio: 'ignore',
    });
    for (let i = 0; i < 120; i++) {
      try { if ((await fetch(`${PROD}/api/health`)).ok) return; } catch { /* aún no arranca */ }
      await new Promise((r) => setTimeout(r, 500));
    }
    throw new Error(`La instancia de producción no arrancó en ${PROD}`);
  });
  test.afterAll(() => { prod?.kill(); });

  test('H4 CA1 · CA2: con aprobación, el modelo no cambia por plan, módulo ni asistente', async () => {
    const anon = await request.newContext({ baseURL: PROD, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { 'X-ERP-Request': '1' } });
    const login = await anon.post('/api/auth/login', { data: { username: ADMIN.username, password: ADMIN.password } });
    expect(login.status()).toBe(200);
    const cookie = /erp\.auth=[^;]+/.exec(login.headers()['set-cookie'] ?? '')![0];
    const admin = await request.newContext({ baseURL: PROD, storageState: { cookies: [], origins: [] }, extraHTTPHeaders: { 'X-ERP-Request': '1', Cookie: cookie } });

    const plan = { description: 'Atajo', operations: [{ op: 'addField', entity: 'EhPedido', field: { name: 'Atajo', label: 'Atajo', type: 'text' } }] };
    expect((await admin.post('/api/model/plan/preview', { data: plan })).status()).toBe(200);
    // Regresión (auditoría 012, hallazgo 4): cada uno de estos caminos aplicaba sin la aprobación de otro administrador.
    for (const [url, data] of [
      ['/api/model/plan/apply', plan],
      ['/api/admin/modules/compras/upgrade', undefined],
      ['/api/admin/modules/inventario/install', undefined],
      ['/api/assistant/requests/999999/apply', { reviewHash: 'x' }],
    ] as const) {
      const res = await admin.post(url, { data });
      expect(res.status(), url).toBe(409);
      expect(await res.text(), url).toContain('Entornos');
    }
    expect((await (await admin.get('/api/model/entities/EhPedido')).json()).fields.map((f: { name: string }) => f.name)).not.toContain('Atajo');
    expect(await (await admin.get('/api/assistant')).json()).toMatchObject({ directChanges: false });
    await admin.dispose();
    await anon.dispose();
  });

  // Spec 018 (H2 CA5): esta instancia conserva el límite por defecto del servidor de tokens (30 por minuto por dirección).
  test('Spec 018 H2 CA5: el servidor de tokens limita las peticiones por dirección', async () => {
    const anon = await request.newContext({ baseURL: PROD, storageState: { cookies: [], origins: [] } });
    const statuses: number[] = [];
    for (let i = 0; i < 32; i++) {
      const r = await anon.post('/oauth/token', { form: { grant_type: 'client_credentials', client_id: 'erpcl_00000000000000', client_secret: 'erpcs_' + 'a'.repeat(43) } });
      statuses.push(r.status());
    }
    expect(statuses.slice(0, 30).every((s) => s === 401)).toBe(true);
    expect(statuses.slice(30)).toEqual([429, 429]);
    await anon.dispose();
  });
});
