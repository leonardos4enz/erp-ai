// Spec 018 · OAuth2 (client credentials) y API OData. Catálogos propios en su grupo del menú; el cliente se da de alta por
// la UI y se usa como lo haría un servicio: sin cookie ni cabecera anti-CSRF. La suite corre con páginas OData de 3 filas
// (OData__MaxPageSize) para probar @odata.nextLink. El límite del servidor de tokens se prueba en endurecimiento.spec.ts
// (instancia :5092 con el límite por defecto) y la aprobación de clientes en jerarquia.spec.ts (:5093).
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import type { APIRequestContext, APIResponse, Page } from '@playwright/test';
import { ADMIN, BASE_URL, anonApi, apiAs, applyPlan, expect, expectAccessible, test } from './helpers';

test.describe.configure({ mode: 'serial' });

const GROUP = 'Pruebas OData';
type Row = Record<string, unknown> & { Id: number };
type Page18 = { '@odata.context': string; '@odata.count'?: number; '@odata.nextLink'?: string; value: Row[] };
type Client = { id: number; name: string; clientId: string; scopes: string[]; status: string };

const ids: Record<string, number> = {};
const cred = { clientId: '', secret: '' };
let admin: APIRequestContext;

const basic = (id = cred.clientId, secret = cred.secret) => 'Basic ' + Buffer.from(`${id}:${secret}`).toString('base64');
const tokenRequest = (form: Record<string, string>, headers: Record<string, string> = { Authorization: basic() }) =>
  anonApi(headers).then((c) => c.post('/oauth/token', { form }));

async function token(scope?: string): Promise<string> {
  const r = await tokenRequest({ grant_type: 'client_credentials', ...(scope ? { scope } : {}) });
  expect(r.status(), await r.text()).toBe(200);
  return (await r.json()).access_token;
}
const bearer = (t: string) => anonApi({ Authorization: `Bearer ${t}` });
const odata = async (t: string, path: string, params?: Record<string, string>) => (await bearer(t)).get(`/odata/${path}`, { params });
const folios = (p: Page18) => p.value.map((r) => r.Folio).sort();

function sql(statement: string) {
  const db = process.env.E2E_DB ?? 'ErpAi_E2E';
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  execFileSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-I', '-Q', statement]);
}

test.beforeAll(async () => { admin = await apiAs(ADMIN.username, ADMIN.password); });
test.afterAll(async () => {
  // Las suites siguientes y las capturas esperan una sola empresa activa.
  const companies = (await (await admin.get('/api/admin/companies')).json()) as { id: number; code: string; name: string; baseCurrency: string; isActive: boolean }[];
  for (const c of companies.filter((c) => c.code === 'E2E-OD18' && c.isActive))
    await admin.put(`/api/admin/companies/${c.id}`, { data: { name: c.name, baseCurrency: c.baseCurrency, isActive: false } });
  await admin.dispose();
});

test('preparar: catálogos con seguridad por campo, registro y empresa; datos en dos empresas', async () => {
  const flags = { canManageModel: false, canManageUsers: false, canViewAudit: false };
  for (const name of ['Bi18', 'Gerente18']) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 018', ...flags, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    ids[name] = (await res.json()).id;
  }
  await applyPlan(admin, { description: 'Spec 018 · OData', operations: [
    { op: 'createEntity', group: GROUP, name: 'Cliente18', label: 'Cliente OD', labelPlural: 'Clientes OD', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Ciudad', label: 'Ciudad', type: 'text' }] },
    { op: 'createEntity', group: GROUP, name: 'Secreto18', label: 'Secreto OD', labelPlural: 'Secretos OD', fields: [
      { name: 'Clave', label: 'Clave', type: 'text' }] },
    { op: 'createEntity', group: GROUP, name: 'Pedido18', label: 'Pedido OD', labelPlural: 'Pedidos OD', displayField: 'Folio', perCompany: true, fields: [
      { name: 'Folio', label: 'Folio', type: 'text', required: true },
      { name: 'Cliente', label: 'Cliente', type: 'reference', referenceEntity: 'Cliente18' },
      { name: 'Monto', label: 'Monto', type: 'decimal' },
      { name: 'Margen', label: 'Margen', type: 'decimal' },
      { name: 'Fecha', label: 'Fecha', type: 'date' },
      { name: 'Urgente', label: 'Urgente', type: 'boolean' },
      { name: 'Etapa', label: 'Etapa', type: 'choice', options: ['Abierto', 'Cerrado'] },
      { name: 'Notas', label: 'Notas', type: 'longtext' },
      { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'Pedido18Linea' }] },
    { op: 'createEntity', name: 'Pedido18Linea', label: 'Partida OD', labelPlural: 'Partidas OD', fields: [
      { name: 'Pedido', label: 'Pedido', type: 'parent', referenceEntity: 'Pedido18' },
      { name: 'Producto', label: 'Producto', type: 'text' },
      { name: 'Cantidad', label: 'Cantidad', type: 'integer' },
      { name: 'Costo', label: 'Costo', type: 'decimal' }] },
    { op: 'setSecurity', entity: 'Pedido18', security: { fields: [{ field: 'Margen', read: ['Gerente18'] }], records: [{ roles: ['Bi18'], where: 'Monto < 1000' }] } },
    { op: 'setSecurity', entity: 'Pedido18Linea', security: { fields: [{ field: 'Costo', read: ['Gerente18'] }] } },
    { op: 'setSecurity', entity: 'Cliente18', security: { records: [{ roles: ['Bi18'], where: "Ciudad <> 'Oculta'" }] } },
  ] });
  const read = (entity: string) => ({ entity, canRead: true, canCreate: false, canUpdate: false, canDelete: false });
  const res = await admin.put(`/api/admin/roles/${ids.Bi18}`, { data: { name: 'Bi18', description: 'E2E spec 018', ...flags, permissions: [read('Pedido18'), read('Cliente18')] } });
  expect(res.status(), await res.text()).toBe(200);

  const create = async (entity: string, data: object) => {
    const r = await admin.post(`/api/data/${entity}`, { data });
    expect(r.status(), await r.text()).toBe(201);
    return (await r.json()).id as number;
  };
  ids.ana = await create('Cliente18', { Nombre: 'Ana', Ciudad: 'Monterrey' });
  ids.beto = await create('Cliente18', { Nombre: 'Beto', Ciudad: 'Oculta' });
  ids.obrien = await create('Cliente18', { Nombre: "O'Brien", Ciudad: 'Saltillo' });
  await create('Secreto18', { Clave: 'no debe salir' });
  ids.p1 = await create('Pedido18', { Folio: 'P-001', Cliente: ids.ana, Monto: 100, Margen: 10, Fecha: '2026-01-10', Urgente: true, Etapa: 'Abierto',
    Partidas: [{ Producto: 'Tornillo', Cantidad: 2, Costo: 5 }, { Producto: 'Tuerca', Cantidad: 1, Costo: 7 }] });
  ids.p2 = await create('Pedido18', { Folio: 'P-002', Cliente: ids.beto, Monto: 200, Margen: 20, Fecha: '2026-02-15', Urgente: false, Etapa: 'Cerrado',
    Notas: 'Entregar en el andén del ñandú' });
  ids.p3 = await create('Pedido18', { Folio: 'P-003', Cliente: ids.ana, Monto: 5000, Fecha: '2026-02-20', Urgente: true });
  ids.p4 = await create('Pedido18', { Folio: 'P-004', Cliente: ids.obrien, Monto: 300, Fecha: '2026-03-01' });
  ids.p5 = await create('Pedido18', { Folio: 'P-005', Cliente: ids.ana, Monto: 400, Fecha: '2026-03-20', Etapa: 'Abierto' });

  // Spec 010: un pedido en otra empresa no le llega al cliente (que trabaja en la predeterminada, la de quien lo crea).
  const companies = (await (await admin.get('/api/admin/companies')).json()) as { id: number; isDefault: boolean }[];
  const other = await admin.post('/api/admin/companies', { data: { code: 'e2e-od18', name: 'Otra empresa OData', baseCurrency: 'mxn' } });
  expect(other.status(), await other.text()).toBe(200);
  ids.company = (await other.json()).id;
  expect((await admin.post('/api/auth/company', { data: { companyId: ids.company } })).status()).toBe(200);
  ids.p900 = await create('Pedido18', { Folio: 'P-900', Cliente: ids.ana, Monto: 50 });
  expect((await admin.post('/api/auth/company', { data: { companyId: companies.find((c) => c.isDefault)!.id } })).status()).toBe(200);
});

async function clientsTab(page: Page) {
  await page.goto('/admin/integrations');
  await page.getByRole('tab', { name: 'Clientes OAuth' }).click();
  await expect(page.getByRole('tabpanel', { name: 'Clientes OAuth' })).toBeVisible();
}

test('H1 CA1 · CA5 + H3 CA4: el administrador da de alta un cliente con roles y alcances; el secreto se ve una vez', async ({ page }) => {
  await clientsTab(page);
  await expect(page.getByText('URL del token', { exact: true })).toBeVisible();
  await expect(page.getByText(`${BASE_URL}/oauth/token`)).toBeVisible();
  await expect(page.getByText(`${BASE_URL}/odata/`, { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Nuevo cliente' }).click();
  const dialog = page.getByRole('dialog', { name: 'Nuevo cliente OAuth' });
  // Regresión (revisión exploratoria 018, F-01): sin roles, el aviso llega junto con el del nombre y se ve sin desplazar la
  // lista de roles (antes quedaba al final de la lista, fuera de la vista, y «Crear cliente» parecía no hacer nada).
  await dialog.getByRole('button', { name: 'Crear cliente' }).click();
  await expect(dialog.getByText('Escribe un nombre de máximo 100 caracteres.')).toBeVisible();
  const rolesError = dialog.getByText('Elige al menos un rol: define qué puede hacer el servicio.');
  await expect(rolesError).toBeInViewport();
  const firstRole = (await dialog.getByRole('checkbox').first().boundingBox())!;
  expect((await rolesError.boundingBox())!.y).toBeLessThan(firstRole.y); // arriba de la lista, sin importar cuántos roles haya
  await dialog.getByLabel(/^Nombre/).fill('Tablero BI E2E');
  await expect(dialog.getByRole('checkbox', { name: 'Administrador' })).toHaveCount(0);
  await dialog.getByRole('checkbox', { name: 'Bi18' }).check();
  await expect(dialog.getByRole('checkbox', { name: /odata\.read/ })).toBeChecked(); // por defecto
  await dialog.getByRole('checkbox', { name: /^Leer datos/ }).check();
  await expect(dialog.getByLabel('Vida del token')).toHaveValue('15');
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Crear cliente' }).click();

  const shown = page.getByRole('dialog', { name: 'Credenciales del cliente' });
  cred.clientId = (await shown.getByLabel('client_id', { exact: true }).textContent())!;
  cred.secret = (await shown.getByLabel('Valor secreto').textContent())!;
  expect(cred.clientId).toMatch(/^erpcl_[0-9a-f]{14}$/);
  expect(cred.secret).toMatch(/^erpcs_[A-Za-z0-9_-]{43}$/);
  await expectAccessible(page);
  await shown.getByRole('button', { name: 'Ya lo guardé' }).click();

  const row = page.getByRole('row', { name: /Tablero BI E2E/ });
  await expect(row).toContainText(cred.clientId);
  await expect(row).not.toContainText(cred.secret);
  await expect(row.getByText('Vigente')).toBeVisible();
  await expect(row.getByText('OData')).toBeVisible();
  await expect(row.getByText('Leer datos')).toBeVisible();
  await expect(row).toContainText('Token de 15 min');
  await expect(row).toContainText('Nunca');
  // Ni la API ni la auditoría devuelven el secreto.
  expect(await (await admin.get('/api/admin/oauth-clients')).text()).not.toContain(cred.secret.slice(6));
  const audit = await (await admin.get('/api/audit', { params: { page: '1', pageSize: '20' } })).text();
  expect(audit).toContain('integration.oauthclient');
  expect(audit).not.toContain(cred.secret.slice(6));
  // Validación: sin alcances o con un rol de administración, no.
  const adminRole = ((await (await admin.get('/api/admin/roles')).json()) as { id: number; name: string }[]).find((r) => r.name === 'Administrador')!.id;
  const bad = await admin.post('/api/admin/oauth-clients', { data: { name: 'Malo', roleIds: [adminRole], scopes: [], tokenMinutes: 90 } });
  expect(bad.status()).toBe(400);
  expect(Object.keys((await bad.json()).errors).sort()).toEqual(['scopes', 'tokenMinutes']);
  const badRole = await admin.post('/api/admin/oauth-clients', { data: { name: 'Malo', roleIds: [adminRole], scopes: ['odata.read'] } });
  expect((await badRole.json()).errors.roleIds[0]).toMatch(/no puede tener roles de administración/);
});

test('H2 CA1 · CA2 · CA3: el servidor de tokens sigue RFC 6749 (client credentials)', async () => {
  // Basic, sin pedir alcance: recibe todos los del cliente.
  const ok = await tokenRequest({ grant_type: 'client_credentials' });
  expect(ok.status()).toBe(200);
  expect(ok.headers()['cache-control']).toBe('no-store');
  expect(ok.headers()['pragma']).toBe('no-cache');
  const body = await ok.json();
  expect(body).toMatchObject({ token_type: 'Bearer', expires_in: 900, scope: 'odata.read data.read' });
  expect(body.access_token).toMatch(/^erpat_[A-Za-z0-9_-]{43}$/);
  // En el cuerpo y con un subconjunto de alcances.
  const post = await tokenRequest({ grant_type: 'client_credentials', client_id: cred.clientId, client_secret: cred.secret, scope: 'odata.read' }, {});
  expect(post.status()).toBe(200);
  expect((await post.json()).scope).toBe('odata.read');

  const err = async (r: APIResponse, status: number, error: string) => {
    expect(r.status(), await r.text()).toBe(status);
    expect((await r.json()).error).toBe(error);
    expect(r.headers()['cache-control']).toBe('no-store');
  };
  await err(await tokenRequest({ grant_type: 'client_credentials', scope: 'data.write' }), 400, 'invalid_scope');
  await err(await tokenRequest({ grant_type: 'client_credentials', scope: 'todo' }), 400, 'invalid_scope');
  await err(await tokenRequest({ grant_type: 'password' }), 400, 'unsupported_grant_type');
  await err(await tokenRequest({}), 400, 'invalid_request');
  await err(await tokenRequest({ grant_type: 'client_credentials', client_secret: cred.secret }), 400, 'invalid_request'); // Basic y cuerpo a la vez
  const json = await (await anonApi({ Authorization: basic() })).post('/oauth/token', { data: { grant_type: 'client_credentials' } });
  await err(json, 400, 'invalid_request');
  const repeated = await (await anonApi({ Authorization: basic(), 'Content-Type': 'application/x-www-form-urlencoded' }))
    .post('/oauth/token', { data: 'grant_type=client_credentials&grant_type=client_credentials' });
  await err(repeated, 400, 'invalid_request');

  // Secreto malo, cliente inexistente o basura: la misma respuesta, sin decir cuál.
  const wrongSecret = cred.secret.slice(0, -1) + (cred.secret.endsWith('A') ? 'B' : 'A');
  const bad = await tokenRequest({ grant_type: 'client_credentials' }, { Authorization: basic(cred.clientId, wrongSecret) });
  await err(bad, 401, 'invalid_client');
  expect(bad.headers()['www-authenticate']).toBe('Basic realm="erp"');
  const texts = new Set<string>([JSON.stringify(await bad.json())]);
  for (const [id, s] of [['erpcl_00000000000000', cred.secret], ['basura', 'basura']]) {
    const r = await tokenRequest({ grant_type: 'client_credentials' }, { Authorization: basic(id, s) });
    await err(r, 401, 'invalid_client');
    texts.add(JSON.stringify(await r.json()));
  }
  expect(texts.size).toBe(1);
  expect((await (await anonApi({})).get('/oauth/token')).status()).toBe(405);

  // Vencido: 401 con invalid_token (se adelanta su vencimiento en la base desechable).
  const t = await token();
  expect((await odata(t, 'Pedido18')).status()).toBe(200);
  sql(`UPDATE meta.OAuthToken SET ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE TokenHash = 0x${createHash('sha256').update(t).digest('hex')}`);
  const expired = await odata(t, 'Pedido18');
  expect(expired.status()).toBe(401);
  expect(expired.headers()['www-authenticate']).toBe('Bearer error="invalid_token"');
  // Un token inventado con buen formato tampoco.
  expect((await odata('erpat_' + 'a'.repeat(43), 'Pedido18')).status()).toBe(401);
});

test('H2 CA4: cada alcance abre solo su superficie y el rol sigue mandando; quitar un alcance aplica ya', async () => {
  const onlyOData = await token('odata.read');
  const o = await bearer(onlyOData);
  expect((await o.get('/odata/Pedido18')).status()).toBe(200);
  const denied = await o.get('/api/data/Pedido18');
  expect(denied.status()).toBe(403);
  expect(denied.headers()['www-authenticate']).toBe('Bearer error="insufficient_scope", scope="data.read"');
  for (const path of ['/api/admin/users', '/api/model/entities', '/api/audit', '/api/admin/oauth-clients', '/api/users/lookup'])
    expect((await o.get(path)).status(), path).toBe(403);
  expect((await (await o.get('/api/auth/me')).json()).username).toBe(`svc.${cred.clientId}`);

  const dataRead = await token('data.read');
  const d = await bearer(dataRead);
  const list = await (await d.get('/api/data/Pedido18', { params: { pageSize: '50' } })).json();
  expect(list.items.map((i: { Folio: string }) => i.Folio).sort()).toEqual(['P-001', 'P-002', 'P-004', 'P-005']); // alcance y empresa
  expect(list.items.some((i: object) => 'Margen' in i)).toBe(false);
  // Escribir exige data.write, aunque sea sin cabecera anti-CSRF (no es una credencial ambiental).
  const write = await d.post('/api/data/Pedido18', { data: { Folio: 'X' } });
  expect(write.status()).toBe(403);
  expect(write.headers()['www-authenticate']).toBe('Bearer error="insufficient_scope", scope="data.write"');
  expect((await d.get('/odata/Pedido18')).status()).toBe(403); // ese token no pidió odata.read

  // El administrador quita data.read: el token ya emitido lo pierde en su siguiente petición.
  const client = ((await (await admin.get('/api/admin/oauth-clients')).json()) as Client[]).find((c) => c.clientId === cred.clientId)!;
  const upd = await admin.put(`/api/admin/oauth-clients/${client.id}`, { data: { name: client.name, roleIds: [ids.Bi18], scopes: ['odata.read'], tokenMinutes: 15 } });
  expect(upd.status(), await upd.text()).toBe(200);
  expect((await d.get('/api/data/Pedido18')).status()).toBe(403);
  expect((await o.get('/odata/Pedido18')).status()).toBe(200);
  expect((await tokenRequest({ grant_type: 'client_credentials', scope: 'data.read' })).status()).toBe(400);
});

test('H3 CA1 · CA2 · CA3: $metadata y el documento de servicio se generan para quien los pide', async () => {
  const t = await token();
  const res = await odata(t, '$metadata');
  expect(res.status()).toBe(200);
  expect(res.headers()['content-type']).toMatch(/^application\/xml/);
  expect(res.headers()['odata-version']).toBe('4.0');
  const xml = await res.text();
  expect(xml).toContain('<EntitySet Name="Pedido18" EntityType="Erp.Pedido18"');
  expect(xml).toContain('<EntitySet Name="Cliente18" EntityType="Erp.Cliente18"');
  expect(xml).toContain('<NavigationProperty Name="Cliente" Type="Erp.Cliente18"');
  expect(xml).toContain('<NavigationProperty Name="Partidas" Type="Collection(Erp.Pedido18Linea)" ContainsTarget="true"');
  expect(xml).toContain('<Property Name="_Cliente_value" Type="Edm.Int64"');
  expect(xml).toContain('<Property Name="Fecha" Type="Edm.Date"');
  for (const hidden of ['Secreto18', '"Margen"', '"Costo"', 'EntitySet Name="Pedido18Linea"']) expect(xml, hidden).not.toContain(hidden);

  const service = await (await odata(t, '')).json();
  const names = (service.value as { name: string }[]).map((s) => s.name);
  expect(names).toEqual(expect.arrayContaining(['Cliente18', 'Pedido18']));
  expect(names).not.toContain('Secreto18');
  expect(service['@odata.context']).toBe(`${BASE_URL}/odata/$metadata`);

  // Con la sesión del administrador, el documento sí trae lo que él ve.
  const full = await (await admin.get('/odata/$metadata')).text();
  expect(full).toContain('EntitySet Name="Secreto18"');
  expect(full).toContain('<Property Name="Margen" Type="Edm.Decimal"');
});

test('H4 CA1 · CA2: $filter, $select, $orderby, $top, $count y paginación con nextLink, traducidos con lista blanca', async () => {
  const t = await token();
  const q = async (params: Record<string, string>, set = 'Pedido18') => {
    const r = await odata(t, set, params);
    expect(r.status(), await r.text()).toBe(200);
    return (await r.json()) as Page18;
  };
  expect(folios(await q({ $filter: "Monto ge 200 and Etapa eq 'Cerrado'" }))).toEqual(['P-002']);
  expect(folios(await q({ $filter: "contains(Notas,'ñandú')" }))).toEqual(['P-002']);
  // Dos valores: «distinto de true» incluye los vacíos (OData), como «not».
  expect(folios(await q({ $filter: 'Urgente ne true' }))).toEqual(['P-002', 'P-004', 'P-005']);
  expect(folios(await q({ $filter: 'not (Monto gt 150)' }))).toEqual(['P-001']);
  expect(folios(await q({ $filter: 'Fecha ge 2026-02-01 and Fecha lt 2026-03-15' }))).toEqual(['P-002', 'P-004']);
  expect(folios(await q({ $filter: `_Cliente_value eq ${ids.ana}` }))).toEqual(['P-001', 'P-005']);
  expect(folios(await q({ $filter: "Folio in ('P-001','P-003','P-900')" }))).toEqual(['P-001']); // P-003 fuera de alcance, P-900 de otra empresa
  expect(folios(await q({ $filter: 'Etapa eq null' }))).toEqual(['P-004']);
  expect((await q({ $filter: "Nombre eq 'O''Brien'" }, 'Cliente18')).value.map((r) => r.Nombre)).toEqual(["O'Brien"]);
  // Un intento de inyección es solo un texto que no coincide.
  expect((await q({ $filter: "Folio eq 'x'' OR 1=1 --'" })).value).toEqual([]);

  const sel = await q({ $select: 'Folio,Monto', $orderby: 'Monto desc', $top: '2' });
  expect(sel.value.map((r) => Object.keys(r).sort())).toEqual([['Folio', 'Id', 'Monto'], ['Folio', 'Id', 'Monto']]);
  expect(sel.value.map((r) => [r.Folio, r.Monto])).toEqual([['P-005', 400], ['P-004', 300]]);
  expect(sel['@odata.nextLink']).toBeUndefined();
  expect(sel['@odata.context']).toBe(`${BASE_URL}/odata/$metadata#Pedido18`);

  // Páginas de 3 (OData__MaxPageSize en la suite): conteo total y enlace a la siguiente con las mismas opciones.
  const first = await q({ $count: 'true', $orderby: 'Folio' });
  expect(first['@odata.count']).toBe(4);
  expect(first.value.map((r) => r.Folio)).toEqual(['P-001', 'P-002', 'P-004']);
  expect(first['@odata.nextLink']).toBe(`${BASE_URL}/odata/Pedido18?$count=true&$orderby=Folio&$skip=3`);
  const next = (await (await (await bearer(t)).get(first['@odata.nextLink']!)).json()) as Page18;
  expect(next.value.map((r) => r.Folio)).toEqual(['P-005']);
  expect(next['@odata.nextLink']).toBeUndefined();
  const topped = await q({ $top: '4', $orderby: 'Folio' });
  expect(topped['@odata.nextLink']).toBe(`${BASE_URL}/odata/Pedido18?$orderby=Folio&$top=1&$skip=3`);
  // Regresión (revisión exploratoria 022, F-01): ordenar por Id, o repetir una propiedad, repetía la columna y respondía 500.
  const byId = await q({ $orderby: 'Id desc', $top: '4' });
  expect(byId.value.map((r) => r.Id as number)).toEqual(byId.value.map((r) => r.Id as number).sort((a, b) => b - a));
  expect(folios(await q({ $orderby: 'Folio,Folio desc' }))).toEqual(['P-001', 'P-002', 'P-004']); // páginas de 3; gana el primer criterio

  // Lo oculto se trata igual que lo inexistente; lo no admitido responde 400 en el formato de OData.
  const bad = async (params: Record<string, string>, pattern: RegExp, set = 'Pedido18') => {
    const r = await odata(t, set, params);
    expect(r.status(), JSON.stringify(params)).toBe(400);
    const e = (await r.json()).error;
    expect(e.code).toBe('BadRequest');
    expect(e.message).toMatch(pattern);
    return e.message as string;
  };
  const hidden = await bad({ $filter: 'Margen gt 0' }, /no existe/);
  const missing = await bad({ $filter: 'Inventado gt 0' }, /no existe/);
  expect(hidden.replace('Margen', 'X')).toBe(missing.replace('Inventado', 'X'));
  await bad({ $orderby: 'Margen' }, /no existe/);
  await bad({ $select: 'Folio,Margen' }, /no existe/);
  await bad({ $orderby: 'Notas' }, /texto largo/);
  await bad({ $search: 'P' }, /no se admite/);
  await bad({ $filter: "Monto eq 'cien'" }, /espera un número/);
  await bad({ $filter: "Etapa eq 'Perdido'" }, /no es una opción/);
  await bad({ $filter: "Cliente/Nombre eq 'Ana'" }, /rutas/);
  await bad({ $expand: 'Cliente($select=Nombre)' }, /anidadas/);
  await bad({ $expand: 'Margen' }, /no es una navegación/);
});

test('H4 CA3 · CA4 · CA6: alcance por registro y empresa, $expand de un nivel con la seguridad del destino; solo lectura', async () => {
  const t = await token();
  const r = await odata(t, 'Pedido18', { $filter: "Folio in ('P-001','P-002')", $orderby: 'Folio', $expand: 'Cliente,Partidas' });
  expect(r.status(), await r.text()).toBe(200);
  const [p1, p2] = ((await r.json()) as Page18).value;
  expect(p1.Cliente).toMatchObject({ Id: ids.ana, Nombre: 'Ana', Ciudad: 'Monterrey' });
  expect(p1).not.toHaveProperty('Margen');
  const lines = p1.Partidas as Row[];
  expect(lines.map((l) => [l.Producto, l.Cantidad])).toEqual([['Tornillo', 2], ['Tuerca', 1]]);
  expect(lines.some((l) => 'Costo' in l || 'Pedido' in l)).toBe(false); // oculto y el padre no se publica
  // Beto está en una ciudad que Bi18 no alcanza: su id sí (como en la API de datos), el registro expandido no.
  expect(p2._Cliente_value).toBe(ids.beto);
  expect(p2.Cliente).toBeNull();
  expect(p2.Partidas).toEqual([]);

  // Por id: dentro del alcance, fuera de él, y en otra empresa.
  const one = await (await odata(t, `Pedido18(${ids.p1})`, { $select: 'Folio' })).json();
  expect(one).toEqual({ '@odata.context': `${BASE_URL}/odata/$metadata#Pedido18/$entity`, Id: ids.p1, Folio: 'P-001' });
  // Regresión (revisión exploratoria 018, F-02): un id fuera de rango decía «El conjunto de entidades no existe».
  for (const id of [ids.p3, ids.p900, 999999, '99999999999999999999']) {
    const nf = await odata(t, `Pedido18(${id})`);
    expect(nf.status(), String(id)).toBe(404);
    expect((await nf.json()).error).toEqual({ code: 'NotFound', message: 'El registro no existe.' });
  }
  // Catálogo sin permiso, partida o inexistente: el mismo 404.
  for (const set of ['Secreto18', 'Pedido18Linea', 'NoExiste']) expect((await odata(t, set)).status(), set).toBe(404);
  const write = await (await bearer(t)).post('/odata/Pedido18', { data: { Folio: 'X' } });
  expect(write.status()).toBe(405);
  expect(write.headers()['allow']).toBe('GET');

  // La sesión del administrador ve más (Margen, P-003, Beto), pero sigue sin ver la otra empresa.
  const all = (await (await admin.get('/odata/Pedido18', { params: { $orderby: 'Folio', $top: '10', $expand: 'Cliente' } })).json()) as Page18;
  expect(all.value.slice(0, 3).map((x) => x.Folio)).toEqual(['P-001', 'P-002', 'P-003']);
  expect(all.value[1].Cliente).toMatchObject({ Nombre: 'Beto' });
  expect(all.value[0].Margen).toBe(10);
  // Una clave de API (008) también llega a OData, con su rol.
  const key = await admin.post('/api/admin/api-keys', { data: { name: 'Clave OData E2E', roleIds: [ids.Bi18] } });
  expect(key.status(), await key.text()).toBe(200);
  const viaKey = await (await bearer((await key.json()).secret)).get('/odata/Pedido18', { params: { $count: 'true' } });
  expect(viaKey.status()).toBe(200);
  expect((await viaKey.json())['@odata.count']).toBe(4);
});

test('H1 CA3 · CA4: rotar sin gracia corta el secreto y los tokens; con gracia, ambos sirven; revocar corta todo', async ({ page }) => {
  const oldToken = await token();
  const oldSecret = cred.secret;
  await clientsTab(page);
  const row = page.getByRole('row', { name: /Tablero BI E2E/ });
  await expect(row).toContainText(/tokens? activos?/); // ya emitió tokens
  await row.getByRole('button', { name: 'Rotar el secreto de Tablero BI E2E' }).click();
  const dialog = page.getByRole('dialog', { name: 'Rotar el secreto de Tablero BI E2E' });
  await expect(dialog.getByLabel('El secreto anterior')).toHaveValue('0');
  await dialog.getByRole('button', { name: 'Rotar secreto' }).click();
  const shown = page.getByRole('dialog', { name: 'Credenciales del cliente' });
  expect(await shown.getByLabel('client_id', { exact: true }).textContent()).toBe(cred.clientId);
  cred.secret = (await shown.getByLabel('Valor secreto').textContent())!;
  expect(cred.secret).not.toBe(oldSecret);
  await shown.getByRole('button', { name: 'Ya lo guardé' }).click();
  expect((await odata(oldToken, 'Pedido18')).status()).toBe(401);
  expect((await tokenRequest({ grant_type: 'client_credentials' }, { Authorization: basic(cred.clientId, oldSecret) })).status()).toBe(401);

  // Con 24 horas de gracia: el secreto anterior y sus tokens siguen.
  const t2 = await token();
  const client = ((await (await admin.get('/api/admin/oauth-clients')).json()) as Client[]).find((c) => c.clientId === cred.clientId)!;
  const graced = await (await admin.post(`/api/admin/oauth-clients/${client.id}/rotate`, { data: { graceHours: 24 } })).json();
  const graceSecret = cred.secret;
  cred.secret = graced.secret;
  expect((await odata(t2, 'Pedido18')).status()).toBe(200);
  expect((await tokenRequest({ grant_type: 'client_credentials' }, { Authorization: basic(cred.clientId, graceSecret) })).status()).toBe(200);
  expect((await tokenRequest({ grant_type: 'client_credentials' })).status()).toBe(200);
  await page.reload();
  await page.getByRole('tab', { name: 'Clientes OAuth' }).click();
  await expect(page.getByRole('row', { name: /Tablero BI E2E/ })).toContainText('El secreto anterior sirve hasta');

  // Revocar desde la UI: nada sirve, tampoco el secreto en gracia.
  await page.getByRole('row', { name: /Tablero BI E2E/ }).getByRole('button', { name: 'Revocar Tablero BI E2E' }).click();
  await page.getByRole('dialog', { name: 'Revocar cliente' }).getByRole('button', { name: 'Revocar' }).click();
  await expect(page.getByRole('row', { name: /Tablero BI E2E/ }).getByText('Revocado')).toBeVisible();
  await expect(page.getByRole('row', { name: /Tablero BI E2E/ }).getByRole('button')).toHaveCount(0);
  expect((await odata(t2, 'Pedido18')).status()).toBe(401);
  for (const s of [cred.secret, graceSecret])
    expect((await tokenRequest({ grant_type: 'client_credentials' }, { Authorization: basic(cred.clientId, s) })).status()).toBe(401);

  // Spec 021: los tokens emitidos también se auditan; se filtra por acción para no depender de cuántos hubo.
  const audit = (await (await admin.get('/api/audit', { params: { action: 'integration.oauthclient', pageSize: '50' } })).json()) as { items: { action: string; recordId: string; after: { action: string } }[] };
  const actions = audit.items.filter((a) => a.action === 'integration.oauthclient' && a.recordId === String(client.id)).map((a) => a.after.action);
  expect(actions).toEqual(['revoke', 'rotate', 'rotate', 'update', 'create']);
});

// Spec 021 (H7 · H8 CA2): límites de costo de OData y auditoría de los tokens emitidos. Fallaron con el código anterior.
test('Spec 021 H7 CA1 · CA2 · H8 CA2: $skip y $expand de partidas con tope; emitir un token queda en la auditoría', async () => {
  const tokens = (await (await admin.get('/api/audit', { params: { action: 'integration.oauthtoken', pageSize: '5' } })).json()) as
    { total: number; items: { user: string; after: { clientId: string; scopes: string[]; expiresInMinutes: number } }[] };
  expect(tokens.total).toBeGreaterThan(0);
  expect(tokens.items[0].after.clientId).toBe(cred.clientId);
  expect(tokens.items[0].after.scopes.length).toBeGreaterThan(0);
  expect(JSON.stringify(tokens.items)).not.toMatch(/access_token|Bearer/);

  const skip = await admin.get('/odata/Pedido18', { params: { $skip: '100001' } });
  expect(skip.status()).toBe(400);
  expect((await skip.json()).error.message).toMatch(/\$skip/);
  expect((await admin.get('/odata/Pedido18', { params: { $skip: '100000' } })).status()).toBe(200);

  // La suite limita a 10 partidas expandidas por página (OData:MaxExpandedLines).
  const r = await admin.post('/api/data/Pedido18', { data: { Folio: 'P-021', Cliente: ids.ana, Monto: 1,
    Partidas: Array.from({ length: 11 }, (_, i) => ({ Producto: `Pieza ${i + 1}`, Cantidad: 1, Costo: 1 })) } });
  expect(r.status(), await r.text()).toBe(201);
  const many = await admin.get('/odata/Pedido18', { params: { $filter: "Folio eq 'P-021'", $expand: 'Partidas' } });
  expect(many.status()).toBe(400);
  expect((await many.json()).error.message).toMatch(/partidas/);
  const few = await admin.get('/odata/Pedido18', { params: { $filter: "Folio eq 'P-001'", $expand: 'Partidas' } });
  expect(few.status()).toBe(200);
  expect((await admin.delete(`/api/data/Pedido18/${(await r.json()).id}`)).status()).toBe(204);
});

test('diseño: la pestaña se adapta al móvil y al tema oscuro sin desbordarse', async ({ browser }) => {
  for (const [width, scheme] of [[390, 'light'], [1280, 'dark']] as const) {
    const ctx = await browser.newContext({ viewport: { width, height: 844 }, colorScheme: scheme });
    const page = await ctx.newPage();
    await clientsTab(page);
    await expect(page.getByRole('row', { name: /Tablero BI E2E/ })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    await expectAccessible(page);
    await ctx.close();
  }
});
