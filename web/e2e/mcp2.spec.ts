// Spec 044 · MCP 2: las casillas de la copia aislada y de las propuestas en Conectar, la copia viva de una conexión en
// Conexiones de Claude (y descartarla) y la propuesta que entrega un cliente MCP, revisada y aplicada en ERP AI Studio. Como
// en mcp.spec.ts, el «cliente MCP» es esta prueba: se registra con una URL de retorno de loopback y llama a /mcp.
import { createHash, randomBytes } from 'node:crypto';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, BASE_URL, anonApi, apiAs, applyPlan, expect, expectAccessible, expectToast, roleId, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga del conector mcp dos 2026';
const RESOURCE = `${BASE_URL}/mcp`;
const ENTITY = 'Mc2Propuesta';

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

test.afterAll(async () => {
  await new Promise<void>((r) => server.close(() => r()));
  // El catálogo que aplicó la propuesta se archiva, también si la prueba se cortó a mitad.
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await admin.get('/api/model/entities')).json()) as { name: string }[];
  if (model.some((e) => e.name === ENTITY)) await applyPlan(admin, { description: 'E2E 044: limpieza', operations: [{ op: 'archiveEntity', entity: ENTITY }] });
  await admin.dispose();
});

const b64url = (b: Buffer) => b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const pkce = () => {
  const verifier = b64url(randomBytes(32));
  return { verifier, challenge: b64url(createHash('sha256').update(verifier).digest()) };
};

async function register(name = 'Claude E2E'): Promise<string> {
  const anon = await anonApi({});
  const res = await anon.post('/oauth/register', { data: { client_name: name, redirect_uris: [callback], token_endpoint_auth_method: 'none' } });
  expect(res.status(), await res.text()).toBe(201);
  const { client_id } = await res.json();
  await anon.dispose();
  return client_id;
}

const authorizeQuery = (clientId: string, challenge: string, scope: string) =>
  new URLSearchParams({ response_type: 'code', client_id: clientId, redirect_uri: callback, code_challenge: challenge, code_challenge_method: 'S256',
    state: 'e2e-044', scope, resource: RESOURCE }).toString();

async function exchange(clientId: string, code: string, verifier: string) {
  const anon = await anonApi({});
  const res = await anon.post('/oauth/token', { form: { grant_type: 'authorization_code', code, redirect_uri: callback, client_id: clientId,
    code_verifier: verifier, resource: RESOURCE } });
  expect(res.status(), await res.text()).toBe(200);
  const tokens = await res.json() as { access_token: string; scope: string };
  await anon.dispose();
  return tokens;
}

async function call(token: string, name: string, args: object = {}) {
  const anon = await anonApi({ Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream' });
  const res = await anon.post('/mcp', { data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: args } } });
  expect(res.status(), await res.text()).toBe(200);
  const result = (await res.json()).result as { isError: boolean; structuredContent?: Record<string, unknown>; content: { text: string }[] };
  await anon.dispose();
  expect(result.isError, result.content[0]?.text).toBe(false);
  return result.structuredContent!;
}

const apis: Record<string, APIRequestContext> = {};
const as = async (who: string) => (apis[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(who, PWD));

async function connectByApi(user: string, scope: string, name = 'Claude E2E') {
  const clientId = await register(name);
  const { verifier, challenge } = pkce();
  const query = authorizeQuery(clientId, challenge, scope);
  const decided = await (await as(user)).post('/api/mcp/authorize', { data: { query, approve: true, scopes: scope.split(' ') } });
  expect(decided.status(), await decided.text()).toBe(200);
  const code = new URL((await decided.json()).redirect).searchParams.get('code')!;
  return exchange(clientId, code, verifier);
}

async function freshPage(browser: Browser): Promise<Page> {
  const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: { width: 1280, height: 900 }, reducedMotion: 'reduce' });
  return ctx.newPage();
}

const key = () => `e2e-${randomBytes(8).toString('hex')}`;

test('preparar: una modeladora y un lector', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/roles', { data: { name: 'ModeladorMcp2', description: 'E2E spec 044', canManageModel: true, canManageUsers: false,
    canViewAudit: false, permissions: [] } });
  expect([200, 409], await res.text()).toContain(res.status());
  for (const [username, displayName, role] of [['e2e-mcp2-ana', 'Ana Probadora', 'ModeladorMcp2'], ['e2e-mcp2-lector', 'Luis Lector', 'Usuario']] as const) {
    const u = await admin.post('/api/admin/users', { data: { username, displayName, password: PWD, roleIds: [await roleId(admin, role)] } });
    expect([201, 409], await u.text()).toContain(u.status());
  }
});

test('H1 CA2 CA3: Conectar ofrece la copia aislada y las propuestas apagadas, y el acceso lleva lo que se marca', async ({ browser }) => {
  const page = await freshPage(browser);
  await uiLogin(page, 'e2e-mcp2-ana', PWD);
  const clientId = await register();
  const { verifier, challenge } = pkce();
  await page.goto(`/conectar?${authorizeQuery(clientId, challenge, 'modelo aislado proponer')}`);
  const aislado = page.getByRole('checkbox', { name: /Copia aislada/ });
  const proponer = page.getByRole('checkbox', { name: /Proponer/ });
  await expect(aislado).not.toBeChecked();
  await expect(aislado).toBeEnabled();
  await expect(proponer).not.toBeChecked();
  // «Datos» no se pidió: deshabilitada.
  await expect(page.getByRole('checkbox', { name: /Datos/ })).toBeDisabled();
  await expect(page.getByText('no se escriben registros reales ni se cambia el modelo')).toBeVisible();
  await expectAccessible(page);
  await aislado.check();
  await proponer.check();
  await page.getByRole('button', { name: 'Permitir' }).click();
  await page.waitForURL((u) => u.href.startsWith(callback));
  const tokens = await exchange(clientId, new URL(page.url()).searchParams.get('code')!, verifier);
  expect(tokens.scope).toBe('modelo aislado proponer');
  await page.context().close();

  // Quien no modela las ve deshabilitadas, con el motivo.
  const lector = await freshPage(browser);
  await uiLogin(lector, 'e2e-mcp2-lector', PWD);
  await lector.goto(`/conectar?${authorizeQuery(await register(), pkce().challenge, 'modelo aislado proponer')}`);
  await expect(lector.getByRole('checkbox', { name: /Copia aislada/ })).toBeDisabled();
  await expect(lector.getByRole('checkbox', { name: /Proponer/ })).toBeDisabled();
  await expect(lector.getByText(/Solo para quien modela/).first()).toBeVisible();
  await lector.context().close();
});

test('H6 CA1 CA2: Conexiones muestra la copia aislada viva y la descarta con confirmación', async ({ browser }) => {
  const person = await as('e2e-mcp2-ana');
  for (const c of (await (await person.get('/api/mcp/connections')).json()).connections as { id: number }[]) await person.delete(`/api/mcp/connections/${c.id}`);
  const tokens = await connectByApi('e2e-mcp2-ana', 'modelo aislado', 'Claude con copia');
  // Una herramienta aislada prepara la copia de la conexión.
  await call(tokens.access_token, 'aislado_procesar_asincronas', { idempotencyKey: key() });
  const page = await freshPage(browser);
  await uiLogin(page, 'e2e-mcp2-ana', PWD);
  await page.goto('/conexiones');
  const row = page.getByRole('listitem', { name: 'Claude con copia' });
  await expect(row).toContainText('Copia aislada desde el');
  await expect(row.getByText('Copia aislada', { exact: true })).toBeVisible(); // la insignia del alcance
  await expectAccessible(page);
  await row.getByRole('button', { name: 'Descartar la copia aislada de Claude con copia' }).click();
  const dialog = page.getByRole('dialog', { name: 'Descartar copia aislada' });
  await expect(dialog).toContainText('la conexión sigue');
  await expect(dialog.getByRole('button', { name: 'Cancelar' })).toBeFocused();
  await expectToast(page, 'Se descartó la copia aislada de Claude con copia', () =>
    tras(page.waitForResponse((r) => r.url().includes('/sandbox') && r.request().method() === 'DELETE'),
      () => dialog.getByRole('button', { name: 'Descartar copia' }).click()));
  await expect(row).not.toContainText('Copia aislada desde el');
  await expect(row.getByRole('button', { name: /Descartar la copia/ })).toHaveCount(0);
  // La conexión sigue viva.
  await expect(row.getByRole('button', { name: 'Desconectar Claude con copia' })).toBeVisible();
  await page.context().close();
});

test('H5 CA1 CA2 CA3: la propuesta de un cliente MCP se revisa y se aplica en Studio, sin compositor', async ({ browser }) => {
  const tokens = await connectByApi('e2e-mcp2-ana', 'modelo proponer', 'Claude E2E');
  const plan = { description: 'E2E 044', operations: [{ op: 'createEntity', group: 'Pruebas 044', name: ENTITY, label: 'Propuesta 044', labelPlural: 'Propuestas 044',
    displayField: 'Nombre', fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] }] };
  const delivered = await call(tokens.access_token, 'entregar', { summary: 'Agrego el catálogo **Propuesta 044**.', plan,
    tested: [{ description: 'Compila contra el modelo', ok: true }], idempotencyKey: key() });
  expect(delivered.status).toBe('proposed');
  const page = await freshPage(browser);
  await uiLogin(page, 'e2e-mcp2-ana', PWD);
  await page.goto(new URL(String(delivered.url)).pathname + new URL(String(delivered.url)).search);
  // En lugar del mensaje de la persona, de dónde llegó.
  await expect(page.getByRole('log', { name: 'Conversación' }).getByText(/Propuesta de\s*Claude E2E\s*por MCP/)).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Conversaciones' }).getByText('MCP', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Compila contra el modelo')).toBeVisible();
  // Regresión (revisión exploratoria 044, F-01): el informe es del cliente; antes decía «Lo que el agente informa» y avisaba
  // que «no ejecutó ninguna herramienta en la copia aislada», que no aplica a pruebas que el ERP no ve.
  await expect(page.getByRole('heading', { name: 'Lo que informa Claude E2E' })).toBeVisible();
  await expect(page.getByText(/no ejecutó ninguna herramienta/)).toHaveCount(0);
  // Regresión (revisión exploratoria 044, F-02): el título no lleva las marcas de Markdown del resumen.
  await expect(page.getByRole('navigation', { name: 'Conversaciones' }).getByText('Claude E2E: Agrego el catálogo Propuesta 044.')).toBeVisible();
  await expect(page.getByText(/llegó por MCP desde Claude E2E/)).toBeVisible();
  await expectAccessible(page);
  const card = page.getByRole('article', { name: 'Propuesta' });
  await card.getByRole('button', { name: 'Revisar y aplicar…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Aplicar la propuesta de ERP AI Studio' });
  await expectToast(page, 'Cambios aplicados', () => dialog.getByRole('button', { name: 'Aplicar cambios' }).click());
  await expect(card.getByText('Aplicada', { exact: true })).toBeVisible();
  const model = (await (await (await as('admin')).get('/api/model/entities')).json()) as { name: string }[];
  expect(model.map((e) => e.name)).toContain(ENTITY);
  await page.context().close();
});
