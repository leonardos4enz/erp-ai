// Spec 038 · servidor MCP por persona: la pantalla Conectar (consentimiento) y Conexiones de Claude. Un «cliente MCP»
// simulado se registra (RFC 7591) con una URL de retorno de loopback servida por esta prueba (127.0.0.1, puerto libre), así
// el navegador vuelve a ella con el código y la prueba lo canjea y llama a /mcp como lo haría Claude. Nada sale del equipo.
import { createHash, randomBytes } from 'node:crypto';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, BASE_URL, anonApi, apiAs, expect, expectAccessible, expectToast, roleId, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });
test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

const PWD = 'frase larga del conector mcp 2026';
const RESOURCE = `${BASE_URL}/mcp`;

// ---------------- Cliente MCP simulado ----------------
let server: Server;
let callback = '';

test.beforeAll(async () => {
  server = createServer((req, res) => {
    res.setHeader('content-type', 'text/html; charset=utf-8');
    res.end('<!doctype html><title>Cliente MCP</title><h1>Cliente MCP de prueba</h1>');
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  callback = `http://127.0.0.1:${(server.address() as AddressInfo).port}/callback`;
});
test.afterAll(() => new Promise<void>((r) => server.close(() => r())));

const b64url = (b: Buffer) => b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const pkce = () => {
  const verifier = b64url(randomBytes(32));
  return { verifier, challenge: b64url(createHash('sha256').update(verifier).digest()) };
};

async function register(name = 'Claude E2E', redirect = callback): Promise<string> {
  const anon = await anonApi({});
  const res = await anon.post('/oauth/register', { data: { client_name: name, redirect_uris: [redirect], token_endpoint_auth_method: 'none' } });
  expect(res.status(), await res.text()).toBe(201);
  const { client_id } = await res.json();
  await anon.dispose();
  return client_id;
}

function authorizeQuery(clientId: string, challenge: string, scope = 'modelo datos', redirect = callback) {
  return new URLSearchParams({ response_type: 'code', client_id: clientId, redirect_uri: redirect, code_challenge: challenge,
    code_challenge_method: 'S256', state: 'e2e-estado', scope, resource: RESOURCE }).toString();
}

async function exchange(clientId: string, code: string, verifier: string) {
  const anon = await anonApi({});
  const res = await anon.post('/oauth/token', { form: { grant_type: 'authorization_code', code, redirect_uri: callback, client_id: clientId,
    code_verifier: verifier, resource: RESOURCE } });
  expect(res.status(), await res.text()).toBe(200);
  const tokens = await res.json() as { access_token: string; refresh_token: string; scope: string };
  await anon.dispose();
  return tokens;
}

async function rpc(token: string, method: string, params: object = {}) {
  const anon = await anonApi({ Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream' });
  const res = await anon.post('/mcp', { data: { jsonrpc: '2.0', id: 1, method, params } });
  const out = { status: res.status(), body: res.status() === 200 ? await res.json() : null };
  await anon.dispose();
  return out;
}

const apis: Record<string, APIRequestContext> = {};
const as = async (who: string) => (apis[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(who, PWD));

/** Una conexión hecha por API (la pantalla Conectar se prueba aparte): registrar, autorizar con la sesión de la persona y canjear. */
async function connectByApi(user: string, name = 'Claude E2E') {
  const clientId = await register(name);
  const { verifier, challenge } = pkce();
  const query = authorizeQuery(clientId, challenge, 'modelo');
  const person = await as(user);
  expect((await person.get(`/api/mcp/authorize?${query}`)).status()).toBe(200);
  const decided = await person.post('/api/mcp/authorize', { data: { query, approve: true, scopes: ['modelo'] } });
  const code = new URL((await decided.json()).redirect).searchParams.get('code')!;
  return exchange(clientId, code, verifier);
}

async function freshPage(browser: Browser, viewport = { width: 1280, height: 900 }, colorScheme: 'light' | 'dark' = 'light'): Promise<Page> {
  const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport, colorScheme, reducedMotion: 'reduce' });
  const page = await ctx.newPage();
  await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, colorScheme);
  return page;
}

const permitir = (page: Page) => page.getByRole('button', { name: 'Permitir' });

test('preparar: una modeladora, un lector y una persona para las capturas', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/roles', { data: { name: 'ModeladorMcp', description: 'E2E spec 038', canManageModel: true, canManageUsers: false,
    canViewAudit: false, permissions: [] } });
  expect([200, 409], await res.text()).toContain(res.status());
  const modelador = await roleId(admin, 'ModeladorMcp');
  for (const [username, displayName, ids] of [['e2e-mcp-ana', 'Ana Conectora', [modelador]], ['e2e-mcp-lector', 'Luis Lector', [await roleId(admin, 'Usuario')]],
    ['e2e-mcp-visual', 'Vera Visual', [modelador]]] as const) {
    const u = await admin.post('/api/admin/users', { data: { username, displayName, password: PWD, roleIds: ids } });
    expect([201, 409], await u.text()).toContain(u.status());
  }
});

test('H1 CA1 H2 CA1 CA3: sin sesión, Conectar pide entrar, vuelve y autoriza sin datos; el cliente recibe el código y llama a /mcp', async ({ browser }) => {
  const clientId = await register();
  const { verifier, challenge } = pkce();
  const page = await freshPage(browser);
  await page.goto(`/oauth/authorize?${authorizeQuery(clientId, challenge)}`);
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Usuario').fill('e2e-mcp-ana');
  await page.getByLabel('Contraseña', { exact: true }).fill(PWD);
  await page.getByRole('button', { name: 'Entrar' }).click();

  await expect(page.getByRole('heading', { name: 'Conectar Claude E2E con ERP AI' })).toBeFocused();
  const host = new URL(callback).host;
  await expect(page.getByText(host, { exact: true })).toBeVisible();
  await expect(page.getByText('Ana Conectora (e2e-mcp-ana)')).toBeVisible();
  await expect(page.getByText('nombre sin verificar')).toBeVisible();
  await expect(page.getByText('Sitio desconocido')).toHaveCount(0); // loopback: el propio equipo
  await expect(page.getByRole('checkbox', { name: /Modelo/ })).toBeChecked();
  await expect(page.getByRole('checkbox', { name: /Modelo/ })).toBeDisabled();
  await expect(page.getByRole('checkbox', { name: /Datos/ })).not.toBeChecked();
  await expectAccessible(page);

  await permitir(page).click();
  await page.waitForURL((u) => u.href.startsWith(callback));
  const back = new URL(page.url()).searchParams;
  expect(back.get('state')).toBe('e2e-estado');
  expect(back.get('iss')).toBe(BASE_URL);
  const tokens = await exchange(clientId, back.get('code')!, verifier);
  expect(tokens.scope).toBe('modelo');
  const tools = await rpc(tokens.access_token, 'tools/list');
  const names = (tools.body.result.tools as { name: string }[]).map((t) => t.name);
  expect(names).toContain('ver_catalogo');
  expect(names).not.toContain('leer_registros');
  const call = await rpc(tokens.access_token, 'tools/call', { name: 'listar_catalogos', arguments: {} });
  expect(call.body.result.isError).toBe(false);
  expect(Array.isArray(call.body.result.structuredContent.items)).toBe(true);
  await page.context().close();
});

test('H2 CA1 CA3: marcar Datos avisa que los registros salen del ERP y el acceso lo incluye', async ({ page }) => {
  // La sesión del proyecto es la del administrador.
  const clientId = await register();
  const { verifier, challenge } = pkce();
  await page.goto(`/oauth/authorize?${authorizeQuery(clientId, challenge)}`);
  await expect(permitir(page)).toBeVisible();
  await expect(page.getByText('Los registros saldrán del ERP')).toHaveCount(0);
  await page.getByRole('checkbox', { name: /Datos/ }).check();
  await expect(page.getByText('Los registros saldrán del ERP')).toBeVisible();
  await expectAccessible(page);
  await permitir(page).click();
  await page.waitForURL((u) => u.href.startsWith(callback));
  const tokens = await exchange(clientId, new URL(page.url()).searchParams.get('code')!, verifier);
  expect(tokens.scope).toBe('modelo datos');
  const names = ((await rpc(tokens.access_token, 'tools/list')).body.result.tools as { name: string }[]).map((t) => t.name);
  expect(names).toContain('leer_registros');
});

test('H2 CA3: Cancelar vuelve al cliente con access_denied y sin código', async ({ page }) => {
  const clientId = await register();
  await page.goto(`/oauth/authorize?${authorizeQuery(clientId, pkce().challenge)}`);
  await page.getByRole('button', { name: 'Cancelar' }).click();
  await page.waitForURL((u) => u.href.startsWith(callback));
  const back = new URL(page.url()).searchParams;
  expect(back.get('error')).toBe('access_denied');
  expect(back.get('code')).toBeNull();
  expect(back.get('state')).toBe('e2e-estado');
});

test('H2 CA1: un sitio de retorno desconocido se avisa', async ({ page }) => {
  const clientId = await register('Claude', 'https://claude-ai.example.net/cb');
  await page.goto(`/conectar?${authorizeQuery(clientId, pkce().challenge, 'modelo', 'https://claude-ai.example.net/cb')}`);
  await expect(page.getByRole('heading', { name: 'Conectar Claude con ERP AI' })).toBeVisible();
  await expect(page.getByText('Sitio desconocido')).toBeVisible();
  await expect(page.locator('.consent-host')).toHaveText('claude-ai.example.net');
  await expect(page.getByRole('status').filter({ hasText: 'Sitio desconocido' })).toContainText('claude-ai.example.net');
  await expectAccessible(page);
});

test('H2 CA2: un retorno no registrado se explica aquí y no se redirige', async ({ page }) => {
  const clientId = await register();
  const query = authorizeQuery(clientId, pkce().challenge, 'modelo', 'https://evil.example/cb');
  await page.goto(`/conectar?${query}`);
  await expect(page.getByRole('heading', { name: 'No se puede conectar' })).toBeFocused();
  await expect(page.getByRole('alert')).toContainText('La dirección de retorno no es una de las que registró el cliente');
  await expect(permitir(page)).toHaveCount(0);
  expect(page.url()).toContain('/conectar?');
  await expectAccessible(page);
});

// Regresión (revisión adversarial 038, F-03): la pantalla redirigía sola al sitio del cliente con el error: con un cliente
// registrado por cualquiera, el ERP era un redirector abierto.
test('H2 CA2: un error del cliente (alcance desconocido) se explica y la persona decide volver', async ({ page }) => {
  const clientId = await register();
  await page.goto(`/conectar?${authorizeQuery(clientId, pkce().challenge, 'modelo aislado')}`);
  await expect(page.getByRole('heading', { name: 'No se puede conectar' })).toBeFocused();
  await expect(page.getByRole('alert')).toContainText('Los alcances admitidos son modelo y datos');
  expect(page.url()).toContain('/conectar?');
  await expectAccessible(page);
  await page.getByRole('button', { name: `Volver a ${new URL(callback).host}` }).click();
  await page.waitForURL((u) => u.href.startsWith(callback));
  expect(new URL(page.url()).searchParams.get('error')).toBe('invalid_scope');
});

test('H6 CA1 CA2: Conexiones de Claude lista la conexión, copia la URL y desconecta con confirmación', async ({ browser }) => {
  const tokens = await connectByApi('e2e-mcp-lector', 'Claude del lector');
  expect((await rpc(tokens.access_token, 'ping')).status).toBe(200);
  const page = await freshPage(browser);
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await uiLogin(page, 'e2e-mcp-lector', PWD);
  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await page.getByRole('menuitem', { name: 'Conexiones de Claude' }).click();
  await expect(page.getByRole('heading', { name: 'Conexiones de Claude', level: 1 })).toBeVisible();
  // Regresión (revisión exploratoria 038, F-02): la miga de pan decía «Inicio».
  await expect(page.getByRole('navigation', { name: 'Ruta de navegación' })).toContainText('Conexiones de Claude');
  await expect(page.getByLabel('URL del servidor MCP')).toHaveText(RESOURCE);
  await expectToast(page, 'URL copiada', () => page.getByRole('button', { name: 'Copiar' }).click());
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(RESOURCE);
  const row = page.getByRole('listitem', { name: 'Claude del lector' });
  await expect(row).toContainText('127.0.0.1');
  await expect(row).toContainText('Modelo');
  await expectAccessible(page);

  await row.getByRole('button', { name: 'Desconectar Claude del lector' }).click();
  const dialog = page.getByRole('dialog', { name: 'Desconectar aplicación' });
  await expect(dialog).toContainText('Su acceso deja de servir de inmediato');
  await expect(dialog.getByRole('button', { name: 'Cancelar' })).toBeFocused();
  await expectToast(page, 'Claude del lector se desconectó', () =>
    tras(page.waitForResponse((r) => r.url().includes('/api/mcp/connections/') && r.request().method() === 'DELETE'),
      () => dialog.getByRole('button', { name: 'Desconectar' }).click()));
  await expect(page.getByText('Sin conexiones')).toBeVisible();
  expect((await rpc(tokens.access_token, 'ping')).status).toBe(401);
  await page.context().close();
});

// Capturas: Conectar (cliente de loopback, sin datos) y Conexiones con una sola conexión, en claro, oscuro y móvil.
for (const [name, scheme, viewport] of [['light', 'light', { width: 1280, height: 900 }], ['dark', 'dark', { width: 1280, height: 900 }],
  ['movil', 'light', { width: 390, height: 844 }]] as const) {
  test(`visual: Conectar y Conexiones de Claude en ${name === 'movil' ? 'móvil' : name === 'light' ? 'claro' : 'oscuro'}`, async ({ browser }) => {
    const admin = await as('admin');
    // Una sola conexión vigente de la persona de las capturas (las de vueltas anteriores se desconectan).
    const person = await as('e2e-mcp-visual');
    for (const c of (await (await person.get('/api/mcp/connections')).json()).connections as { id: number }[])
      await person.delete(`/api/mcp/connections/${c.id}`);
    expect(admin).toBeTruthy();
    await connectByApi('e2e-mcp-visual', 'Claude');

    // Se entra en escritorio (en móvil la navegación va plegada) y luego se pasa al tamaño de la captura.
    const page = await freshPage(browser, { width: 1280, height: 900 }, scheme);
    await uiLogin(page, 'e2e-mcp-visual', PWD);
    await page.setViewportSize(viewport);
    const clientId = await register('Claude');
    await page.goto(`/conectar?${authorizeQuery(clientId, pkce().challenge)}`);
    await expect(permitir(page)).toBeVisible();
    await page.mouse.move(0, 0);
    await page.evaluate(() => document.fonts.ready);
    // El puerto del cliente de prueba cambia en cada corrida.
    await expect(page.getByRole('main')).toHaveScreenshot(`conectar-${name}.png`, { mask: [page.locator('.consent-host')] });

    await page.goto('/conexiones');
    await expect(page.getByRole('listitem', { name: 'Claude' })).toBeVisible();
    await page.mouse.move(0, 0);
    await expect(page.getByRole('main')).toHaveScreenshot(`conexiones-${name}.png`, {
      mask: [page.locator('.secret-box code'), page.locator('.connection-host'), page.locator('.connection-dates')],
    });
    if (name === 'movil') {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    }
    await page.context().close();
  });
}
