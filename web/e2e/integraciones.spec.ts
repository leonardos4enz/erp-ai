// Spec 008 · Integraciones: webhooks firmados, claves de API, importación masiva y exportación de listados.
// Corre después de compras.spec.ts (Compras 1.5.0 instalado con sus usuarios). Un receptor HTTP local (127.0.0.1, la
// única IP interna que la suite permite: Webhooks__AllowedInternalAddresses) recibe los webhooks y verifica la firma.
import { createHmac, timingSafeEqual } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, anonApi, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de integraciones 2026';
const COMPRAS_PWD = 'frase larga de compras 2026'; // usuarios creados por compras.spec.ts
const ROLES = ['Integracion8', 'Capturista8', 'Gerente8'];

// ---------------- Receptor de webhooks ----------------
type Received = { headers: Record<string, string>; body: string; json: { id: string; webhook: string; event: string; state?: string; recordId?: number; data: Record<string, unknown> } };
const received: Received[] = [];
const mode: Record<string, number> = {}; // webhook → código que responde el receptor (200 por defecto)
let server: Server;
let hookUrl = '';
const secrets: Record<string, string> = {};

test.beforeAll(async () => {
  server = createServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      const json = JSON.parse(body) as Received['json'];
      received.push({ headers: req.headers as Record<string, string>, body, json });
      res.statusCode = mode[json.webhook] ?? 200;
      res.end();
    });
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  hookUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}/hooks/token-de-ruta`;
});
test.afterAll(async () => { await new Promise((r) => server.close(r)); });

/** Lo que haría el receptor: HMAC-SHA256 de «timestamp.cuerpo» con el secreto, en tiempo constante, y marca reciente. */
function validSignature(r: Received, secret: string) {
  const ts = r.headers['x-erp-timestamp'];
  const expected = Buffer.from('sha256=' + createHmac('sha256', secret).update(`${ts}.${r.body}`).digest('hex'));
  const got = Buffer.from(r.headers['x-erp-signature'] ?? '');
  return got.length === expected.length && timingSafeEqual(got, expected) && Math.abs(Date.now() / 1000 - Number(ts)) < 300;
}

const deliveriesFor = (webhook: string) => received.filter((r) => r.json.webhook === webhook);

// ---------------- Usuarios y sesiones ----------------
const api: Record<string, APIRequestContext> = {};
const as = async (who: 'admin' | 'capturista' | 'comprador' | 'gerente') =>
  (api[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password)
    : who === 'capturista' ? await apiAs('e2e-int-capturista', PWD)
    : await apiAs(`e2e-${who}`, COMPRAS_PWD));

async function pageAs(browser: Browser, user: string, pwd = PWD): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, user, pwd);
  return page;
}

const contactos = (q: Record<string, string> = {}) => as('admin').then((a) => a.get('/api/data/Contacto', { params: { pageSize: '200', ...q } })).then((r) => r.json());

const WEBHOOKS = [
  { name: 'ContactoValidado', label: 'Contacto validado', events: [{ on: 'enterState', state: 'Validado' }], role: 'Integracion8' },
  { name: 'ContactoMayorista', label: 'Contacto mayorista', events: [{ on: 'create' }], role: 'Integracion8', when: "Segmento = 'Mayorista'",
    fields: ['Nombre', 'Segmento', 'Zona'] },
];

test('preparar: roles, catálogo con seguridad y webhooks declarados', async () => {
  const admin = await as('admin');
  const flags = { canManageModel: false, canManageUsers: false, canViewAudit: false };
  const ids: Record<string, number> = {};
  for (const name of ROLES) {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 008', ...flags, permissions: [] } });
    expect(res.status(), await res.text()).toBe(200);
    ids[name] = (await res.json()).id;
  }
  await applyPlan(admin, { description: 'Spec 008 · contactos', operations: [
    { op: 'createEntity', name: 'Zona8', label: 'Zona', labelPlural: 'Zonas', icon: 'map', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true, unique: true }] },
    { op: 'createEntity', name: 'Contacto', label: 'Contacto', labelPlural: 'Contactos', icon: 'contact', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Segmento', label: 'Segmento', type: 'choice', options: ['Minorista', 'Mayorista'], defaultValue: "'Minorista'" },
      { name: 'Zona', label: 'Zona', type: 'reference', referenceEntity: 'Zona8' },
      { name: 'Credito', label: 'Crédito', type: 'decimal' },
      { name: 'Alta', label: 'Fecha de alta', type: 'date' },
      { name: 'Activo', label: 'Activo', type: 'boolean' },
      { name: 'Etiqueta', label: 'Etiqueta', type: 'formula', formula: 'UPPER(Nombre)', resultType: 'text' },
    ] },
    { op: 'setWorkflow', entity: 'Contacto', workflow: { initial: 'Nuevo',
      states: [{ name: 'Nuevo', label: 'Nuevo' }, { name: 'Validado', label: 'Validado', color: 'success' }],
      transitions: [{ name: 'Validar', label: 'Validar', from: ['Nuevo'], to: 'Validado' }] } },
    { op: 'setRules', entity: 'Contacto', rules: [{ name: 'CreditoPositivo', message: 'El crédito no puede ser negativo.', assert: 'Credito >= 0', field: 'Credito' }] },
    { op: 'setSecurity', entity: 'Contacto', security: {
      fields: [{ field: 'Credito', read: ['Gerente8'] }],
      records: [
        { roles: ['Capturista8'], where: "Segmento = 'Minorista'" },
        { roles: ['Gerente8'] },
        // El rol de servicio solo ve contactos activos: los inactivos no se envían.
        { roles: ['Integracion8'], where: 'Activo', access: 'read' },
      ] } },
    { op: 'setWebhooks', entity: 'Contacto', webhooks: WEBHOOKS },
  ] });
  const perms = (entity: string, all: boolean) => ({ entity, canRead: true, canCreate: all, canUpdate: all, canDelete: all });
  for (const [role, list] of [['Integracion8', [perms('Contacto', false), perms('Zona8', false)]], ['Capturista8', [perms('Contacto', true), perms('Zona8', false)]],
    ['Gerente8', [perms('Contacto', true), perms('Zona8', true)]]] as const) {
    const res = await admin.put(`/api/admin/roles/${ids[role]}`, { data: { name: role, description: 'E2E spec 008', ...flags, permissions: list } });
    expect(res.status(), await res.text()).toBe(200);
  }
  const res = await admin.post('/api/admin/users', { data: { username: 'e2e-int-capturista', displayName: 'Capturista Integraciones', password: PWD, roleIds: [ids.Capturista8] } });
  expect(res.status(), await res.text()).toBe(201);
  for (const nombre of ['Norte', 'Sur']) expect((await admin.post('/api/data/Zona8', { data: { Nombre: nombre } })).status()).toBe(201);
});

test('H1 CA1: la vista previa valida los webhooks (estado, rol y campos que el rol no ve) y el modelador los muestra', async ({ page }) => {
  const admin = await as('admin');
  const preview = async (webhooks: object[]) =>
    (await (await admin.post('/api/model/plan/preview', { data: { description: 'x', operations: [{ op: 'setWebhooks', entity: 'Contacto', webhooks }] } })).json()) as { valid: boolean; errors: { message: string }[] };
  const msgs = async (w: object) => (await preview([w])).errors.map((e) => e.message).join(' | ');
  expect(await msgs({ name: 'A', label: 'A', role: 'Integracion8', events: [{ on: 'enterState', state: 'Cerrado' }] })).toMatch(/estado/);
  expect(await msgs({ name: 'A', label: 'A', role: 'Nadie', events: [{ on: 'create' }] })).toMatch(/rol 'Nadie' no existe/);
  // Crédito solo lo ve Gerente8 (006): pedirlo para el rol de servicio sería una fuga.
  expect(await msgs({ name: 'A', label: 'A', role: 'Integracion8', events: [{ on: 'create' }], fields: ['Nombre', 'Credito'] })).toMatch(/no puede ver 'Crédito'/);
  expect((await preview(WEBHOOKS)).valid).toBe(true);

  await page.goto('/admin/model?edit=Contacto');
  await expect(page.getByLabel('Webhooks (JSON)')).toHaveValue(/"ContactoValidado"/);
  await expect(page.getByLabel('Webhooks (JSON)')).not.toHaveValue(/https?:/); // el destino no es metadato
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
});

test('H1 CA2: el administrador configura el destino; URLs internas rechazadas; el secreto se muestra una vez', async ({ page }) => {
  await page.goto('/admin/integrations');
  await expect(page.getByRole('heading', { name: 'Integraciones' })).toBeVisible();
  const row = page.getByRole('row', { name: /Contacto validado/ });
  await expect(row.getByText('Sin configurar')).toBeVisible();
  await expect(row).toContainText('Al entrar a «Validado»');
  await expectAccessible(page);

  await row.getByRole('button', { name: 'Configurar Contacto validado' }).click();
  const dialog = page.getByRole('dialog', { name: /Destino de «Contacto validado»/ });
  const url = dialog.getByLabel(/^URL de destino/);
  for (const [bad, message] of [
    ['http://169.254.169.254/latest/meta-data', /interna/], // metadatos de la nube (SSRF)
    ['http://10.0.0.5/hook', /interna/],
    ['http://localhost:5090/api/admin/users', /interna/],
    ['https://usuario:clave@ejemplo.com/hook', /usuario ni contraseña/],
    ['ftp://ejemplo.com/hook', /https/],
  ] as const) {
    await url.fill(bad);
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(dialog.getByText(message)).toBeVisible();
  }
  await url.fill(hookUrl);
  await dialog.getByRole('button', { name: 'Guardar' }).click();
  const secretDialog = page.getByRole('dialog', { name: 'Secreto del webhook' });
  const secret = (await secretDialog.getByLabel('Valor secreto').textContent())!;
  expect(secret).toMatch(/^whsec_[A-Za-z0-9_-]{40,}$/);
  secrets.ContactoValidado = secret;
  await expectAccessible(page);
  await secretDialog.getByRole('button', { name: 'Ya lo guardé' }).click();
  // El destino se muestra enmascarado: la ruta puede llevar un token.
  await expect(row).toContainText(/http:\/\/127\.0\.0\.1:\d+\/…/);
  await expect(row.getByText('Activo')).toBeVisible();

  // El segundo webhook, por API (mismo servicio).
  const admin = await as('admin');
  const res = await admin.put('/api/admin/webhooks/Contacto/ContactoMayorista', { data: { url: hookUrl, active: true } });
  secrets.ContactoMayorista = (await res.json()).secret;
  expect(secrets.ContactoMayorista).toMatch(/^whsec_/);
  // Nunca se devuelven ni se auditan el secreto o la ruta completa.
  const list = await (await admin.get('/api/admin/webhooks')).text();
  expect(list).not.toContain(secret);
  expect(list).not.toContain('token-de-ruta');
  const audit = await (await admin.get('/api/audit', { params: { entity: 'Contacto', page: '1', pageSize: '50' } })).text();
  expect(audit).toContain('integration.webhook');
  expect(audit).not.toContain(secret);
  expect(audit).not.toContain('token-de-ruta');
  // Volver a guardar no regenera el secreto.
  expect((await (await admin.put('/api/admin/webhooks/Contacto/ContactoMayorista', { data: { active: true } })).json()).secret).toBeNull();
  // Solo el superusuario administra integraciones.
  expect((await (await as('capturista')).get('/api/admin/webhooks')).status()).toBe(403);
});

test('H1 CA3 · CA5 · CA6: el evento llega firmado, con lo que ve el rol de servicio; lo revertido no se envía', async () => {
  const cap = await as('capturista');
  const zona = (await (await cap.get('/api/data/Zona8/lookup', { params: { search: 'Norte' } })).json())[0].id;
  const admin = await as('admin');
  const activo = await (await admin.post('/api/data/Contacto', { data: { Nombre: 'Ferretería Ñandú', Zona: zona, Credito: 5000, Activo: true } })).json();
  const inactivo = await (await admin.post('/api/data/Contacto', { data: { Nombre: 'Inactivo SA', Activo: false } })).json();
  for (const c of [activo, inactivo])
    expect((await cap.post(`/api/data/Contacto/${c.id}/transition`, { data: { transition: 'Validar', _rowVersion: c._rowVersion } })).status()).toBe(200);

  await expect.poll(() => deliveriesFor('ContactoValidado').length, { timeout: 15_000 }).toBe(1);
  const d = deliveriesFor('ContactoValidado')[0];
  expect(validSignature(d, secrets.ContactoValidado)).toBe(true);
  expect(validSignature(d, 'otro secreto')).toBe(false);
  expect(d.headers['x-erp-event']).toBe('enterState');
  expect(d.headers['x-erp-delivery']).toBe(d.json.id);
  expect(d.json).toMatchObject({ webhook: 'ContactoValidado', event: 'enterState', state: 'Validado', recordId: activo.id });
  expect(d.json.data).toMatchObject({ Nombre: 'Ferretería Ñandú', Zona__label: 'Norte', Etiqueta: 'FERRETERÍA ÑANDÚ', Estado: 'Validado' });
  // Crédito está oculto para el rol de servicio: no viaja. El inactivo está fuera de su alcance: no se envió.
  expect(d.json.data).not.toHaveProperty('Credito');
  expect(d.body).not.toContain('5000');
  expect(received.some((r) => r.json.recordId === inactivo.id)).toBe(false);

  // Un alta que se revierte (queda fuera del alcance del capturista) no deja evento: la bandeja es transaccional.
  expect((await cap.post('/api/data/Contacto', { data: { Nombre: 'Revertido', Segmento: 'Mayorista', Activo: true } })).status()).toBe(403);
  const ok = await admin.post('/api/data/Contacto', { data: { Nombre: 'Mayorista SA', Segmento: 'Mayorista', Zona: zona, Activo: true, Credito: 1 } });
  expect(ok.status()).toBe(201);
  await expect.poll(() => deliveriesFor('ContactoMayorista').length, { timeout: 15_000 }).toBe(1);
  const m = deliveriesFor('ContactoMayorista')[0];
  expect(validSignature(m, secrets.ContactoMayorista)).toBe(true);
  // Con «fields», solo el id y esos campos (con sus etiquetas).
  expect(Object.keys(m.json.data).sort()).toEqual(['Nombre', 'Segmento', 'Zona', 'Zona__label', 'id']);
  expect(received.some((r) => JSON.stringify(r.json.data).includes('Revertido'))).toBe(false);
  const deliveries = await (await admin.get('/api/admin/webhooks/deliveries', { params: { entity: 'Contacto' } })).json();
  expect(deliveries.some((x: { recordId: number }) => x.recordId === inactivo.id)).toBe(false);
});

test('H1 CA4: reintentos con bitácora; reintento manual, prueba y rotación del secreto', async ({ page }) => {
  mode.ContactoMayorista = 500;
  const admin = await as('admin');
  const before = deliveriesFor('ContactoMayorista').length;
  const c = await (await admin.post('/api/data/Contacto', { data: { Nombre: 'Distribuidora Caída', Segmento: 'Mayorista', Activo: true } })).json();
  // Webhooks__RetryDelaysSeconds=1,1 en la suite: 3 intentos y queda «Fallida».
  await expect.poll(() => deliveriesFor('ContactoMayorista').length - before, { timeout: 20_000 }).toBe(3);
  const attempts = deliveriesFor('ContactoMayorista').slice(before);
  expect(new Set(attempts.map((a) => a.json.id)).size).toBe(1);       // mismo id de entrega (el receptor deduplica)
  expect(new Set(attempts.map((a) => a.body)).size).toBe(1);          // mismo cuerpo en cada intento
  expect(attempts.every((a) => validSignature(a, secrets.ContactoMayorista))).toBe(true);

  await page.goto('/admin/integrations');
  // Fila de la bitácora (segunda tabla) con el id del registro en su propia celda.
  const deliveryRow = () => page.getByRole('table').nth(1).getByRole('row')
    .filter({ hasText: 'Contacto mayorista' }).filter({ has: page.getByRole('cell', { name: String(c.id), exact: true }) });
  const row = deliveryRow();
  await expect(row.getByText('Fallida')).toBeVisible({ timeout: 10_000 });
  await expect(row).toContainText('El destino respondió 500.');
  await expect(row.getByRole('cell').nth(5)).toHaveText('3');

  mode.ContactoMayorista = 200;
  await row.getByRole('button', { name: /Reintentar entrega/ }).click();
  await expect.poll(() => deliveriesFor('ContactoMayorista').length - before, { timeout: 15_000 }).toBe(4);
  await page.getByRole('button', { name: 'Actualizar' }).click();
  await expect(deliveryRow().getByText('Entregada')).toBeVisible({ timeout: 10_000 });

  // «Probar» envía un ping firmado en el momento.
  await page.getByRole('button', { name: 'Probar Contacto validado' }).click();
  await expect(page.getByText(/Prueba entregada \(respuesta 200\)/)).toBeVisible();
  const ping = received.at(-1)!;
  expect(ping.json.event).toBe('ping');
  expect(validSignature(ping, secrets.ContactoValidado)).toBe(true);

  // Rotar: el secreto anterior deja de firmar.
  await page.getByRole('button', { name: 'Rotar secreto de Contacto validado' }).click();
  await page.getByRole('dialog', { name: 'Rotar secreto' }).getByRole('button', { name: 'Rotar secreto' }).click();
  const fresh = (await page.getByRole('dialog', { name: 'Secreto del webhook' }).getByLabel('Valor secreto').textContent())!;
  expect(fresh).not.toBe(secrets.ContactoValidado);
  await page.getByRole('button', { name: 'Ya lo guardé' }).click();
  await page.getByRole('button', { name: 'Probar Contacto validado' }).click();
  await expect(page.getByText(/Prueba entregada/).last()).toBeVisible();
  expect(validSignature(received.at(-1)!, fresh)).toBe(true);
  expect(validSignature(received.at(-1)!, secrets.ContactoValidado)).toBe(false);
  secrets.ContactoValidado = fresh;
});

test('H2 CA1 · CA4: el administrador crea una clave; se ve una sola vez y no tiene roles de administración', async ({ page }) => {
  await page.goto('/admin/integrations');
  await page.getByRole('tab', { name: 'Claves de API' }).click();
  await page.getByRole('button', { name: 'Nueva clave' }).click();
  const dialog = page.getByRole('dialog', { name: 'Nueva clave de API' });
  await dialog.getByLabel(/^Nombre/).fill('Tienda E2E');
  await expect(dialog.getByRole('checkbox', { name: 'Administrador' })).toHaveCount(0); // no se ofrecen roles de administración
  await dialog.getByRole('checkbox', { name: 'Capturista8' }).check();
  await dialog.getByRole('button', { name: 'Crear clave' }).click();
  const key = (await page.getByRole('dialog', { name: 'Clave de API' }).getByLabel('Valor secreto').textContent())!;
  expect(key).toMatch(/^erp_[0-9a-f]{12}_[A-Za-z0-9_-]{43}$/);
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Ya lo guardé' }).click();
  const row = page.getByRole('row', { name: /Tienda E2E/ });
  await expect(row).toContainText(key.slice(0, 16) + '…');
  await expect(row).not.toContainText(key);
  await expect(row.getByText('Vigente')).toBeVisible();
  await expect(row).toContainText('Nunca');
  process.env.E2E_API_KEY = key;

  const admin = await as('admin');
  expect(await (await admin.get('/api/admin/api-keys')).text()).not.toContain(key.slice(17));
  // Por API tampoco se puede dar un rol de administración.
  const bad = await admin.post('/api/admin/api-keys', { data: { name: 'Mala', roleIds: [await roleId(admin, 'Administrador')] } });
  expect(bad.status()).toBe(400);
  // La cuenta de servicio no aparece en Usuarios ni en el buscador de usuarios.
  expect(await (await admin.get('/api/admin/users')).text()).not.toContain('svc.');
  expect(await (await admin.get('/api/users/lookup', { params: { search: 'Tienda' } })).json()).toEqual([]);
});

test('H2 CA2 · CA3: la clave usa datos con su rol, no llega a administración; rotar y revocar', async () => {
  const key = process.env.E2E_API_KEY!;
  // Sin cookie y sin cabecera anti-CSRF: la clave no es una credencial ambiental.
  const svc = await anonApi({ Authorization: `Bearer ${key}` });
  const list = await (await svc.get('/api/data/Contacto', { params: { pageSize: '100' } })).json();
  expect(list.items.length).toBeGreaterThan(0);
  expect(list.items.every((i: { Segmento: string }) => i.Segmento === 'Minorista')).toBe(true); // alcance del rol (006)
  expect(list.items.some((i: object) => 'Credito' in i)).toBe(false);                          // campo oculto (006)
  const created = await svc.post('/api/data/Contacto', { data: { Nombre: 'Alta por API' } });
  expect(created.status(), await created.text()).toBe(201);
  expect((await svc.post('/api/data/Contacto', { data: { Nombre: 'Fuera', Segmento: 'Mayorista' } })).status()).toBe(403);
  for (const path of ['/api/admin/users', '/api/model/entities', '/api/audit', '/api/admin/api-keys', '/api/admin/webhooks'])
    expect((await svc.get(path)).status(), path).toBe(403);
  expect((await (await svc.get('/api/auth/me')).json()).username).toMatch(/^svc\.[0-9a-f]{12}$/);

  // Auditoría a nombre de la cuenta de servicio.
  const admin = await as('admin');
  const audit = await (await admin.get('/api/audit', { params: { entity: 'Contacto', page: '1', pageSize: '20' } })).json();
  expect(audit.items.find((a: { action: string; user: string }) => a.action === 'create')?.user).toMatch(/^svc\./);

  // Una cuenta de servicio no inicia sesión.
  const me = await (await svc.get('/api/auth/me')).json();
  const anon = await anonApi();
  expect((await anon.post('/api/auth/login', { data: { username: me.username, password: key } })).status()).toBe(401);

  // Clave mal escrita, sin «Bearer» válido: 401 sin pistas.
  for (const wrong of [key.slice(0, -1) + (key.endsWith('A') ? 'B' : 'A'), 'erp_000000000000_' + 'a'.repeat(43), 'basura'])
    expect((await (await anonApi({ Authorization: `Bearer ${wrong}` })).get('/api/data/Contacto')).status()).toBe(401);

  // Rotar sin gracia: la anterior deja de servir, la nueva sirve.
  const keys = await (await admin.get('/api/admin/api-keys')).json();
  const id = keys.find((k: { name: string }) => k.name === 'Tienda E2E').id;
  const rotated = await (await admin.post(`/api/admin/api-keys/${id}/rotate`, { data: { graceHours: 0 } })).json();
  expect(rotated.secret.slice(0, 17)).toBe(key.slice(0, 17)); // mismo prefijo público
  expect((await svc.get('/api/data/Contacto')).status()).toBe(401);
  const svc2 = await anonApi({ Authorization: `Bearer ${rotated.secret}` });
  expect((await svc2.get('/api/data/Contacto')).status()).toBe(200);
  // Rotar con 24 h de gracia: las dos sirven.
  const graced = await (await admin.post(`/api/admin/api-keys/${id}/rotate`, { data: { graceHours: 24 } })).json();
  const svc3 = await anonApi({ Authorization: `Bearer ${graced.secret}` });
  expect((await svc2.get('/api/data/Contacto')).status()).toBe(200);
  expect((await svc3.get('/api/data/Contacto')).status()).toBe(200);
  // Revocar: ninguna sirve, tampoco la de gracia.
  expect((await admin.post(`/api/admin/api-keys/${id}/revoke`)).status()).toBe(204);
  expect((await svc2.get('/api/data/Contacto')).status()).toBe(401);
  expect((await svc3.get('/api/data/Contacto')).status()).toBe(401);
  for (const c of [svc, svc2, svc3, anon]) await c.dispose();
});

// ---------------- Importación y exportación ----------------

const file = (name: string, text: string) => ({ name, mimeType: 'text/csv', buffer: Buffer.from('﻿' + text, 'utf8') });

test('H3 CA1 · CA2 · CA4 · CA5: vista previa con errores por fila; columnas ocultas y calculadas; nada se importa con errores', async ({ browser }) => {
  const page = await pageAs(browser, 'e2e-int-capturista');
  await page.goto('/c/Contacto');
  const before = (await contactos()).total;
  await page.getByRole('button', { name: 'Importar' }).click();
  const dialog = page.getByRole('dialog', { name: 'Importar contactos' });

  // Plantilla: solo lo que el capturista puede capturar (sin Crédito, que no ve, ni Etiqueta, calculada).
  const download = page.waitForEvent('download');
  await dialog.getByRole('button', { name: 'Descargar plantilla' }).click();
  const template = readFileSync((await (await download).path())!, 'utf8');
  expect(template.replace('﻿', '').trim()).toBe('Nombre,Segmento,Zona,Fecha de alta,Activo');

  await dialog.getByLabel('Archivo').setInputFiles(file('contactos.csv', [
    'NOMBRE;segmento;Zona;Fecha de alta;Activo;Crédito;Etiqueta;Color',
    'Papelería Ávila;minorista;Norte;23/10/2026;sí;100;x;rojo',
    'Sin zona;;Marte;2026-10-23;no;;;',
    ';Minorista;Sur;;;;;',
    'Mayoreo;Mayorista;Sur;;sí;;;',
    'Fecha mala;;Sur;mañana;tal vez;;;',
  ].join('\r\n')));
  await expect(dialog.getByText('El archivo tiene errores')).toBeVisible();
  const cols = dialog.getByRole('table').first();
  await expect(cols.getByRole('row', { name: /^NOMBRE Nombre Se importa/ })).toBeVisible();
  await expect(cols.getByRole('row', { name: /^Etiqueta Etiqueta Se ignora/ })).toBeVisible();     // calculada
  // Crédito está oculto para el capturista: se trata igual que una columna que no existe (no revela que existe).
  await expect(cols.getByRole('row', { name: /^Crédito — Error No corresponde/ })).toBeVisible();
  await expect(cols.getByRole('row', { name: /^Color — Error No corresponde/ })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Importar' })).toBeDisabled();
  await expectAccessible(page);

  // Sin las columnas desconocidas: quedan los errores por fila (motor: referencias, obligatorios, alcance, formatos).
  await dialog.getByLabel('Archivo').setInputFiles(file('contactos.csv', [
    'NOMBRE;segmento;Zona;Fecha de alta;Activo;Etiqueta',
    'Papelería Ávila;minorista;Norte;23/10/2026;sí;x',
    'Sin zona;;Marte;2026-10-23;no;',
    ';Minorista;Sur;;;',
    'Mayoreo;Mayorista;Sur;;sí;',
    'Fecha mala;;Sur;mañana;tal vez;',
  ].join('\r\n')));
  const errs = dialog.getByRole('table').nth(1);
  await expect(errs.getByRole('row', { name: /^3 Zona No existe «Marte» en Zonas/ })).toBeVisible();
  await expect(errs.getByRole('row', { name: /^4 .*Nombre es obligatorio/ })).toBeVisible();
  await expect(errs.getByRole('row', { name: /^5 .*fuera de los registros que tu rol puede modificar/ })).toBeVisible();
  await expect(errs.getByRole('row', { name: /^6 Fecha de alta «mañana» no es una fecha/ })).toBeVisible();
  await expect(errs.getByRole('row', { name: /^6 Activo «tal vez» no es un valor/ })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Importar' })).toBeDisabled();
  // La vista previa no guarda nada; importar con errores tampoco (todo o nada).
  expect((await contactos()).total).toBe(before);
  const cap = await as('capturista');
  const forced = await cap.post('/api/data/Contacto/import', { params: { dryRun: 'false' }, data: Buffer.from('Nombre;Zona\nBueno;Norte\nMalo;Marte\n'), headers: { 'Content-Type': 'application/octet-stream' } });
  expect(forced.status()).toBe(400);
  expect((await forced.json()).title).toMatch(/no se importó ningún registro/);
  expect((await contactos()).total).toBe(before);
  await page.context().close();
});

test('H3 CA3 · CA5: la importación usa el motor (valores por defecto, fórmulas, referencias) y queda en la auditoría', async ({ browser }) => {
  const page = await pageAs(browser, 'e2e-int-capturista');
  await page.goto('/c/Contacto');
  await page.getByRole('button', { name: 'Importar' }).click();
  const dialog = page.getByRole('dialog', { name: 'Importar contactos' });
  await dialog.getByLabel('Archivo').setInputFiles(file('contactos.csv', [
    'Nombre,Segmento,Zona,Fecha de alta,Activo',
    '"Papelería Ávila, S.A.",,Norte,23/10/2026,sí',
    'Óptica Núñez,Minorista,sur,2026-10-01,no',
    "'=HYPERLINK(\"http://x\"),,,,",
  ].join('\r\n')));
  await expect(dialog.getByText('Todo listo para importar')).toBeVisible();
  await dialog.getByRole('button', { name: 'Importar 3 registros' }).click();
  await expect(page.getByText('Se importaron 3 registros')).toBeVisible();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('cell', { name: 'Papelería Ávila, S.A.', exact: true })).toBeVisible(); // el listado se recarga

  const items = (await contactos({ search: 'Ávila' })).items;
  expect(items[0]).toMatchObject({ Segmento: 'Minorista', Zona__label: 'Norte', Alta: '2026-10-23', Activo: true, Etiqueta: 'PAPELERÍA ÁVILA, S.A.', Estado: 'Nuevo' });
  expect((await contactos({ search: 'Núñez' })).items[0]).toMatchObject({ Zona__label: 'Sur', Activo: false });
  // El apóstrofo que pone la exportación se quita; se guarda como texto.
  expect((await contactos({ search: 'HYPERLINK' })).items[0].Nombre).toBe('=HYPERLINK("http://x")');
  const audit = await (await (await as('admin')).get('/api/audit', { params: { entity: 'Contacto', page: '1', pageSize: '30' } })).json();
  const imp = audit.items.find((a: { action: string }) => a.action === 'data.import');
  expect(imp.user).toBe('e2e-int-capturista');
  expect(imp.after).toMatchObject({ imported: 3, rows: 3 });
  expect(audit.items.filter((a: { action: string; user: string }) => a.action === 'create' && a.user === 'e2e-int-capturista').length).toBeGreaterThanOrEqual(3);
  await page.context().close();
});

test('H4 CA1 · CA2: exportar el listado como se ve, sin campos ocultos ni inyección de fórmulas; se puede reimportar', async ({ browser }) => {
  const page = await pageAs(browser, 'e2e-int-capturista');
  await page.goto('/c/Contacto?search=a');
  await expect(page.getByRole('cell').first()).toBeVisible();
  const shown = Number((await page.getByText(/\d+ registros?$/).textContent())!.replace(/\D/g, ''));
  await page.getByRole('button', { name: 'Exportar' }).click();
  const dialog = page.getByRole('dialog', { name: 'Exportar contactos' });
  await expectAccessible(page);
  const csvDownload = page.waitForEvent('download');
  await dialog.getByRole('link', { name: 'Descargar CSV' }).click();
  const csvFile = await csvDownload;
  expect(csvFile.suggestedFilename()).toMatch(/^Contacto-\d{8}\.csv$/);
  const csv = readFileSync((await csvFile.path())!, 'utf8');
  expect(csv.startsWith('﻿')).toBe(true);
  const lines = csv.replace('﻿', '').trim().split('\r\n');
  const header = lines[0];
  expect(header).not.toContain('Crédito');                      // oculto para el capturista
  expect(header).toContain('Nombre');
  expect(lines.length - 1).toBe(shown);                          // misma búsqueda que la pantalla
  expect(csv).not.toContain('Mayorista SA');                     // fuera de su alcance
  expect(csv).toContain(`"'=HYPERLINK(""http://x"")"`);          // neutralizada para la hoja de cálculo
  expect(csv).toContain('Norte');                                 // etiqueta de la referencia, no el id

  await page.getByRole('button', { name: 'Exportar' }).click();
  const xlsxDownload = page.waitForEvent('download');
  await page.getByRole('dialog', { name: 'Exportar contactos' }).getByRole('link', { name: 'Descargar Excel' }).click();
  const xlsx = readFileSync((await (await xlsxDownload).path())!);
  expect(xlsx.subarray(0, 2).toString()).toBe('PK');
  await page.context().close();

  // El archivo exportado se vuelve a importar (vista previa): columnas reconocidas, calculadas ignoradas.
  const cap = await as('capturista');
  const again = await (await cap.post('/api/data/Contacto/import', { params: { dryRun: 'true' }, data: xlsx, headers: { 'Content-Type': 'application/octet-stream' } })).json();
  expect(again.format).toBe('xlsx');
  expect(again.errors.filter((e: { message: string }) => !/fuera de los registros/.test(e.message))).toEqual([]);
  expect(again.columns.every((c: { status: string }) => c.status !== 'error')).toBe(true);
  // La exportación queda en la auditoría.
  const audit = await (await (await as('admin')).get('/api/audit', { params: { entity: 'Contacto', page: '1', pageSize: '10' } })).json();
  expect(audit.items.some((a: { action: string; user: string }) => a.action === 'data.export' && a.user === 'e2e-int-capturista')).toBe(true);
  // Columnas pedidas que el usuario no ve simplemente no salen.
  const forced = await (await cap.get('/api/data/Contacto/export', { params: { format: 'csv', columns: 'Nombre,Credito' } })).text();
  expect(forced.replace('﻿', '').split('\r\n')[0]).toBe('Nombre');
});

test('H5 CA1 · CA2 · CA3: Compras 1.5.0 trae su rol de servicio y el webhook sin destino; la orden autorizada llega sin importes', async ({ page }) => {
  const admin = await as('admin');
  const mod = (await (await admin.get('/api/admin/modules')).json()).find((m: { id: string }) => m.id === 'compras');
  expect(mod.installedVersion).toBe('1.5.0');
  await page.goto('/admin/integrations');
  const row = page.getByRole('row', { name: /Orden autorizada/ });
  await expect(row).toContainText('Integración de almacén');
  await expect(row.getByText('Sin configurar')).toBeVisible();

  const res = await admin.put('/api/admin/webhooks/OrdenCompra/OrdenAutorizada', { data: { url: hookUrl, active: true } });
  const secret = (await res.json()).secret as string;
  const comprador = await as('comprador');
  const proveedor = (await (await comprador.get('/api/data/Proveedor', { params: { search: 'Cartones' } })).json()).items[0].id;
  const almacen = (await (await comprador.get('/api/data/Almacen/lookup')).json())[0].id;
  const producto = (await (await comprador.get('/api/data/Producto', { params: { search: 'CAJ-30' } })).json()).items[0].id;
  const oc = await (await comprador.post('/api/data/OrdenCompra', { data: { Proveedor: proveedor, Almacen: almacen, Partidas: [{ Producto: producto, Cantidad: 4 }] } })).json();
  const sent = await (await comprador.post(`/api/data/OrdenCompra/${oc.id}/transition`, { data: { transition: 'enviar', _rowVersion: oc._rowVersion } })).json();
  const auth = await (await as('gerente')).post(`/api/data/OrdenCompra/${oc.id}/transition`, { data: { transition: 'autorizar', _rowVersion: sent._rowVersion } });
  expect(auth.status(), await auth.text()).toBe(200);

  await expect.poll(() => deliveriesFor('OrdenAutorizada').length, { timeout: 15_000 }).toBe(1);
  const d = deliveriesFor('OrdenAutorizada')[0];
  expect(validSignature(d, secret)).toBe(true);
  expect(d.json.data).toMatchObject({ Folio: oc.Folio, Estado: 'Autorizada', Proveedor__label: expect.stringMatching(/Cartones/) });
  const partida = (d.json.data.Partidas as Record<string, unknown>[])[0];
  expect(partida).toMatchObject({ Cantidad: 4 });
  // La 006 le oculta los importes al rol «Integración de almacén»: ni en el encabezado ni en las partidas.
  for (const k of ['Total', 'Subtotal', 'IVA', 'TotalMXN']) expect(d.json.data).not.toHaveProperty(k);
  for (const k of ['PrecioUnitario', 'Importe', 'Total']) expect(partida).not.toHaveProperty(k);
  // Se deja sin destino para no afectar otras pruebas.
  expect((await admin.delete('/api/admin/webhooks/OrdenCompra/OrdenAutorizada')).status()).toBe(204);
});

test('Integraciones: solo el superusuario ve la opción y la ruta', async ({ browser }) => {
  const page = await pageAs(browser, 'e2e-int-capturista');
  await expect(page.getByRole('link', { name: 'Integraciones' })).toHaveCount(0);
  await page.goto('/admin/integrations');
  await expect(page.getByRole('heading', { name: 'Integraciones' })).toHaveCount(0);
  await page.context().close();
});

/** SQL directo sobre la base desechable de la suite (nunca la de desarrollo). */
function sql(query: string) {
  const db = process.env.E2E_DB ?? 'ErpAi_E2E';
  if (!db.endsWith('_E2E')) throw new Error('Solo sobre la base desechable.');
  execFileSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB', '-d', db, '-E', '-b', '-Q', query]);
}

test('H5 CA1: la vista previa de una actualización dice qué roles nuevos creará', async ({ page }) => {
  // Regresión (revisión exploratoria 008, F-01): la vista previa de «Actualizar Compras» no mencionaba el rol de servicio.
  // Se simula una base en Compras 1.4.0 sin el rol, y luego se deja como estaba (acentos con NCHAR: sqlcmd y la consola).
  sql("UPDATE meta.Module SET Version = '1.4.0' WHERE Id = 'compras'; UPDATE meta.Role SET Name = N'tmp-e2e-rol' WHERE Name = N'Integraci' + NCHAR(243) + N'n de almac' + NCHAR(233) + N'n'");
  try {
    await page.goto('/admin/modules');
    await page.getByRole('button', { name: 'Actualizar Compras' }).click();
    const dialog = page.getByRole('dialog', { name: 'Actualizar Compras' });
    await expect(dialog.getByText('El plan es válido')).toBeVisible();
    await expect(dialog.getByText('Roles que se crearán')).toBeVisible();
    await expect(dialog.getByText('Integración de almacén', { exact: true })).toBeVisible();
    await dialog.getByRole('button', { name: 'Cancelar' }).click();
  } finally {
    sql("UPDATE meta.Module SET Version = '1.5.0' WHERE Id = 'compras'; UPDATE meta.Role SET Name = N'Integraci' + NCHAR(243) + N'n de almac' + NCHAR(233) + N'n' WHERE Name = N'tmp-e2e-rol'");
  }
});

test('H3 CA1: en móvil el diálogo de importación cabe en la pantalla y su botón se ve completo', async ({ browser }) => {
  // Regresión (revisión exploratoria 008, F-02): el pie con tres botones y el selector de archivo ensanchaban el diálogo
  // más que la pantalla y «Importar» quedaba cortado.
  const page = await pageAs(browser, 'e2e-int-capturista');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/c/Contacto');
  await page.getByRole('button', { name: 'Importar' }).click();
  const dialog = page.getByRole('dialog', { name: 'Importar contactos' });
  await expect(dialog).toBeVisible();
  const box = (await dialog.boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  const btn = dialog.getByRole('button', { name: 'Importar' });
  const visible = await btn.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.left >= 0 && r.right <= window.innerWidth && (hit === el || el.contains(hit));
  });
  expect(visible, '«Importar» debe verse completo').toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await page.context().close();
});
