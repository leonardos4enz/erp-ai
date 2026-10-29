// Spec 037 · ERP AI Studio: pantalla de chat (reemplaza la de la 035). Un proveedor simulado (127.0.0.1, PORTS.asistente:
// Assistant__BaseUrl de la suite) responde como la API de mensajes de Anthropic, en streaming, con `tool_use` según un guion
// por conversación (`[guion:…]` en el primer mensaje): el motor de la 033/036 corre de verdad (herramientas, copia aislada,
// entrega, varios turnos, flujo SSE) y la suite nunca llama a Anthropic. La clave es ficticia.
import { createServer, type Server, type ServerResponse } from 'node:http';
import type { APIRequestContext, Browser, Locator, Page } from '@playwright/test';
import { ADMIN, PORTS, apiAs, applyPlan, expect, expectAccessible, expectToast, receptor, roleId, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });
test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

const GROUP = 'Pruebas Studio'; // grupo propio del menú
const PWD = 'frase larga de studio 2026';
const API_KEY = 'sk-ant-e2e-studio-clave-ficticia-0001';
const HOSTIL = '<img src=x onerror="window.__xss=1"> [Haz clic aquí](https://phishing.example/login) https://phishing.example/otra';

// ---------------- Proveedor simulado ----------------
type Block = { type: 'text'; text: string } | { type: 'tool_use'; id: string; name: string; input: unknown };
/**
 * Una respuesta del «modelo»: bloques, retraso antes de empezar, `chunkMs` (escribe el texto despacio, de 20 en 20
 * caracteres), `stop` (otro `stop_reason`, p. ej. `max_tokens`), o `hold` (no responde hasta que el ERP corte la conexión).
 */
type Turn = { blocks: Block[]; delayMs?: number; chunkMs?: number; hold?: boolean; stop?: string };
let ids = 0;
const say = (text: string): Block => ({ type: 'text', text });
const tool = (name: string, input: object = {}): Block => ({ type: 'tool_use', id: `toolu_e2e${++ids}`, name, input });
const deliver = (input: object): Turn => ({ blocks: [tool('entregar', input)] });
/** Guion por respuestas (cuenta las del asistente en todo el historial); después de la última, entrega sin plan. */
const steps = (...turns: Turn[]) => (i: number): Turn => turns[i] ?? deliver({ summary: 'Fin del guion.' });
/**
 * Guion por llamadas al proveedor: una respuesta detenida o rechazada no queda en el historial (no cuenta como respuesta del
 * asistente), así que reintentar volvería a la misma posición de `steps`.
 */
const calls = (...turns: Turn[]) => { let n = 0; return (): Turn => turns[n++] ?? deliver({ summary: 'Fin del guion.' }); };
const scripts: Record<string, (turn: number) => Turn> = {};
const received = receptor<{ tag: string; turn: number; last: string }>();
const held: ServerResponse[] = [];
let server: Server;

test.beforeAll(async () => {
  server = createServer((req, res) => {
    let raw = '';
    req.on('data', (c) => (raw += c));
    req.on('end', () => {
      res.setHeader('content-type', 'application/json');
      // Verificación de la clave al guardarla (GET /v1/models/{id}).
      if (req.method === 'GET') {
        res.end(JSON.stringify({ type: 'model', id: 'claude-sonnet-5-5', display_name: 'Claude Sonnet 5.5', created_at: '2026-01-01T00:00:00Z' }));
        return;
      }
      const body = JSON.parse(raw) as { stream?: boolean; messages: { role: string; content: unknown }[] };
      const tag = /\[guion:([a-z0-9-]+)\]/.exec(JSON.stringify(body.messages[0]?.content))?.[1] ?? '';
      const turn = body.messages.filter((m) => m.role === 'assistant').length;
      received.push({ tag, turn, last: JSON.stringify(body.messages[body.messages.length - 1]?.content) });
      const next = scripts[tag]?.(turn) ?? { blocks: [say('Sin guion.')] };
      if (next.hold) { held.push(res); return; }
      const message = {
        id: `msg_e2e_${received.length}`, type: 'message', role: 'assistant', model: 'claude-sonnet-5-5', content: next.blocks,
        stop_reason: next.stop ?? (next.blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn'), stop_sequence: null,
        usage: { input_tokens: 1500, output_tokens: 250 },
      };
      setTimeout(() => void (body.stream ? streamMessage(res, message, next.chunkMs ?? 0) : res.end(JSON.stringify(message))), next.delayMs ?? 0);
    });
  });
  await new Promise<void>((r) => server.listen(PORTS.asistente, '127.0.0.1', r));
});

/** La respuesta como eventos SSE de la API de mensajes (texto en trozos, entrada de herramienta como JSON parcial). */
async function streamMessage(res: ServerResponse, msg: { content: Block[]; stop_reason: string; usage: { input_tokens: number; output_tokens: number } } & Record<string, unknown>, chunkMs: number) {
  res.setHeader('content-type', 'text/event-stream');
  const ev = (type: string, data: object) => { if (!res.destroyed) res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`); };
  const pause = () => (chunkMs ? new Promise((r) => setTimeout(r, chunkMs)) : Promise.resolve());
  ev('message_start', { message: { ...msg, content: [], stop_reason: null, usage: { input_tokens: msg.usage.input_tokens, output_tokens: 1 } } });
  for (const [index, b] of msg.content.entries()) {
    if (b.type === 'text') {
      ev('content_block_start', { index, content_block: { type: 'text', text: '' } });
      for (let i = 0; i < b.text.length; i += 20) {
        if (res.destroyed) return;
        ev('content_block_delta', { index, delta: { type: 'text_delta', text: b.text.slice(i, i + 20) } });
        await pause();
      }
    } else {
      ev('content_block_start', { index, content_block: { type: 'tool_use', id: b.id, name: b.name, input: {} } });
      ev('content_block_delta', { index, delta: { type: 'input_json_delta', partial_json: JSON.stringify(b.input) } });
    }
    ev('content_block_stop', { index });
  }
  ev('message_delta', { delta: { stop_reason: msg.stop_reason, stop_sequence: null }, usage: { output_tokens: msg.usage.output_tokens } });
  ev('message_stop', {});
  res.end();
}

// ---------------- Sesiones de API ----------------
const apis: Record<string, APIRequestContext> = {};
const as = async (who: string) => (apis[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(who, PWD));

/** Detiene la conversación activa del administrador, si hay (para que la siguiente prueba pueda empezar otra). */
async function stopActive() {
  const admin = await as('admin');
  const status = await (await admin.get('/api/studio')).json().catch(() => null);
  if (!status?.active) return;
  await admin.post(`/api/studio/sessions/${status.active}/cancel`);
  await expect.poll(async () => (await (await admin.get('/api/studio')).json()).active, { timeout: 30_000 }).toBeNull();
}

test.afterAll(async () => {
  // Deja el entorno como lo encontró, también si se cortó a mitad: sin conversación activa, sin clave y sin el catálogo de prueba.
  await stopActive().catch(() => undefined);
  const admin = await as('admin');
  await admin.delete('/api/assistant/key');
  const model = await (await admin.get('/api/model/entities')).json() as { name: string }[];
  const mine = ['StE2eTarea', 'StE2eHostil'].filter((n) => model.some((e) => e.name === n));
  if (mine.length) await applyPlan(admin, { description: 'E2E 037: limpieza', operations: mine.map((entity) => ({ op: 'archiveEntity', entity })) });
  for (const r of held.splice(0)) r.destroy();
  server.closeAllConnections();
  await new Promise((r) => server.close(r));
});

async function pageAs(browser: Browser, user: string, viewport = { width: 1280, height: 800 }): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined, viewport });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

// ---------------- Localizadores ----------------
const log = (page: Page) => page.getByRole('log', { name: 'Conversación' });
const turnOf = (page: Page, n: number) => log(page).getByRole('region', { name: `Mensaje ${n}` });
const input = (page: Page) => page.getByRole('textbox', { name: 'Escribe tu mensaje' });
const stopButton = (page: Page) => page.getByRole('button', { name: 'Detener respuesta' });
const proposal = (page: Page) => log(page).getByRole('article', { name: 'Propuesta', exact: true });
const bar = (page: Page) => page.getByRole('navigation', { name: 'Conversaciones' });
const sessionId = (page: Page) => Number(new URL(page.url()).searchParams.get('s'));

/** Empieza una conversación desde la bienvenida con Enter (H2 CA2) y espera a que se abra. */
async function startChat(page: Page, prompt: string, opts: { aislado?: boolean } = {}) {
  await page.goto('/admin/studio');
  await input(page).fill(prompt);
  if (opts.aislado === false) await page.getByRole('switch', { name: 'Probar en una copia aislada' }).uncheck();
  await tras(page.waitForResponse((r) => r.url().endsWith('/api/studio/sessions') && r.request().method() === 'POST'),
    () => input(page).press('Enter'));
  await expect(log(page)).toBeVisible();
  return sessionId(page);
}

/** Manda un mensaje de seguimiento con Enter. */
async function followUp(page: Page, text: string) {
  await input(page).fill(text);
  await tras(page.waitForResponse((r) => /\/messages$/.test(r.url()) && r.request().method() === 'POST'), () => input(page).press('Enter'));
}

/** Espera a que el turno termine (el botón vuelve a ser «Enviar mensaje»). */
async function idle(page: Page) {
  await expect(stopButton(page)).toHaveCount(0, { timeout: 60_000 });
  await expect(page.getByRole('button', { name: 'Enviar mensaje' })).toBeVisible();
}

const scrollTop = (page: Page) => page.locator('.chat-scroll').evaluate((e) => e.scrollTop);

const plan = (operations: object[], description = 'Peso en tareas') => ({ description, operations });
const PLAN_PESO = plan([
  { op: 'addField', entity: 'StE2eTarea', field: { name: 'Peso', label: 'Peso (kg)', type: 'decimal', precision: 18, scale: 2 } },
  { op: 'setRules', entity: 'StE2eTarea', rules: [{ name: 'PesoPositivo', field: 'Peso', message: 'El peso no puede ser negativo.', assert: 'Peso >= 0' }] },
  { op: 'archiveField', entity: 'StE2eTarea', field: 'Notas' },
]);
const PLAN_PESO_2 = plan([
  { op: 'addField', entity: 'StE2eTarea', field: { name: 'PesoNeto', label: 'Peso neto (kg)', type: 'decimal', precision: 18, scale: 3 } },
], 'Peso neto en tareas');

/** Texto en Markdown del agente, escrito despacio (20 caracteres cada 150 ms ≈ 130 car/s). */
const MD = [
  'Voy a revisar el catálogo **Tarea Studio** antes de proponer nada.',
  '',
  'Lo que haré:',
  '- Ver sus campos y reglas',
  '- Probar en una copia aislada',
  '',
  '| Campo | Tipo |',
  '|---|---|',
  '| Peso | decimal |',
  '',
  '```json',
  '{ "regla": "Peso >= 0" }',
  '```',
].join('\n');

test('preparar: catálogos, un modelador y un lector', async () => {
  const admin = await as('admin');
  await applyPlan(admin, { description: 'E2E 037: catálogos', operations: [
    { op: 'createEntity', name: 'StE2eTarea', label: 'Tarea Studio', labelPlural: 'Tareas Studio', group: GROUP, icon: 'list-checks', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true }, { name: 'Monto', label: 'Monto', type: 'decimal', precision: 18, scale: 2 },
      { name: 'Notas', label: 'Notas', type: 'text' }] },
    { op: 'createEntity', name: 'StE2eHostil', label: 'Hostil Studio', labelPlural: 'Hostiles Studio', group: GROUP, icon: 'skull',
      description: HOSTIL, fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
  ] });
  const res = await admin.post('/api/admin/roles', { data: { name: 'ModeladorStudio', description: 'E2E spec 037', canManageModel: true, canManageUsers: false,
    canViewAudit: false, permissions: [{ entity: 'StE2eTarea', canRead: true, canCreate: true, canUpdate: true, canDelete: false }] } });
  expect([200, 409], await res.text()).toContain(res.status());
  const roles = await (await admin.get('/api/admin/roles')).json() as { id: number; name: string }[];
  const modelador = roles.find((r) => r.name === 'ModeladorStudio')!.id;
  for (const [username, ids] of [['e2e-st-modelador', [modelador]], ['e2e-st-lector', [await roleId(admin, 'Usuario')]]] as const) {
    const u = await admin.post('/api/admin/users', { data: { username, displayName: username, password: PWD, roleIds: ids } });
    expect([201, 409], await u.text()).toContain(u.status());
  }
});

test('H1 CA2: sin clave de Anthropic la pantalla lo explica y no ofrece escribir', async ({ page }) => {
  const admin = await as('admin');
  await admin.delete('/api/assistant/key');
  await page.goto('/admin/studio');
  await expect(page.getByText('ERP AI Studio no está configurado')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Configuración › Asistente IA' })).toBeVisible();
  await expect(input(page)).toHaveCount(0);
  const key = await admin.put('/api/assistant/key', { data: { apiKey: API_KEY } });
  expect(key.status(), await key.text()).toBe(200);
  await page.reload();
  await expect(input(page)).toBeVisible();
});

test('H1 CA2: un modelador ve ERP AI Studio en Configuración; un lector no ve Studio ni llega por URL', async ({ browser }) => {
  const modelador = await pageAs(browser, 'e2e-st-modelador');
  await modelador.goto('/admin');
  await modelador.getByRole('main').getByRole('link', { name: /ERP AI Studio/ }).click();
  await expect(modelador.getByRole('heading', { name: 'ERP AI Studio', level: 1 })).toBeVisible();
  await expect(modelador.getByRole('heading', { name: 'Hola, e2e-st-modelador' })).toBeVisible();
  await expect(input(modelador)).toBeVisible();
  await modelador.context().close();

  const lector = await pageAs(browser, 'e2e-st-lector');
  await expect(lector.getByRole('link', { name: /ERP AI Studio/ })).toHaveCount(0);
  await lector.goto('/admin/studio');
  await expect(lector.getByText('Sin acceso')).toBeVisible();
  await expect(input(lector)).toHaveCount(0);
  await lector.context().close();
});

test('H1 CA2: la bienvenida saluda, sus sugerencias rellenan el compositor y los alcances se eligen', async ({ page }) => {
  await page.goto('/admin/studio');
  await expect(page.getByRole('heading', { name: /^Hola/ })).toBeVisible();
  await expect(bar(page).getByText('Aún no hay conversaciones.')).toBeVisible();
  await expect(input(page)).toBeFocused();
  const suggestion = page.getByRole('list', { name: 'Sugerencias' }).getByRole('button').first();
  const text = (await suggestion.textContent())!;
  await suggestion.click();
  await expect(input(page)).toHaveValue(text);
  await expect(input(page)).toBeFocused();
  expect(received.length, 'una sugerencia no se envía sola').toBe(0);

  const datos = page.getByRole('switch', { name: 'Leer registros (datos)' });
  await expect(datos).not.toBeChecked();
  await expect(page.getByRole('switch', { name: 'Modelo del ERP' })).toBeChecked();
  await expect(page.getByRole('switch', { name: 'Modelo del ERP' })).toBeDisabled();
  await expect(page.getByText('Los registros que el agente lea viajan a Anthropic')).toHaveCount(0);
  await datos.check();
  await expect(page.getByText('Los registros que el agente lea viajan a Anthropic')).toBeVisible();
  await expect(page.getByRole('switch', { name: 'Probar en una copia aislada' })).toBeChecked();
  await expectAccessible(page);

  // Un servidor sin copia aislada: la opción se ve, apagada y con su motivo.
  await page.route('**/api/studio', async (route) => {
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), sandboxAvailable: false } });
  });
  await page.reload();
  const sandbox = page.getByRole('switch', { name: 'Probar en una copia aislada' });
  await expect(sandbox).toBeDisabled();
  await expect(sandbox).not.toBeChecked();
  await expect(page.getByText(/No disponible en este servidor/)).toBeVisible();
  await page.unroute('**/api/studio');
});

test('H2 CA1: el compositor crece de una línea hasta 384 px, se desplaza por dentro y vuelve a una línea', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/admin/studio');
  const box = input(page);
  const h = () => box.evaluate((e) => Math.round(e.getBoundingClientRect().height));
  const lines = (n: number) => Array.from({ length: n }, (_, i) => `línea ${i + 1}`).join('\n');
  await box.fill('una línea');
  const one = await h();
  // Una línea: la mínima (en la bienvenida, dos filas como en el análisis: ≤ 57 px); después, 21 px por línea.
  expect(one).toBeGreaterThanOrEqual(28);
  expect(one).toBeLessThanOrEqual(57);
  await box.fill(lines(3));
  const three = await h();
  await box.fill(lines(5));
  expect(await h()).toBe(three + 2 * 21);
  await box.fill(lines(40));
  expect(await h()).toBe(384);
  expect(await box.evaluate((e) => getComputedStyle(e).overflowY)).toBe('auto');
  await box.fill('');
  expect(await h()).toBe(one);
  // Shift+Enter inserta una línea y no envía.
  await box.fill('primera');
  await box.press('Shift+Enter');
  await box.pressSequentially('segunda');
  await expect(box).toHaveValue('primera\nsegunda');
  expect(received.length).toBe(0);
});

const VIVO = '[guion:vivo] Agrega el peso a las tareas con una regla que no permita pesos negativos, y pruébala.';
let vivoId = 0;

test('H3 CA1 H2 CA2: el texto llega en vivo con fundido por palabra y el compositor queda listo', async ({ page }) => {
  scripts.vivo = steps(
    { blocks: [say(MD), tool('listar_catalogos')], delayMs: 1500, chunkMs: 150 },
    { blocks: [tool('ver_catalogo', { entity: 'StE2eTarea' })], delayMs: 800 },
    { blocks: [tool('compilar_plan', { plan: PLAN_PESO })], delayMs: 800 },
    { blocks: [tool('aislado_aplicar_plan', { plan: PLAN_PESO }), tool('aislado_crear', { entity: 'StE2eTarea', values: { Nombre: 'Negativa', Peso: -1 } }),
      tool('aislado_crear', { entity: 'StE2eTarea', values: { Nombre: 'Positiva', Peso: 2.5 } })], delayMs: 500 },
    deliver({
      summary: 'Agregué **Peso (kg)** a Tareas Studio con una regla que rechaza pesos negativos. Archivo «Notas», que no se usa.',
      plan: PLAN_PESO,
      tested: [{ description: 'Un peso negativo se rechaza con el mensaje de la regla', ok: true, detail: '400: El peso no puede ser negativo.' },
        { description: 'Un peso positivo se guarda', ok: true }],
      failures: ['No probé la importación masiva con pesos.'],
      risks: ['Las tareas existentes quedan sin peso hasta que alguien lo capture.'],
    }),
    // Respuesta 6 (H2 CA3): un seguimiento que contesta despacio, sin plan.
    { blocks: [say(Array.from({ length: 40 }, (_, i) => `Párrafo ${i + 1}: la regla se evalúa al guardar y el campo vacío no la viola.`).join('\n\n'))], delayMs: 1500, chunkMs: 60 },
    // Respuesta 7 (H5 CA2): una entrega nueva reemplaza la propuesta.
    deliver({ summary: 'Mejor un peso neto con tres decimales.', plan: PLAN_PESO_2 }),
  );
  vivoId = await startChat(page, VIVO);
  // H2 CA2: tras enviar, el compositor queda vacío y con el foco; el mensaje se ve a la derecha.
  await expect(input(page)).toHaveValue('');
  await expect(input(page)).toBeFocused();
  await expect(turnOf(page, 1).getByText(VIVO)).toBeVisible();
  // Esperando: el punto que late y el estado (anunciado en aria-live, nunca palabra por palabra).
  await expect(turnOf(page, 1).getByText(/^(En cola|Pensando)…$/)).toBeVisible();
  await expect(stopButton(page)).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: /^(En cola|Pensando)…$/ })).toHaveCount(1);

  // El texto entra por palabras (cada una en su span con fundido) mientras el paso aún no existe.
  const liveWords = turnOf(page, 1).locator('.chat-live .chat-w');
  await expect(liveWords.first()).toBeVisible({ timeout: 30_000 });
  expect(await liveWords.first().evaluate((e) => getComputedStyle(e).animationName)).toBe('chat-word-in');
  const n1 = await liveWords.count();
  await expect.poll(() => liveWords.count()).toBeGreaterThan(n1);
  await expect(log(page)).toHaveAttribute('aria-live', 'off');
  await expect(page.getByRole('status').filter({ hasText: 'Escribiendo…' })).toHaveCount(1);

  // Al guardarse, el Markdown: negritas, lista, tabla y bloque de código, sin spans de animación.
  await idle(page);
  const t1 = turnOf(page, 1);
  await expect(t1.locator('strong').filter({ hasText: 'Tarea Studio' }).first()).toBeVisible();
  await expect(t1.getByRole('listitem').filter({ hasText: 'Probar en una copia aislada' })).toBeVisible();
  await expect(t1.getByRole('region', { name: 'Tabla' }).getByRole('cell', { name: 'decimal' })).toBeVisible();
  await expect(t1.getByLabel('Código json')).toContainText('"regla": "Peso >= 0"');
  // Regresión (revisión exploratoria 037, F-11): sin ligaduras, `>=` no se ve como «≥».
  expect(await t1.getByLabel('Código json').evaluate((e) => getComputedStyle(e).fontVariantLigatures)).toBe('none');
  await expect(page.getByRole('status').filter({ hasText: 'Respuesta lista.' })).toHaveCount(1);
  await expect(bar(page).getByRole('button', { name: /Agrega el peso a las tareas/ })).toHaveAttribute('aria-current', 'page');
});

test('H3 CA2: las herramientas se pliegan en una línea y cada una abre su entrada, su salida y su error', async ({ page }) => {
  test.skip(!vivoId, 'depende de la conversación «vivo»');
  await page.goto(`/admin/studio?s=${vivoId}`);
  const t1 = turnOf(page, 1);
  // Las herramientas seguidas van en un solo grupo plegado; la creación de la tarea negativa falló por la regla.
  const group = t1.getByRole('button', { name: /^Usó \d+ herramientas · 1 con error$/ });
  await expect(group).toHaveAttribute('aria-expanded', 'false');
  await expect(t1.getByRole('list', { name: 'Herramientas' })).toHaveCount(0);
  await group.click();
  await expect(group).toHaveAttribute('aria-expanded', 'true');
  const list = t1.getByRole('list', { name: 'Herramientas' });
  for (const name of ['listar_catalogos', 'ver_catalogo', 'compilar_plan', 'aislado_preparar', 'aislado_aplicar_plan']) {
    await expect(list.getByRole('button').filter({ hasText: name })).toHaveCount(1);
  }
  const crear = list.getByRole('button').filter({ hasText: 'aislado_crear' });
  await expect(crear).toHaveCount(2);
  await expect(crear.first()).toContainText('Con error');
  await expect(crear.last()).toContainText('Correcta');
  const ver = list.getByRole('button').filter({ hasText: 'ver_catalogo' });
  await ver.click();
  await expect(ver).toHaveAttribute('aria-expanded', 'true');
  await expect(list.getByLabel('Entrada de ver_catalogo')).toContainText('StE2eTarea');
  await expect(list.getByLabel('Salida de ver_catalogo')).toContainText('Tarea Studio');
  await crear.first().click();
  await expect(list.getByLabel('Salida de aislado_crear')).toContainText('El peso no puede ser negativo.');
  await expect(list.locator('.chat-tool-time').first()).toHaveText(/\d+ (ms|s)$/);
  await expectAccessible(page);
});

test('H5 CA1 CA4: la propuesta se revisa en el chat, con lo que el agente informa y el consumo contra los topes', async ({ page }) => {
  test.skip(!vivoId, 'depende de la conversación «vivo»');
  await page.goto(`/admin/studio?s=${vivoId}`);
  const card = proposal(page);
  await expect(card).toBeVisible();
  await expect(turnOf(page, 1).locator('strong').filter({ hasText: 'Peso (kg)' })).toBeVisible();
  const informe = turnOf(page, 1).getByRole('region', { name: 'Lo que el agente informa' });
  await expect(informe.getByText('Es una afirmación del agente: compárala con sus herramientas.')).toBeVisible();
  await expect(informe.getByRole('listitem').filter({ hasText: 'Un peso negativo se rechaza' })).toContainText('Superada');
  await expect(informe.getByText('No probé la importación masiva con pesos.')).toBeVisible();
  await expect(informe.getByText(/no ejecutó ninguna herramienta en la copia aislada/)).toHaveCount(0);
  await expect(card.getByText('Propuesta lista', { exact: true })).toBeVisible();
  await expect(card.getByText('Archiva el campo «Notas» de «Tarea Studio» (sus datos se conservan).')).toBeVisible();
  await expect(card.getByText('El plan es válido')).toBeVisible();
  await expect(card.getByRole('button', { name: 'Revisar y aplicar…' })).toBeEnabled();

  // H5 CA4: el consumo de la conversación, en un panel plegable.
  await expect(page.getByRole('meter')).toHaveCount(0);
  await page.getByRole('button', { name: 'Consumo de la conversación' }).click();
  await expect(page.getByRole('meter', { name: 'Mensajes' })).toHaveAttribute('aria-valuetext', '1 de 20');
  await expect(page.getByRole('meter', { name: 'Pasos' })).toHaveAttribute('aria-valuenow', '5');
  await expect(page.getByRole('meter', { name: 'Tokens' })).toHaveAttribute('aria-valuetext', /8,750 de 1,500,000/);
  await expect(page.getByRole('meter', { name: 'Costo estimado' })).toHaveAttribute('aria-valuetext', /0\.0275 USD de 5\.00 USD/);
  await expect(page.getByRole('meter', { name: 'Herramientas' })).toBeVisible();
  await expectAccessible(page);
});

// Capturas: la conversación (con su propuesta al final) y la bienvenida, en claro, oscuro y móvil.
for (const [name, scheme, viewport] of [['light', 'light', { width: 1280, height: 1000 }], ['dark', 'dark', { width: 1280, height: 1000 }], ['movil', 'light', { width: 390, height: 844 }]] as const) {
  test(`visual: la conversación y la bienvenida en ${name === 'movil' ? 'móvil' : name === 'light' ? 'claro' : 'oscuro'}`, async ({ browser }) => {
    test.skip(!vivoId, 'depende de la conversación «vivo»');
    const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json', viewport, colorScheme: scheme, reducedMotion: 'reduce' });
    const page = await ctx.newPage();
    await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    await page.goto(`/admin/studio?s=${vivoId}`);
    await expect(proposal(page).getByText('El plan es válido')).toBeVisible();
    await page.mouse.move(0, 0);
    await page.evaluate(() => document.fonts.ready);
    // Lo que depende del momento: horas relativas y duraciones de las herramientas.
    const mask: Locator[] = [page.locator('.studio-date'), page.locator('.studio-duration')];
    await expect(page.getByRole('main')).toHaveScreenshot(`studio-chat-${name}.png`, { mask });
    await page.goto('/admin/studio');
    await expect(page.getByRole('heading', { name: /^Hola/ })).toBeVisible();
    await page.mouse.move(0, 0);
    await expect(page.getByRole('main')).toHaveScreenshot(`studio-bienvenida-${name}.png`, { mask });
    if (name === 'movil') {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    }
    await ctx.close();
  });
}

test('H2 CA3: el mensaje enviado sube arriba, la respuesta crece sin mover la vista e «Ir al final» lleva al final', async ({ page }) => {
  test.skip(!vivoId, 'depende de la conversación «vivo»');
  await page.goto(`/admin/studio?s=${vivoId}`);
  await expect(proposal(page)).toBeVisible();
  // Abierta y terminada: se ve el final, sin «Ir al final».
  await expect(page.getByRole('button', { name: 'Ir al final' })).toHaveCount(0);
  await followUp(page, '¿Cuándo se evalúa la regla?');
  const t2 = turnOf(page, 2);
  await expect(t2.getByText('¿Cuándo se evalúa la regla?')).toBeVisible();
  // El mensaje queda en el borde superior de la conversación (desplazamiento suave).
  const offset = () => page.evaluate(() => {
    const sc = document.querySelector('.chat-scroll')!.getBoundingClientRect();
    const users = document.querySelectorAll('.chat-user');
    return Math.round(users[users.length - 1].getBoundingClientRect().top - sc.top);
  });
  await expect.poll(offset, { timeout: 5000 }).toBeGreaterThanOrEqual(0);
  await expect.poll(offset, { timeout: 5000 }).toBeLessThanOrEqual(40);
  // Sin auto-scroll mientras llega el texto.
  const words = t2.locator('.chat-live .chat-w');
  await expect(words.first()).toBeVisible({ timeout: 30_000 });
  const top = await scrollTop(page);
  const n = await words.count();
  await expect.poll(() => words.count(), { timeout: 15_000 }).toBeGreaterThan(n + 40);
  expect(Math.abs((await scrollTop(page)) - top)).toBeLessThanOrEqual(1);
  // El final quedó abajo: «Ir al final» aparece y lleva ahí.
  await expect(page.getByRole('button', { name: 'Ir al final' })).toBeVisible({ timeout: 30_000 });
  await idle(page);
  await page.getByRole('button', { name: 'Ir al final' }).click();
  await expect(page.getByRole('button', { name: 'Ir al final' })).toHaveCount(0);
  await expect(t2.getByText(/Párrafo 40:/)).toBeInViewport();
  // Una respuesta sin plan no retira la propuesta.
  await expect(proposal(page).getByText('Propuesta lista', { exact: true })).toBeVisible();
});

test('H5 CA2: una entrega nueva reemplaza la propuesta, que queda sin acciones', async ({ page }) => {
  test.skip(!vivoId, 'depende de la conversación «vivo»');
  await page.goto(`/admin/studio?s=${vivoId}`);
  await followUp(page, 'Mejor con tres decimales.');
  await idle(page);
  const old = log(page).getByRole('article', { name: 'Propuesta reemplazada' });
  await expect(old).toBeVisible();
  await expect(old.getByText('Reemplazada', { exact: true })).toBeVisible();
  await expect(old.getByRole('button')).toHaveCount(0);
  await expect(proposal(page)).toHaveCount(1);
  await expect(turnOf(page, 3).getByRole('article', { name: 'Propuesta', exact: true }).getByText('Peso neto en tareas', { exact: true })).toBeVisible();
  await expect(proposal(page).getByRole('button', { name: 'Revisar y aplicar…' })).toBeEnabled();
});

test('H5 CA2 CA3: se aplica con la huella; si el modelo cambió, se recarga; y la conversación sigue', async ({ page }) => {
  const admin = await as('admin');
  scripts.aplicar = steps(deliver({
    summary: 'Agrego la referencia a las tareas.',
    plan: plan([{ op: 'addField', entity: 'StE2eTarea', field: { name: 'Referencia', label: 'Referencia', type: 'text' } }], 'Referencia en tareas'),
    tested: [{ description: 'La referencia se guarda', ok: true }], failures: [], risks: [],
  }), { blocks: [say('De nada.')] });
  await startChat(page, '[guion:aplicar] Agrega una referencia a las tareas.', { aislado: false });
  await idle(page);
  const card = proposal(page);
  // R-033-11: dice haber probado sin correr nada en la copia: la pantalla lo advierte.
  await expect(turnOf(page, 1).getByText(/no ejecutó ninguna herramienta en la copia aislada/)).toBeVisible();

  // Otra persona cambia el catálogo después de la revisión: la huella ya no vale. (Carrera de la prueba vista con --lento:
  // el turno ya terminó, pero la lectura completa con la huella llega después; se espera a tener la propuesta revisable.)
  await expect(card.getByRole('button', { name: 'Revisar y aplicar…' })).toBeVisible();
  await applyPlan(admin, plan([{ op: 'updateField', entity: 'StE2eTarea', field: 'Monto', label: 'Monto total' }], 'E2E 037: cambio ajeno'));
  await card.getByRole('button', { name: 'Revisar y aplicar…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Aplicar la propuesta de ERP AI Studio' });
  await expect(dialog.getByText(/Se aplicarán 1 operaciones/)).toBeVisible();
  await expectToast(page, /El modelo cambió/, () => dialog.getByRole('button', { name: 'Aplicar cambios' }).click());
  await expect(card.getByText('La propuesta se actualizó')).toBeVisible();

  await card.getByRole('button', { name: 'Revisar y aplicar…' }).click();
  await expectToast(page, 'Cambios aplicados', () => dialog.getByRole('button', { name: 'Aplicar cambios' }).click());
  await expect(card.getByText('Aplicada', { exact: true })).toBeVisible();
  await expect(card.getByText(/Conjunto de cambios #\d+/)).toBeVisible();
  // Regresión (revisión exploratoria 037, F-12): la fecha terminaba en «p.m..».
  expect(await card.getByText(/Conjunto de cambios #\d+/).textContent()).not.toMatch(/\.\.$/);
  await expect(card.getByRole('button', { name: 'Revisar y aplicar…' })).toHaveCount(0);
  const fields = (await (await admin.get('/api/model/entities/StE2eTarea')).json()).fields as { name: string }[];
  expect(fields.map((f) => f.name)).toContain('Referencia');
  // H5 CA3: aplicar no cierra la conversación.
  await expect(input(page)).toBeEnabled();
  await followUp(page, 'Gracias.');
  await idle(page);
  await expect(turnOf(page, 2).getByText('De nada.')).toBeVisible();
});

let aprobacionId = 0;

test('H5 CA3: en un entorno con aprobación no se aplica y se descarga el plan; descartar la cierra', async ({ page }) => {
  scripts.aprobacion = steps(deliver({
    summary: 'Agrego un folio a las tareas.',
    plan: plan([{ op: 'addField', entity: 'StE2eTarea', field: { name: 'Folio', label: 'Folio', type: 'text' } }], 'Folio en tareas'),
  }));
  aprobacionId = await startChat(page, '[guion:aprobacion] Agrega un folio a las tareas.', { aislado: false });
  await idle(page);
  // La suite permite cambios directos; la respuesta del servidor con aprobación se reproduce en la lectura completa.
  const reason = 'Este entorno exige la aprobación de otro administrador: la propuesta no se aplica aquí. Los cambios al modelo llegan como paquete desde Configuración › Entornos.';
  await page.route(/\/api\/studio\/sessions\/\d+$/, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), canApply: false, blockedReason: reason } });
  });
  await page.reload();
  const card = proposal(page);
  await expect(card.getByText('No se aplica en este entorno')).toBeVisible();
  await expect(card.getByRole('button', { name: 'Revisar y aplicar…' })).toHaveCount(0);
  const download = await tras(page.waitForEvent('download'), () => card.getByRole('button', { name: 'Descargar plan' }).click());
  expect(download.suggestedFilename()).toBe(`erp-ai-studio-sesion-${aprobacionId}.json`);
  await page.unroute(/\/api\/studio\/sessions\/\d+$/);

  await expectToast(page, 'Propuesta descartada', () => card.getByRole('button', { name: 'Descartar' }).click());
  await expect(card.getByText('Descartada', { exact: true })).toBeVisible();
  await expect(card.getByRole('button', { name: 'Descartar' })).toHaveCount(0);
  await expect(card.getByRole('button', { name: 'Revisar y aplicar…' })).toHaveCount(0);
  await expect(input(page)).toBeEnabled();
});

test('H3 CA3: el texto del agente y las salidas son dato: Markdown seguro, sin HTML ni enlaces', async ({ page }) => {
  scripts.hostil = steps(
    { blocks: [say(`Revisé esto: ${HOSTIL}\n\n**negritas reales** y <b>falsas</b>`), tool('ver_catalogo', { entity: 'StE2eHostil' })] },
    deliver({ summary: `Resumen ${HOSTIL}`, failures: [`Falla ${HOSTIL}`] }),
  );
  await startChat(page, '[guion:hostil] Revisa el catálogo hostil.', { aislado: false });
  await idle(page);
  const t1 = turnOf(page, 1);
  await t1.getByRole('button', { name: /^Usó/ }).click();
  await t1.getByRole('list', { name: 'Herramientas' }).getByRole('button').first().click();
  await expect(t1.getByLabel('Salida de ver_catalogo')).toContainText('onerror');
  await expect(t1.getByText('Resumen <img src=x onerror="window.__xss=1">', { exact: false })).toBeVisible();
  await expect(t1.getByText(/\[Haz clic aquí\]\(https:\/\/phishing\.example\/login\)/).first()).toBeVisible();
  await expect(t1.locator('strong').filter({ hasText: 'negritas reales' })).toBeVisible();
  await expect(t1.getByText('<b>falsas</b>', { exact: false })).toBeVisible();
  await expect(t1.getByRole('listitem').filter({ hasText: 'Falla <img src=x' })).toBeVisible();
  await expect(log(page).locator('a, img, b, script, iframe')).toHaveCount(0);
  expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
});

test('H4 CA1 CA2 CA3: detener conserva lo escrito; reintentar y editar mandan un mensaje nuevo', async ({ page }) => {
  const LARGO = Array.from({ length: 200 }, (_, i) => `palabra${i}`).join(' ');
  scripts.detener = calls(
    { blocks: [say(LARGO)], delayMs: 500, chunkMs: 200 },
    { blocks: [say('Respuesta completa del reintento.')] },
    { blocks: [say('Respuesta al mensaje editado.')] },
  );
  received.marca();
  await startChat(page, '[guion:detener] Escribe algo largo.', { aislado: false });
  await received.siguiente((r) => r.tag === 'detener');
  await expect(turnOf(page, 1).locator('.chat-live .chat-w').first()).toBeVisible({ timeout: 30_000 });
  await stopButton(page).click();
  const t1 = turnOf(page, 1);
  await expect(t1.getByText('Detuviste la respuesta.')).toBeVisible({ timeout: 30_000 });
  await expect(input(page)).toBeFocused();
  await expect(t1.getByText(/palabra0 palabra1/)).toBeVisible();
  await expect(t1.getByText(/palabra199/)).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: 'Detuviste la respuesta.' })).toHaveCount(1);

  // H4 CA2: «Intentar de nuevo» manda otra vez el último mensaje.
  received.marca();
  await tras(page.waitForResponse((r) => /\/messages$/.test(r.url())), () => t1.getByRole('button', { name: 'Intentar de nuevo' }).click());
  expect((await received.siguiente((r) => r.tag === 'detener')).last).toContain('Escribe algo largo.');
  await idle(page);
  await expect(turnOf(page, 2).getByText('[guion:detener] Escribe algo largo.')).toBeVisible();
  await expect(turnOf(page, 2).getByText('Respuesta completa del reintento.')).toBeVisible();

  // H4 CA3: editar el último mensaje en su lugar; Esc cancela y deja el foco en el compositor.
  const t2 = turnOf(page, 2);
  await t2.getByText('[guion:detener] Escribe algo largo.').hover();
  await t2.getByRole('button', { name: 'Editar el mensaje' }).click();
  const edit = t2.getByRole('textbox', { name: 'Editar el mensaje' });
  await expect(edit).toBeFocused();
  await expect(t2.getByRole('button', { name: 'Enviar', exact: true })).toBeDisabled();
  await edit.press('Escape');
  await expect(edit).toHaveCount(0);
  await expect(input(page)).toBeFocused();
  await t2.getByText('[guion:detener] Escribe algo largo.').hover();
  await t2.getByRole('button', { name: 'Editar el mensaje' }).click();
  await edit.fill('Escribe algo corto.');
  await expect(t2.getByText('Se envía como un mensaje nuevo; el anterior y su respuesta quedan en la conversación.')).toBeVisible();
  await tras(page.waitForResponse((r) => /\/messages$/.test(r.url())), () => t2.getByRole('button', { name: 'Enviar', exact: true }).click());
  await idle(page);
  await expect(turnOf(page, 3).getByText('Escribe algo corto.')).toBeVisible();
  await expect(turnOf(page, 3).getByText('Respuesta al mensaje editado.')).toBeVisible();
  await expect(t2.getByText('Respuesta completa del reintento.')).toBeVisible();
  // Solo el último mensaje se edita.
  await t2.getByText('[guion:detener] Escribe algo largo.').hover();
  await expect(t2.getByRole('button', { name: 'Editar el mensaje' })).toHaveCount(0);
});

test('H4 CA2: un turno que falla lo dice al final, con «Intentar de nuevo»', async ({ page }) => {
  scripts.rechazo = calls({ blocks: [say('Empiezo a escribir y me corto')], stop: 'max_tokens' }, { blocks: [say('Ahora sí.')] });
  await startChat(page, '[guion:rechazo] Algo que no cabe en una respuesta.', { aislado: false });
  await idle(page);
  const t1 = turnOf(page, 1);
  await expect(t1.getByText('La respuesta del modelo quedó incompleta. Pide un desarrollo más acotado.')).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'La respuesta no terminó.' })).toHaveCount(1);
  await tras(page.waitForResponse((r) => /\/messages$/.test(r.url())), () => t1.getByRole('button', { name: 'Intentar de nuevo' }).click());
  await idle(page);
  await expect(turnOf(page, 2).getByText('Ahora sí.')).toBeVisible();
});

test('H4 CA4: copiar el mensaje, la respuesta y un bloque de código, tal cual', async ({ page }) => {
  test.skip(!vivoId, 'depende de la conversación «vivo»');
  await page.goto(`/admin/studio?s=${vivoId}`);
  const t1 = turnOf(page, 1);
  const clip = () => page.evaluate(() => navigator.clipboard.readText());
  await t1.getByLabel('Código json').scrollIntoViewIfNeeded();
  await expectToast(page, 'Copiado', () => t1.getByRole('button', { name: 'Copiar el código' }).click());
  expect(await clip()).toBe('{ "regla": "Peso >= 0" }');
  await t1.getByText(VIVO).hover();
  await expectToast(page, 'Copiado', () => t1.getByRole('button', { name: 'Copiar el mensaje' }).click());
  expect(await clip()).toBe(VIVO);
  await t1.getByRole('region', { name: 'Tabla' }).hover();
  await expectToast(page, 'Copiado', () => t1.getByRole('button', { name: 'Copiar la respuesta' }).click());
  const copied = await clip();
  expect(copied).toContain('| Peso | decimal |');
  expect(copied).toContain('Agregué **Peso (kg)**');
});

test('H3 CA4: si el flujo se corta, se reconecta sin repetir pasos; si no puede, «Sin conexión» con «Intentar de nuevo»', async ({ page }) => {
  scripts.red = calls(
    { blocks: [tool('listar_catalogos')] },
    { blocks: [say('Terminé tras el corte.')], delayMs: 22_000 },
  );
  let cut = true;
  let streams = 0;
  await page.route(/\/api\/studio\/sessions\/\d+\/stream/, async (route) => {
    streams++;
    if (cut) await route.abort('failed'); else await route.fallback();
  });
  received.marca();
  await startChat(page, '[guion:red] Algo con la red inestable.', { aislado: false });
  await expect(page.getByRole('status').filter({ hasText: 'Reconectando…' })).toHaveCount(1, { timeout: 10_000 });
  // 1 + 2 + 4 + 8 s de espera: a la quinta falla deja de intentar.
  await expect(page.getByRole('alert').filter({ hasText: 'Sin conexión con el avance de la conversación' })).toBeVisible({ timeout: 30_000 });
  const tries = streams;
  expect(tries).toBe(5);
  await page.waitForTimeout(1500);
  expect(streams, 'sin conexión no sigue intentando').toBe(tries);
  cut = false;
  await page.getByRole('button', { name: 'Intentar de nuevo' }).click();
  await idle(page);
  const t1 = turnOf(page, 1);
  await expect(t1.getByText('Terminé tras el corte.')).toBeVisible();
  await t1.getByRole('button', { name: /^Usó/ }).click();
  await expect(t1.getByRole('list', { name: 'Herramientas' }).getByRole('button')).toHaveCount(1);
  await page.unroute(/\/api\/studio\/sessions\/\d+\/stream/);
});

test('H2 CA4: doble Enter manda un mensaje; otra pestaña o un motivo del servidor lo dicen en la vista', async ({ page }) => {
  scripts.doble = calls({ blocks: [say('Hola.')] }, { blocks: [], hold: true });
  const id = await startChat(page, '[guion:doble] Hola.', { aislado: false });
  await idle(page);
  // Dos Enter seguidos: un solo mensaje.
  let posts = 0;
  page.on('request', (r) => { if (/\/messages$/.test(r.url()) && r.method() === 'POST') posts++; });
  await input(page).fill('Algo largo.');
  received.marca();
  await input(page).press('Enter');
  await input(page).press('Enter');
  await received.siguiente((r) => r.tag === 'doble' && r.last.includes('Algo largo.'));
  expect(posts).toBe(1);
  await expect(stopButton(page)).toBeVisible();

  // Otra pestaña con la conversación vieja (aún «se puede enviar») intenta mandar: 409 con su motivo y el texto se conserva.
  const other = await page.context().newPage();
  await other.route(/\/api\/studio\/sessions\/\d+$/, async (route) => {
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), status: 'answered', canSend: true, sendBlockedReason: null } });
  });
  await other.goto(`/admin/studio?s=${id}`);
  await input(other).fill('Desde otra pestaña.');
  await input(other).press('Enter');
  await expect(other.getByRole('alert').filter({ hasText: 'El agente está trabajando en esta conversación' })).toBeVisible();
  await expect(input(other)).toHaveValue('Desde otra pestaña.');
  await other.close();
  await stopButton(page).click();
  await idle(page);

  // Sin poder enviar (p. ej. el tope de mensajes): el compositor dice por qué.
  await page.route(/\/api\/studio\/sessions\/\d+$/, async (route) => {
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), canSend: false, sendBlockedReason: 'Esta conversación llegó al tope de 20 mensajes: empieza una nueva.' } });
  });
  await page.reload();
  await expect(page.getByRole('note').filter({ hasText: 'llegó al tope de 20 mensajes' })).toBeVisible();
  await expect(input(page)).toBeDisabled();
  await page.unroute(/\/api\/studio\/sessions\/\d+$/);
});

test('H1 CA1 CA3: la barra lista por actividad, marca la abierta y la que trabaja; con una en curso se abre esa', async ({ page }) => {
  scripts.larga = steps({ blocks: [], hold: true });
  const other = await page.context().newPage();
  await other.goto('/admin/studio');
  await other.getByRole('textbox', { name: 'Escribe tu mensaje' }).fill('[guion:larga] Otro pedido desde otra pestaña.');

  received.marca();
  const id = await startChat(page, '[guion:larga] Algo largo.', { aislado: false });
  await received.siguiente((r) => r.tag === 'larga');
  // La más reciente arriba, abierta y trabajando.
  const items = bar(page).getByRole('listitem');
  await expect(items.first().getByRole('button')).toHaveAttribute('aria-current', 'page');
  await expect(items.first().getByRole('button')).toHaveAccessibleName(/Algo largo\. Trabajando/);
  // La otra pestaña no puede empezar otra: el motivo en la vista.
  await other.getByRole('textbox', { name: 'Escribe tu mensaje' }).press('Enter');
  await expect(other.getByRole('alert').filter({ hasText: 'Ya tienes una sesión de ERP AI Studio en curso' })).toBeVisible();
  await other.close();

  // «Nueva conversación» vuelve a la bienvenida, que avisa de la que está en curso; al volver a entrar se abre esa.
  await bar(page).getByRole('button', { name: 'Nueva conversación' }).click();
  await expect(page).toHaveURL(/\/admin\/studio$/);
  await expect(page.getByText('Ya tienes una conversación en curso')).toBeVisible();
  await page.goto('/admin/studio');
  await expect(page).toHaveURL(new RegExp(`[?&]s=${id}$`));
  // Abrir otra desde la barra.
  await bar(page).getByRole('button', { name: /Agrega el peso a las tareas/ }).click();
  await expect(page).toHaveURL(new RegExp(`[?&]s=${vivoId}$`));
  await expect(turnOf(page, 1).getByText(VIVO)).toBeVisible();
  await stopActive();
});

test('H6 CA1 CA2: en móvil la barra es un panel, sin desborde; Esc lo cierra y devuelve el foco', async ({ page }) => {
  test.skip(!vivoId, 'depende de la conversación «vivo»');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/admin/studio?s=${vivoId}`);
  await expect(log(page)).toBeVisible();
  await expect(bar(page)).toBeHidden();
  // Regresión (revisión exploratoria 037, F-13): la barra de la app ya dice «ERP AI Studio»; el título de la conversación
  // queda solo para el lector de pantalla (antes había dos títulos y dos íconos de panel iguales).
  await expect(page.getByRole('heading', { name: 'ERP AI Studio', level: 1 })).toHaveCount(1);
  expect((await page.getByRole('heading', { name: 'ERP AI Studio', level: 1 }).boundingBox())!.width).toBeLessThanOrEqual(1);
  const openBar = page.getByRole('button', { name: 'Conversaciones' });
  await openBar.click();
  await expect(bar(page)).toBeVisible();
  await expect(bar(page).getByRole('button', { name: 'Cerrar conversaciones' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(bar(page)).toBeHidden();
  await expect(openBar).toBeFocused();
  // El compositor queda abajo, dentro de la vista.
  const box = await input(page).boundingBox();
  expect(box!.y + box!.height).toBeLessThanOrEqual(844);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await expectAccessible(page);
});

test('H6 CA3: con movimiento reducido no hay fundidos ni latidos y el texto en vivo sale de golpe', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  scripts.quieto = steps({ blocks: [say(Array.from({ length: 120 }, (_, i) => `w${i}`).join(' '))], delayMs: 800, chunkMs: 120 });
  await startChat(page, '[guion:quieto] Escribe despacio.', { aislado: false });
  const live = turnOf(page, 1).locator('.chat-live');
  await expect(live.locator('.chat-w').first()).toBeVisible({ timeout: 30_000 });
  expect(await live.locator('.chat-w').first().evaluate((e) => getComputedStyle(e).animationName)).toBe('none');
  expect(await live.evaluate((e) => getComputedStyle(e).animationName)).toBe('none');
  await idle(page);
  await expect(turnOf(page, 1).getByText(/w119$/)).toBeVisible();
});

// ---------------- Regresiones (revisión adversarial 037) ----------------

test('Revisión adversarial F-04: mientras llega la lectura final de un turno no se puede enviar, y el siguiente se sigue en vivo', async ({ page }) => {
  await stopActive();
  scripts.ventana = calls({ blocks: [say('Uno.')] }, { blocks: [say('Dos.')] }, { blocks: [say('Tres.')], delayMs: 8000 });
  const id = await startChat(page, '[guion:ventana] Cuenta.', { aislado: false });
  await idle(page);
  // La lectura completa del final del turno llega tarde. El flujo ya dijo que terminó, pero el compositor sigue bloqueado
  // hasta esa lectura (el evento `session` no trae `canSend`): no hay hueco para mandar un mensaje sin flujo.
  let held = 0;
  await page.route(new RegExp(`/api/studio/sessions/${id}$`), async (route) => {
    if (route.request().method() === 'GET') { held++; await new Promise((r) => setTimeout(r, 4000)); }
    await route.fallback();
  });
  await followUp(page, 'Sigue.');
  await expect(turnOf(page, 2).getByText('Dos.')).toBeVisible();
  await expect.poll(() => held).toBeGreaterThan(0);
  await expect(input(page)).toBeDisabled();
  await expect(input(page)).toBeEnabled({ timeout: 10_000 });
  await followUp(page, 'Otra vez.');
  // El turno 3 tarda más que una lectura retenida: solo el flujo lo trae.
  await expect(turnOf(page, 3).getByText('Tres.')).toBeVisible({ timeout: 25_000 });
  await page.unroute(new RegExp(`/api/studio/sessions/${id}$`));
});

test('Regresión F-05: un flujo que se corta tras su saludo llega a «Sin conexión»', async ({ page }) => {
  await stopActive();
  scripts.huerfano = calls({ blocks: [], hold: true });
  received.marca();
  const id = await startChat(page, '[guion:huerfano] Algo que tarda.', { aislado: false });
  await received.siguiente((r) => r.tag === 'huerfano');
  // F-05: el flujo manda lo de siempre al abrir (estado en vivo), un evento mal formado y se corta; antes cada evento
  // reiniciaba la cuenta de fallas y reconectaba cada segundo para siempre.
  let opened = 0;
  await page.route(/\/stream\?/, (route) => {
    opened++;
    return route.fulfill({ status: 200, contentType: 'text/event-stream', body: 'event: live\ndata: {"kind":"waiting"}\n\nevent: step\ndata: {mal\n\n' });
  });
  await page.reload();
  await expect(page.getByRole('alert').filter({ hasText: 'Sin conexión con el avance de la conversación' })).toBeVisible({ timeout: 30_000 });
  expect(opened).toBe(5);
  await page.unroute(/\/stream\?/);
  await stopActive();
});

test('Regresión F-03: cambiar de conversación antes de que llegue su lectura no deja un flujo huérfano', async ({ page }) => {
  await stopActive();
  scripts.huerfano2 = calls({ blocks: [], hold: true });
  received.marca();
  const id = await startChat(page, '[guion:huerfano2] Algo que tarda.', { aislado: false });
  await received.siguiente((r) => r.tag === 'huerfano2');
  // F-03: se abre la conversación activa y, antes de que llegue su lectura, se cambia a otra: la lectura tardía no abre
  // un flujo que nadie cerraría.
  const streams: string[] = [];
  page.on('request', (r) => { if (r.url().includes(`/sessions/${id}/stream`)) streams.push(r.url()); });
  await page.route(new RegExp(`/api/studio/sessions/${id}$`), async (route) => {
    await new Promise((r) => setTimeout(r, 3000));
    await route.fallback().catch(() => undefined);
  });
  await page.goto(`/admin/studio?s=${id}`);
  await bar(page).getByRole('button', { name: 'Nueva conversación' }).click();
  await expect(page).toHaveURL(/\/admin\/studio$/);
  await page.waitForTimeout(5000);
  expect(streams, 'ningún flujo de la conversación que se dejó').toEqual([]);
  await page.unroute(new RegExp(`/api/studio/sessions/${id}$`));
  await stopActive();
});

test('Regresión F-07: al reintentar, el foco pasa al compositor sin esperar al servidor', async ({ page }) => {
  await stopActive();
  scripts.foco = calls({ blocks: [say('Hecho.')] }, { blocks: [say('Hecho otra vez.')] });
  await startChat(page, '[guion:foco] Hazlo.', { aislado: false });
  await idle(page);
  await page.route(/\/messages$/, async (route) => { await new Promise((r) => setTimeout(r, 1500)); await route.fallback(); });
  await page.getByRole('button', { name: 'Reintentar' }).click();
  // Antes, «Reintentar» desaparecía y el foco quedaba en body hasta que respondía el servidor.
  expect(await page.evaluate(() => document.activeElement?.getAttribute('aria-label'))).toBe('Escribe tu mensaje');
  await idle(page);
  await expect(turnOf(page, 2).getByText('Hecho otra vez.')).toBeVisible();
  await page.unroute(/\/messages$/);
});
