// Spec 007 · Metadatos de interfaz: layouts, vistas guardadas, reportes y tableros. Corre después de compras.spec.ts
// (Compras 1.4.0 instalado con sus usuarios). El motor se prueba con un catálogo propio (Pedidos) declarado por plan,
// con la seguridad de la 006; lo que ve una persona, por la UI.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de interfaz 2026';
const COMPRAS_PWD = 'frase larga de compras 2026'; // usuarios creados por compras.spec.ts
const USERS = { cap1: 'Capturista', cap2: 'Capturista', analista: 'Analista', ajeno: 'Ajeno' } as const;
type Who = keyof typeof USERS;
const username = (w: Who) => `e2e-ui-${w}`;

const api: Partial<Record<Who | 'admin', APIRequestContext>> = {};
const as = async (w: Who | 'admin') =>
  (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(username(w), PWD));

async function pageAs(browser: Browser, user: string, pwd = PWD): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, user, pwd);
  return page;
}

type Report = { rows: Record<string, unknown>[]; totals: Record<string, unknown>; columns: { key: string; label: string }[] };
const report = async (w: Who | 'admin', name: string, filters?: object[], entity = 'Pedido') => {
  const res = await (await as(w)).get(`/api/reports/${entity}/${name}`, { params: filters ? { filters: JSON.stringify(filters) } : {} });
  return { status: res.status(), body: (await res.json().catch(() => null)) as Report };
};
const byGroup = (r: Report) => Object.fromEntries(r.rows.map((x) => [String(x.g0), [x.m0, x.m1]]));

const ROLES = ['Capturista', 'Analista', 'Ajeno'];
const layout = {
  sections: [
    { name: 'Datos', label: 'Datos del pedido', tab: 'General', fields: ['Folio', 'Cliente', 'Zona', 'Fecha', 'Responsable'] },
    { name: 'Entrega', label: 'Entrega', tab: 'General', description: 'Cómo llega el pedido al cliente.',
      fields: ['Envio', { field: 'Direccion', visibleWhen: "Envio = 'Domicilio'", wide: true }] },
    { name: 'Importes', label: 'Importes', tab: 'Importes', fields: ['Monto', 'Costo'] },
    // Para el capturista la condición lee un campo que no ve: se ignora y la sección se muestra siempre.
    { name: 'Revision', label: 'Revisión', tab: 'Importes', columns: 1, visibleWhen: 'Costo > 100', fields: ['Notas'] },
  ],
};

let pedidoNorte = 0;

test('preparar: roles, catálogos con layout, vistas, reportes, tablero y datos', async () => {
  const admin = await as('admin');
  const roleIds: Record<string, number> = {};
  const flags = { canManageModel: false, canManageUsers: false, canViewAudit: false };
  for (const name of ROLES) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 007', ...flags, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    roleIds[name] = (await res.json()).id;
  }

  await applyPlan(admin, { description: 'Spec 007 · pedidos', operations: [
    { op: 'createEntity', name: 'Zona', label: 'Zona', labelPlural: 'Zonas', icon: 'map', displayField: 'Nombre', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true },
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
    ] },
    { op: 'createEntity', name: 'Pedido', label: 'Pedido', labelPlural: 'Pedidos', icon: 'package', displayField: 'Folio', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'PE-', padding: 4 },
      { name: 'Cliente', label: 'Cliente', type: 'text', required: true },
      { name: 'Zona', label: 'Zona', type: 'reference', referenceEntity: 'Zona' },
      { name: 'Fecha', label: 'Fecha', type: 'date', defaultValue: 'TODAY()' },
      { name: 'Responsable', label: 'Responsable', type: 'user', defaultValue: 'CURRENTUSER()' },
      { name: 'Envio', label: 'Envío', type: 'choice', options: ['Recoger', 'Domicilio'], defaultValue: "'Recoger'" },
      { name: 'Direccion', label: 'Dirección', type: 'text', required: true, showInList: false },
      { name: 'Monto', label: 'Monto', type: 'decimal' },
      { name: 'Costo', label: 'Costo', type: 'decimal', showInList: false },
      { name: 'Notas', label: 'Notas', type: 'longtext', showInList: false },
      { name: 'Urgente', label: 'Urgente', type: 'boolean' },
    ] },
    { op: 'createEntity', name: 'PedidoPartida', label: 'Partida de pedido', labelPlural: 'Partidas de pedido', fields: [
      { name: 'Pedido', label: 'Pedido', type: 'parent', referenceEntity: 'Pedido' },
      { name: 'Concepto', label: 'Concepto', type: 'text', required: true },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal' },
    ] },
    { op: 'addField', entity: 'Pedido', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'PedidoPartida' } },
    { op: 'setWorkflow', entity: 'Pedido', workflow: {
      initial: 'Abierto',
      states: [{ name: 'Abierto', label: 'Abierto', color: 'accent' }, { name: 'Cerrado', label: 'Cerrado', color: 'success' }],
      transitions: [{ name: 'cerrar', label: 'Cerrar', from: ['Abierto'], to: 'Cerrado' }],
    } },
    { op: 'setSecurity', entity: 'Zona', security: { records: [
      { roles: ['Capturista'], where: "Clave = 'N'", access: 'read' }, { roles: ['Analista'] },
    ] } },
    { op: 'setSecurity', entity: 'Pedido', security: {
      owner: 'Responsable',
      fields: [{ field: 'Costo', read: ['Analista'] }],
      records: [{ roles: ['Capturista'], scope: 'own' }, { roles: ['Analista'] }],
    } },
    { op: 'setLayout', entity: 'Pedido', layout },
    { op: 'setViews', entity: 'Pedido', views: [
      { name: 'Urgentes', label: 'Urgentes', roles: ['Analista'], default: true, columns: ['Folio', 'Cliente', 'Monto'],
        filters: [{ field: 'Urgente', op: 'eq', value: true }], sort: 'Monto', dir: 'desc' },
      { name: 'Caros', label: 'Caros', filters: [{ field: 'Costo', op: 'gt', value: 100 }] },
      { name: 'Mios', label: 'Mis pedidos', filters: [{ field: 'Responsable', op: 'eq', value: '$me' }] },
    ] },
    { op: 'setReports', entity: 'Pedido', reports: [
      { name: 'PorZona', label: 'Pedidos por zona', groupBy: [{ field: 'Zona' }], measures: [{ fn: 'count' }, { fn: 'sum', field: 'Monto' }], chart: 'bar' },
      { name: 'PorMes', label: 'Pedidos por mes', groupBy: [{ field: 'Fecha', period: 'month' }], measures: [{ fn: 'sum', field: 'Monto' }, { fn: 'count' }], chart: 'line' },
      { name: 'PorCliente', label: 'Pedidos por cliente', groupBy: [{ field: 'Cliente' }], measures: [{ fn: 'sum', field: 'Monto' }] },
      { name: 'PorClaveZona', label: 'Por clave de zona', groupBy: [{ field: 'Zona.Clave' }], measures: [{ fn: 'count' }] },
      { name: 'Total', label: 'Pedidos', measures: [{ fn: 'count', label: 'Pedidos' }, { fn: 'sum', field: 'Monto', label: 'Monto' }] },
      { name: 'CostoTotal', label: 'Costo total', measures: [{ fn: 'sum', field: 'Costo' }] },
      { name: 'Abiertos', label: 'Abiertos', measures: [{ fn: 'count' }], filter: "Estado = 'Abierto'" },
    ] },
    { op: 'setReports', entity: 'PedidoPartida', reports: [
      { name: 'CantidadPorZona', label: 'Cantidad por zona', groupBy: [{ field: 'Pedido.Zona' }], measures: [{ fn: 'sum', field: 'Cantidad' }] },
    ] },
    { op: 'setDashboard', dashboard: { name: 'Pedidos', label: 'Pedidos', icon: 'package', description: 'Tablero de prueba', widgets: [
      { type: 'kpi', entity: 'Pedido', report: 'Total', measure: 1, label: 'Monto vendido' },
      { type: 'kpi', entity: 'Pedido', report: 'CostoTotal' },
      { type: 'chart', entity: 'Pedido', report: 'PorZona', measure: 1 },
      { type: 'table', entity: 'Pedido', report: 'PorMes', wide: true },
    ] } },
  ] });

  const perms = [
    { entity: 'Pedido', canRead: true, canCreate: true, canUpdate: true, canDelete: false },
    { entity: 'Zona', canRead: true, canCreate: false, canUpdate: false, canDelete: false },
  ];
  for (const name of ['Capturista', 'Analista']) {
    const res = await admin.put(`/api/admin/roles/${roleIds[name]}`, { data: { name, description: 'E2E spec 007', ...flags, permissions: perms } });
    expect(res.status(), await res.text()).toBe(200);
  }
  for (const [w, role] of Object.entries(USERS)) {
    const res = await admin.post('/api/admin/users', { data: { username: username(w as Who), displayName: `${w} E2E`, password: PWD, roleIds: [roleIds[role]] } });
    expect(res.status(), await res.text()).toBe(201);
  }

  const zona = async (Clave: string, Nombre: string) => (await (await admin.post('/api/data/Zona', { data: { Clave, Nombre } })).json()).id as number;
  const norte = await zona('N', 'Norte');
  const sur = await zona('S', 'Sur');
  const pedido = async (w: Who, data: object) => {
    const res = await (await as(w)).post('/api/data/Pedido', { data: { Envio: 'Recoger', ...data } });
    expect(res.status(), await res.text()).toBe(201);
    return res.json();
  };
  pedidoNorte = (await pedido('cap1', { Cliente: 'Acme', Zona: norte, Fecha: '2026-01-15', Monto: 100, Urgente: true, Partidas: [{ Concepto: 'Caja', Cantidad: 3 }] })).id;
  await pedido('cap1', { Cliente: '=HYPERLINK("http://x")', Zona: norte, Fecha: '2026-02-10', Monto: 200 });
  // cap2 no ve la zona Sur (regla where de Zona), pero sí puede elegirla el administrador para él: se crea como analista.
  await pedido('analista', { Cliente: 'Beta', Zona: sur, Fecha: '2026-02-20', Monto: 50, Costo: 500, Urgente: true, Partidas: [{ Concepto: 'Tubo', Cantidad: 7 }] });
  const cap2 = await (await as('analista')).get('/api/users/lookup', { params: { search: username('cap2') } });
  const cap2Id = (await cap2.json())[0].id;
  // El pedido de cap2 en la zona Sur: él lo ve, pero no la zona (cuenta como «sin acceso» en sus reportes).
  const res = await (await as('admin')).post('/api/data/Pedido', { data: { Cliente: 'Gamma', Zona: sur, Fecha: '2026-02-21', Monto: 70, Envio: 'Recoger', Responsable: cap2Id } });
  expect(res.status(), await res.text()).toBe(201);
});

test('H1 CA1 · CA2 · CA3: secciones, pestañas, condición en vivo y la pestaña con el error se abre sola', async ({ browser }) => {
  const page = await pageAs(browser, username('cap1'));
  await page.goto('/c/Pedido/new');
  const tabs = page.getByRole('tablist', { name: 'Secciones de pedido' });
  await expect(tabs.getByRole('tab')).toHaveText(['General', 'Importes']);
  await expect(page.getByRole('heading', { name: 'Datos del pedido' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Entrega' })).toBeVisible();
  await expect(page.getByText('Cómo llega el pedido al cliente.')).toBeVisible();

  // CA2: «Dirección» solo con envío a domicilio (lo decide el servidor mientras se captura).
  await expect(page.getByLabel(/^Dirección/)).toHaveCount(0);
  await page.getByLabel(/^Envío/).selectOption('Domicilio');
  await expect(page.getByLabel(/^Dirección/)).toBeVisible();
  await page.getByLabel(/^Envío/).selectOption('Recoger');
  await expect(page.getByLabel(/^Dirección/)).toHaveCount(0);

  // Pestaña «Importes»: el costo está en el layout pero es oculto para el capturista (006); «Otros datos» trae lo no mencionado.
  await tabs.getByRole('tab', { name: 'Importes' }).click();
  await expect(page.getByLabel(/^Monto/)).toBeVisible();
  await expect(page.getByLabel(/^Costo/)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Otros datos' })).toBeVisible();
  await expect(page.getByLabel('Urgente')).toBeVisible();
  // CA4: la condición de «Revisión» lee el costo, que no ve: se ignora y la sección se muestra.
  await expect(page.getByRole('heading', { name: 'Revisión' })).toBeVisible();
  // Regresión (spec 007, F-02): display:flex anulaba el atributo hidden y la pestaña inactiva seguía a la vista.
  await expect(page.getByLabel(/^Cliente/)).toBeHidden();
  await expectAccessible(page);

  // CA3 (UI): al guardar sin cliente, se abre la pestaña «General» y se enfoca el campo.
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(tabs.getByRole('tab', { name: 'General' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByLabel(/^Cliente/)).toBeFocused();
  await page.getByLabel(/^Cliente/).fill('Delta');
  // Un obligatorio oculto por su condición (dirección con «Recoger») no se exige.
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await page.context().close();
});

test('H1 CA2 · CA4: el servidor exige solo lo visible y nunca evalúa una condición con un dato oculto', async () => {
  const cap1 = await as('cap1');
  const missing = await cap1.post('/api/data/Pedido', { data: { Cliente: 'Sin dirección', Envio: 'Domicilio' } });
  expect(missing.status()).toBe(400);
  expect(JSON.stringify((await missing.json()).errors)).toContain('Dirección es obligatorio');
  expect((await cap1.post('/api/data/Pedido', { data: { Cliente: 'Recoge', Envio: 'Recoger' } })).status()).toBe(201);

  // /compute devuelve qué ocultar con los valores capturados.
  const comp = await (await cap1.post('/api/data/Pedido/compute', { data: { values: { Envio: 'Domicilio' } } })).json();
  expect(comp.values).toBeDefined();
  expect(comp.layout.hiddenFields).not.toContain('Direccion');
  expect(comp.layout.hiddenSections).not.toContain('Revision'); // el capturista no ve el costo: condición ignorada

  const analista = await as('analista');
  const low = await (await analista.post('/api/data/Pedido/compute', { data: { values: { Envio: 'Recoger', Costo: 50 } } })).json();
  expect(low.layout.hiddenSections).toContain('Revision');
  expect(low.layout.hiddenFields).toContain('Direccion');
  const high = await (await analista.post('/api/data/Pedido/compute', { data: { values: { Costo: 500 } } })).json();
  expect(high.layout.hiddenSections).not.toContain('Revision');
  // El registro también trae su estado de layout.
  const row = await (await cap1.get(`/api/data/Pedido/${pedidoNorte}`)).json();
  expect(row._layout.hiddenFields).toContain('Direccion');
  expect(row.Costo).toBeUndefined();
});

test('H1 CA5: la vista previa rechaza layouts inválidos', async () => {
  const res = await (await as('admin')).post('/api/model/plan/preview', { data: { description: 'mal', operations: [
    { op: 'setLayout', entity: 'Pedido', layout: { sections: [
      { name: 'A1', label: 'A', fields: ['Cliente', 'NoExiste'] },
      { name: 'B1', label: 'B', visibleWhen: 'Inventado = 1', fields: ['Cliente', 'Partidas'] },
    ] } },
    { op: 'setLayout', entity: 'PedidoPartida', layout: { sections: [{ name: 'A1', label: 'A', fields: ['Concepto'] }] } },
  ] } });
  const preview = await res.json();
  expect(preview.valid).toBe(false);
  const text = preview.errors.map((e: { message: string }) => e.message).join(' | ');
  expect(text).toContain("'NoExiste' no existe");
  expect(text).toContain('ya está en otra sección');
  expect(text).toContain('Inventado');
  expect(text).toContain('no se coloca en el layout');
  expect(text).toContain('es una partida');
});

test('H2 CA1 · CA2 · CA3: vista predeterminada, filtros, columnas y vista personal desde el listado', async ({ browser }) => {
  const page = await pageAs(browser, username('analista'));
  await page.goto('/c/Pedido');
  // CA3: la vista compartida predeterminada del rol se aplica al abrir (columnas, filtro y orden).
  await expect(page).toHaveURL(/view=Urgentes/);
  await expect(page.getByLabel('Vista', { exact: true })).toHaveValue('Urgentes');
  const table = page.getByRole('table');
  await expect(table.getByRole('columnheader')).toHaveText([/Folio/, /Cliente/, /Monto/, 'Actualizado']);
  await expect(table.getByRole('row')).toHaveCount(3); // encabezado + 2 urgentes
  await expect(page.getByRole('list', { name: 'Filtros aplicados' })).toContainText('Urgente es igual a Sí');

  // CA1: «Todos los registros», luego un filtro propio y columnas elegidas.
  await page.getByLabel('Vista', { exact: true }).selectOption('todos');
  await expect(table.getByRole('row')).toHaveCount(1 + 6);
  await page.getByRole('button', { name: /^Filtros/ }).click();
  let dialog = page.getByRole('dialog', { name: 'Filtros' });
  await dialog.getByLabel('Campo 1', { exact: true }).selectOption('Cliente');
  await dialog.getByLabel('Condición 1', { exact: true }).selectOption('contains');
  await dialog.getByLabel('Valor 1', { exact: true }).fill('ac');
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Aplicar' }).click();
  await expect(page.getByRole('list', { name: 'Filtros aplicados' })).toContainText('Cliente contiene ac');
  await expect(table.getByRole('row')).toHaveCount(2); // Acme
  await expect(page).toHaveURL(/filters=/);

  await page.getByRole('button', { name: 'Columnas' }).click();
  dialog = page.getByRole('dialog', { name: 'Columnas' });
  await dialog.getByLabel('Costo').check();
  await dialog.getByLabel('Zona').uncheck();
  await dialog.getByRole('button', { name: 'Aplicar' }).click();
  await expect(table.getByRole('columnheader', { name: /Costo/ })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: /Zona/ })).toHaveCount(0);

  // CA2: guardar como vista personal; sigue ahí al recargar y se puede eliminar.
  await page.getByRole('button', { name: 'Guardar vista' }).click();
  dialog = page.getByRole('dialog', { name: 'Guardar vista' });
  await dialog.getByLabel(/^Nombre de la vista/).fill('Clientes con «ac»');
  await dialog.getByRole('button', { name: 'Guardar' }).click();
  await expect(page.getByText('Vista guardada')).toBeVisible();
  await page.goto('/c/Pedido?view=todos');
  await page.getByLabel('Vista', { exact: true }).selectOption({ label: 'Clientes con «ac»' });
  await expect(page.getByRole('list', { name: 'Filtros aplicados' })).toContainText('Cliente contiene ac');
  await expect(table.getByRole('columnheader', { name: /Costo/ })).toBeVisible();
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Eliminar vista' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Eliminar' }).click();
  await expect(page.getByText('Vista eliminada')).toBeVisible();
  await expect(page.getByLabel('Vista', { exact: true }).locator('option', { hasText: 'Clientes con «ac»' })).toHaveCount(0);
  await page.context().close();
});

test('H2 CA4: vistas y filtros respetan la seguridad por campo y las vistas personales son privadas', async () => {
  const cap1 = await as('cap1');
  const analista = await as('analista');
  // Filtrar por un campo oculto: 400 (ordenar por él se ignora, como en la 006).
  const hidden = await cap1.get('/api/data/Pedido', { params: { filters: JSON.stringify([{ field: 'Costo', op: 'gt', value: 100 }]) } });
  expect(hidden.status()).toBe(400);
  const bad = await cap1.get('/api/data/Pedido', { params: { filters: '{no es json' } });
  expect(bad.status()).toBe(400);
  const ok = await (await cap1.get('/api/data/Pedido', { params: { filters: JSON.stringify([{ field: 'Responsable', op: 'eq', value: '$me' }]), pageSize: 200 } })).json();
  expect(ok.items.length).toBeGreaterThanOrEqual(2);

  // Compartidas: «Urgentes» es solo para analistas y «Caros» filtra por el costo: al capturista no se le ofrecen.
  const views = await (await cap1.get('/api/views/Pedido')).json();
  expect(views.shared.map((v: { name: string }) => v.name)).toEqual(['Mios']);
  const aViews = await (await analista.get('/api/views/Pedido')).json();
  expect(aViews.shared.map((v: { name: string }) => v.name)).toEqual(['Urgentes', 'Caros', 'Mios']);

  // Personales: el capturista no puede guardar una con un campo oculto; la del analista no la ve ni la toca nadie más.
  expect((await cap1.post('/api/views/Pedido', { data: { label: 'Con costo', filters: [{ field: 'Costo', op: 'gt', value: 1 }] } })).status()).toBe(400);
  expect((await cap1.post('/api/views/Pedido', { data: { label: 'Por costo', sort: 'Costo' } })).status()).toBe(400);
  const mine = await (await analista.post('/api/views/Pedido', { data: { label: 'Privada', columns: ['Cliente', 'Costo'] } })).json();
  expect((await analista.post('/api/views/Pedido', { data: { label: 'Privada' } })).status()).toBe(400); // nombre repetido
  expect((await (await cap1.get('/api/views/Pedido')).json()).personal).toEqual([]);
  expect((await cap1.put(`/api/views/Pedido/${mine.id}`, { data: { label: 'Mía ahora' } })).status()).toBe(404);
  expect((await cap1.delete(`/api/views/Pedido/${mine.id}`)).status()).toBe(404);
  expect((await analista.delete(`/api/views/Pedido/${mine.id}`)).status()).toBe(204);
  // Sin permiso de lectura del catálogo, tampoco hay vistas.
  expect((await (await as('ajeno')).get('/api/views/Pedido')).status()).toBe(403);
  // Las vistas personales quedan en la auditoría.
  const audit = await (await (await as('admin')).get('/api/audit', { params: { entity: 'Pedido', pageSize: 50, page: 1 } })).json();
  expect(audit.items.some((a: { action: string }) => a.action === 'view.change')).toBe(true);
});

test('H3 CA1 · CA2 · CA3: agrupaciones, totales y seguridad por registro y por campo en reportes', async () => {
  // Analista: ve todo.
  const a = (await report('analista', 'PorZona')).body;
  // Los dos pedidos sin zona (capturados en H1 sin monto) son «(sin valor)».
  expect(byGroup(a)).toEqual({ '(sin valor)': [2, 0], Norte: [2, 300], Sur: [2, 120] });
  expect(a.totals).toEqual({ m0: 6, m1: 420 });
  // Capturista 1: solo sus pedidos.
  const c1 = (await report('cap1', 'PorZona')).body;
  expect(byGroup(c1)).toEqual({ '(sin valor)': [2, 0], Norte: [2, 300] });
  // Capturista 2: su pedido es de la zona Sur, que no ve (regla where de Zona).
  expect(byGroup((await report('cap2', 'PorZona')).body)).toEqual({ '(sin acceso)': [1, 70] });
  expect(byGroup((await report('cap2', 'PorClaveZona')).body)).toEqual({ '(sin acceso)': [1, undefined] });
  // Por periodo.
  expect(byGroup((await report('analista', 'PorMes')).body)).toMatchObject({ '2026-01': [100, 1], '2026-02': [320, 3] });
  // Partidas por el documento relacionado: hereda el alcance del documento.
  expect(byGroup((await report('analista', 'CantidadPorZona', undefined, 'PedidoPartida')).body)).toEqual({ Norte: [3, undefined], Sur: [7, undefined] });
  expect(byGroup((await report('cap1', 'CantidadPorZona', undefined, 'PedidoPartida')).body)).toEqual({ Norte: [3, undefined] });
  // Regresión (revisión exploratoria 007, F-04): una suma sin registros es 0, no vacío (el indicador mostraba «—»).
  expect((await report('analista', 'Total', [{ field: 'Monto', op: 'gt', value: 99999 }])).body.totals).toEqual({ m0: 0, m1: 0 });
  // Filtro fijo y filtros del usuario.
  expect((await report('analista', 'Abiertos')).body.totals.m0).toBe(6);
  expect(byGroup((await report('analista', 'PorZona', [{ field: 'Monto', op: 'gt', value: 60 }])).body)).toEqual({ Norte: [2, 300], Sur: [1, 70] });

  // Campo oculto: el reporte del costo no existe para el capturista, ni se puede filtrar por él.
  expect((await report('cap1', 'CostoTotal')).status).toBe(404);
  expect((await report('analista', 'CostoTotal')).body.totals.m0).toBe(500);
  expect((await report('cap1', 'PorZona', [{ field: 'Costo', op: 'gt', value: 1 }])).status).toBe(400);
  const list = await (await (await as('cap1')).get('/api/reports')).json();
  const names = list.map((r: { name: string }) => r.name);
  expect(names).toContain('PorZona');
  expect(names).not.toContain('CostoTotal');
  expect(names).toContain('CantidadPorZona');
  // Sin permiso de lectura: ningún reporte del catálogo.
  expect((await report('ajeno', 'PorZona')).status).toBe(404);
  expect((await (await (await as('ajeno')).get('/api/reports')).json()).filter((r: { entity: string }) => r.entity.startsWith('Pedido'))).toEqual([]);
});

test('H3 CA5: exportación a CSV (sin inyección de fórmulas) y a Excel', async () => {
  const cap1 = await as('cap1');
  const csv = await cap1.get('/api/reports/Pedido/PorCliente/export', { params: { format: 'csv' } });
  expect(csv.status()).toBe(200);
  expect(csv.headers()['content-type']).toContain('text/csv');
  expect(csv.headers()['content-disposition']).toMatch(/attachment; filename=Pedido-PorCliente-\d{8}\.csv/);
  const text = (await csv.body()).toString('utf8');
  expect(text.charCodeAt(0)).toBe(0xfeff);
  expect(text).toContain('Cliente,Monto');
  expect(text).toContain('"\'=HYPERLINK(""http://x"")",200.00');
  expect(text).not.toContain('Beta'); // registro de otro usuario
  expect(text).toContain('Total,');

  const xlsx = await cap1.get('/api/reports/Pedido/PorCliente/export', { params: { format: 'xlsx' } });
  expect(xlsx.status()).toBe(200);
  expect(xlsx.headers()['content-type']).toContain('spreadsheetml');
  expect((await xlsx.body()).subarray(0, 2).toString()).toBe('PK');
  expect((await cap1.get('/api/reports/Pedido/CostoTotal/export', { params: { format: 'csv' } })).status()).toBe(404);
  expect((await cap1.get('/api/reports/Pedido/PorCliente/export', { params: { format: 'pdf' } })).status()).toBe(400);
});

test('H3 CA4 · CA6: el reporte en pantalla con gráfica, tabla, filtros y descargas', async ({ browser }) => {
  const page = await pageAs(browser, username('analista'));
  const nav = page.getByLabel('Navegación principal');
  await nav.getByRole('link', { name: 'Reportes' }).click();
  await page.getByRole('link', { name: /Pedidos por zona/ }).click();
  await expect(page.getByRole('heading', { name: 'Pedidos por zona' })).toBeVisible();
  await expect(page.getByRole('img', { name: /Registros por zona: .*Norte 2; Sur 2/ })).toBeVisible();
  const table = page.getByRole('table');
  await expect(table.getByRole('row', { name: /Norte 2 300\.00/ })).toBeVisible();
  await expect(table.getByRole('row', { name: /Total 6 420\.00/ })).toBeVisible();
  await expect(page.getByRole('link', { name: 'CSV' })).toHaveAttribute('href', /export\?format=csv/);
  await expect(page.getByRole('link', { name: 'Excel' })).toHaveAttribute('href', /format=xlsx/);
  await expectAccessible(page);

  await page.getByRole('button', { name: /^Filtros/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Filtros' });
  await dialog.getByLabel('Campo 1', { exact: true }).selectOption('Urgente');
  await dialog.getByRole('button', { name: 'Aplicar' }).click();
  await expect(table.getByRole('row', { name: /Total 2 150\.00/ })).toBeVisible();
  await expect(page.getByRole('link', { name: 'CSV' })).toHaveAttribute('href', /filters=/);
  const download = page.waitForEvent('download');
  await page.getByRole('link', { name: 'Excel' }).click();
  expect((await download).suggestedFilename()).toMatch(/^Pedido-PorZona-\d{8}\.xlsx$/);

  await page.context().close();
});

// Regresión (revisión exploratoria 007, F-03): en móvil los botones del reporte tapaban un título con palabras largas.
test('H3 CA4: en móvil los botones del reporte no tapan el título', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/reports/Requisicion/PorEstado');
  const title = (await page.getByRole('heading', { name: 'Requisiciones por estado', level: 1 }).boundingBox())!;
  // Sin la corrección, el título se encogía a ~0 px de ancho y su texto quedaba debajo de los botones.
  expect(title.width, 'el título tiene su propio espacio').toBeGreaterThan(150);
  for (const name of ['Filtros', 'CSV', 'Excel']) {
    const b = (await page.getByRole('main').getByRole(name === 'Filtros' ? 'button' : 'link', { name: new RegExp(`^${name}`) }).boundingBox())!;
    const overlaps = b.x < title.x + title.width && title.x < b.x + b.width && b.y < title.y + title.height && title.y < b.y + b.height;
    expect(overlaps, `«${name}» no debe tapar el título`).toBe(false);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
});

test('H4 CA1 · CA2 · CA3: tableros con indicadores y gráficas, filtrados por la seguridad de cada reporte', async ({ browser }) => {
  const a = await (await (await as('analista')).get('/api/dashboards/Pedidos')).json();
  expect(a.widgets.map((w: { label: string }) => w.label)).toEqual(['Monto vendido', 'Costo total', 'Pedidos por zona', 'Pedidos por mes']);
  expect(a.widgets[0].result.totals.m1).toBe(420);
  expect(a.definition).toBeNull(); // la definición solo la recibe quien modela
  const c = await (await (await as('cap1')).get('/api/dashboards/Pedidos')).json();
  expect(c.widgets.map((w: { label: string }) => w.label)).toEqual(['Monto vendido', 'Pedidos por zona', 'Pedidos por mes']);
  expect(c.widgets[0].result.totals.m1).toBe(300);
  // Sin ninguna tarjeta visible, el tablero no existe para el usuario.
  expect((await (await as('ajeno')).get('/api/dashboards/Pedidos')).status()).toBe(404);
  expect((await (await (await as('ajeno')).get('/api/dashboards')).json()).map((d: { name: string }) => d.name)).not.toContain('Pedidos');

  const page = await pageAs(browser, username('cap1'));
  const nav = page.getByLabel('Navegación principal');
  await nav.getByRole('link', { name: 'Tableros' }).click();
  await page.getByRole('main').getByRole('link', { name: /Pedidos/ }).click();
  await expect(page).toHaveURL(/\/dashboards\/Pedidos$/);
  await expect(page.getByRole('region', { name: 'Monto vendido' })).toContainText('300.00');
  await expect(page.getByRole('region', { name: 'Costo total' })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Pedidos por zona' }).getByRole('img')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Editar (JSON)' })).toHaveCount(0);
  await expectAccessible(page);
  // Móvil: sin desbordamiento horizontal.
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await page.context().close();
});

test('H5: el modelador edita layout, vistas, reportes y tableros como JSON validado', async ({ page }) => {
  await page.goto('/admin/model?edit=Pedido');
  await expect(page.getByLabel('Layout del formulario (JSON)')).toHaveValue(/"Revision"/);
  await expect(page.getByLabel('Vistas compartidas (JSON)')).toHaveValue(/"Urgentes"/);
  await expect(page.getByLabel('Reportes (JSON)')).toHaveValue(/"PorZona"/);
  await expect(page.getByLabel('Layout del formulario (JSON)')).not.toHaveValue(/null/);
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
  const box = page.getByLabel('Reportes (JSON)');
  await box.fill((await box.inputValue()).replace('"Pedidos por zona"', '"Pedidos por zona (editado)"'));
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/Definir 7 reporte\(s\) en 'Pedido'/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  expect((await report('admin', 'PorZona')).body).toMatchObject({ label: 'Pedidos por zona (editado)' });

  // Tablero: el modelador lo edita desde su página y pasa por la misma vista previa.
  await page.goto('/dashboards/Pedidos');
  await page.getByRole('button', { name: 'Editar (JSON)' }).click();
  const editor = page.getByLabel('Tablero (JSON)');
  await expect(editor).toHaveValue(/"Tablero de prueba"/);
  await editor.fill((await editor.inputValue()).replace('"Tablero de prueba"', '"Tablero editado"'));
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(page.getByRole('dialog').getByText(/Actualizar el tablero 'Pedidos'/)).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Tablero editado')).toBeVisible();
});

// Regresión (revisión exploratoria 006, F-03): el modelador comparaba la posición en pantalla con un displayOrder no
// consecutivo (la orden de compra tiene el estado en -1 y huecos) y ofrecía «Revisar cambios» sin cambios.
test('H5 CA1 (F-03): sin cambios en un catálogo con orden no consecutivo no se ofrece revisar', async ({ page }) => {
  await page.goto('/admin/model?edit=OrdenCompra');
  await expect(page.getByLabel('Layout del formulario (JSON)')).toHaveValue(/"Seguimiento"/);
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
  // Al mover un campo sí hay cambio, y solo de orden.
  await page.getByRole('button', { name: 'Bajar campo' }).nth(2).click();
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(page.getByRole('dialog').getByText(/Actualizar campo/).first()).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Volver a editar' }).click();
});

test('H6 CA1 · CA2: Compras 1.4.0 — orden con pestañas, tipo de cambio solo fuera de MXN y vistas compartidas', async ({ browser }) => {
  const page = await pageAs(browser, 'e2e-comprador', COMPRAS_PWD);
  await page.goto('/c/OrdenCompra/new');
  const tabs = page.getByRole('tablist', { name: 'Secciones de orden de compra' });
  await expect(tabs.getByRole('tab')).toHaveText(['General', 'Seguimiento']);
  await expect(page.getByLabel(/^Moneda/)).toBeVisible();
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveCount(0); // MXN por defecto
  await tabs.getByRole('tab', { name: 'Seguimiento' }).click();
  await expect(page.getByRole('heading', { name: 'Autorización' })).toBeVisible();
  await expect(page.getByLabel(/^Motivo de rechazo/)).toHaveCount(0); // solo en una orden rechazada
  await page.goto('/c/OrdenCompra');
  await expect(page.getByLabel('Vista', { exact: true }).locator('option')).toContainText(['Todos los registros', 'Pendientes de recibir']);
  await expect(page.getByLabel('Vista', { exact: true }).locator('option', { hasText: 'Por autorizar' })).toHaveCount(0); // solo gerente y director
  await page.context().close();

  // Con una moneda distinta de MXN el servidor muestra el tipo de cambio.
  const c = await apiAs('e2e-comprador', COMPRAS_PWD);
  const usd = (await (await c.get('/api/data/Moneda', { params: { search: 'USD' } })).json()).items[0];
  const comp = await (await c.post('/api/data/OrdenCompra/compute', { data: { values: { Moneda: usd.id } } })).json();
  expect(comp.layout.hiddenFields).not.toContain('TipoCambio');
});

test('H6 CA3 · CA4: reportes y tablero de Compras; el almacenista no ve importes', async () => {
  const admin = await as('admin');
  const all = (await (await admin.get('/api/reports')).json()).map((r: { name: string }) => r.name);
  expect(all).toEqual(expect.arrayContaining(['ComprasPorProveedor', 'ComprasPorMes', 'MontoComprado', 'OrdenesAbiertas', 'PorEstado', 'ProductosMasComprados']));
  const almacen = await apiAs('e2e-almacen', COMPRAS_PWD);
  const mine = (await (await almacen.get('/api/reports')).json()).map((r: { name: string }) => r.name);
  expect(mine).toEqual(expect.arrayContaining(['OrdenesAbiertas', 'ProductosMasComprados']));
  expect(mine).not.toContain('ComprasPorProveedor');
  expect(mine).not.toContain('MontoComprado');
  const board = await (await almacen.get('/api/dashboards/Compras')).json();
  expect(board.widgets.map((w: { report: string }) => w.report)).toEqual(['OrdenesAbiertas', 'ProductosMasComprados']);
  const g = await apiAs('e2e-gerente', COMPRAS_PWD);
  const full = await (await g.get('/api/dashboards/Compras')).json();
  expect(full.widgets.map((w: { report: string }) => w.report)).toEqual(['OrdenesAbiertas', 'MontoComprado', 'PorEstado', 'ComprasPorMes', 'ComprasPorProveedor', 'ProductosMasComprados']);
  expect(full.widgets[1].result.totals.m0).toBeGreaterThan(0);
});
