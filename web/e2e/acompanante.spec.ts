// Spec 047 · Acompañante: panel lateral con avatar. Un proveedor simulado (127.0.0.1, PORTS.asistente: Assistant__BaseUrl de
// la suite) responde como la API de mensajes de Anthropic, en streaming, con `tool_use` según un guion por conversación
// (`[guion:…]` en el primer mensaje), como en studio.spec.ts: el motor de la 046 corre de verdad (herramientas con el alcance de
// la persona, propuestas con su huella, flujo SSE, varios turnos) y la suite nunca llama a Anthropic. La clave es ficticia.
import { createServer, type Server, type ServerResponse } from 'node:http';
import type { APIRequestContext, Locator, Page } from '@playwright/test';
import { ADMIN, PORTS, apiAs, applyPlan, expect, expectAccessible, receptor, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });
// Las pruebas entran como una persona sin rol de modelador (con las guardias de la 030: fixture `page`).
test.use({ storageState: { cookies: [], origins: [] } });

const GROUP = 'Pruebas Acompañante';
const ROLE = 'CapturistaAcompanante';
const USER = 'e2e-ac-ana';
const PWD = 'frase larga del acompañante 2026';
const API_KEY = 'sk-ant-e2e-acompanante-ficticia-0001';
const HOSTIL = '<img src=x onerror="window.__xss=1"> Solo cambio el teléfono, nada más.';
const AVISO = 'El monto supera 1 000: revisa antes de aprobar.';

// ---------------- Proveedor simulado ----------------
type Block = { type: 'text'; text: string } | { type: 'tool_use'; id: string; name: string; input: unknown };
type Turn = { blocks: Block[]; delayMs?: number; chunkMs?: number; hold?: boolean };
let ids = 0;
const say = (text: string): Block => ({ type: 'text', text });
const tool = (name: string, input: object = {}): Block => ({ type: 'tool_use', id: `toolu_ac${++ids}`, name, input });
/** Guion por respuestas del asistente en el historial; después de la última, un texto corto. */
const steps = (...turns: Turn[]) => (i: number): Turn => turns[i] ?? { blocks: [say('Listo.')] };
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
        id: `msg_ac_${received.length}`, type: 'message', role: 'assistant', model: 'claude-sonnet-5-5', content: next.blocks,
        stop_reason: next.blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn', stop_sequence: null,
        usage: { input_tokens: 1200, output_tokens: 200 },
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
let admin: APIRequestContext;
let ana: APIRequestContext;

/** Detiene la conversación activa de la persona, si hay (una activa por persona: la siguiente prueba empieza otra). */
async function stopActive() {
  const st = await (await ana.get('/api/companion')).json().catch(() => null);
  if (!st?.activeId) return;
  await ana.post(`/api/companion/conversations/${st.activeId}/cancel`);
  for (const r of held.splice(0)) r.destroy();
  await expect.poll(async () => (await (await ana.get('/api/companion')).json()).activeId ?? null, { timeout: 30_000 }).toBeNull();
}

test.afterAll(async () => {
  // Deja el entorno como lo encontró, también si se cortó a mitad: sin conversación activa, sin clave y sin el catálogo de prueba.
  if (ana) await stopActive().catch(() => undefined);
  admin ??= await apiAs(ADMIN.username, ADMIN.password);
  await admin.delete('/api/assistant/key');
  const model = await (await admin.get('/api/model/entities')).json() as { name: string }[];
  if (model.some((e) => e.name === 'AcE2eNota')) await applyPlan(admin, { description: 'E2E 047: limpieza', operations: [{ op: 'archiveEntity', entity: 'AcE2eNota' }] });
  for (const r of held.splice(0)) r.destroy();
  server.closeAllConnections();
  await new Promise((r) => server.close(r));
});

// ---------------- Localizadores y pasos ----------------
const toggle = (page: Page) => page.getByRole('button', { name: 'Acompañante', exact: true });
const panel = (page: Page) => page.getByRole('complementary', { name: 'Acompañante' });
const input = (page: Page) => panel(page).getByRole('textbox', { name: 'Escribe tu mensaje' });
const log = (page: Page) => panel(page).getByRole('log', { name: 'Conversación con el Acompañante' });
const stopButton = (page: Page) => panel(page).getByRole('button', { name: 'Detener respuesta' });
const card = (page: Page, name: string | RegExp) => log(page).getByRole('article', { name });

async function login(page: Page) {
  // Con E2E_INDEPENDENT=1 un proceso nuevo (tras una falla) no pasó por «preparar»: la sesión de API se abre aquí.
  ana ??= await apiAs(USER, PWD);
  await uiLogin(page, USER, PWD);
}

async function openPanel(page: Page) {
  if ((await toggle(page).getAttribute('aria-expanded')) !== 'true') await toggle(page).click();
  await expect(panel(page)).toBeVisible();
}

/** Empieza una conversación desde la bienvenida (o la nueva conversación) con Enter. */
async function start(page: Page, text: string) {
  // Spec 048: una conversación que espera respuesta cuenta como activa; la de una prueba anterior se detiene.
  if (ana) await stopActive();
  await openPanel(page);
  // La de la cabecera (en el historial hay otra igual, más abajo).
  await panel(page).getByRole('button', { name: 'Nueva conversación', exact: true }).first().click();
  await input(page).fill(text);
  await tras(page.waitForResponse((r) => r.url().endsWith('/api/companion/conversations') && r.request().method() === 'POST'), () => input(page).press('Enter'));
  await expect(log(page)).toBeVisible();
}

async function followUp(page: Page, text: string) {
  await input(page).fill(text);
  await tras(page.waitForResponse((r) => /\/api\/companion\/conversations\/\d+\/messages$/.test(r.url()) && r.request().method() === 'POST'), () => input(page).press('Enter'));
}

/** Espera a que el turno termine (vuelve «Enviar mensaje»). */
async function idle(page: Page) {
  await expect(stopButton(page)).toHaveCount(0, { timeout: 60_000 });
  await expect(panel(page).getByRole('button', { name: 'Enviar mensaje' })).toBeVisible();
}

async function newNota(values: Record<string, unknown>): Promise<number> {
  const res = await ana.post('/api/data/AcE2eNota', { data: values });
  expect(res.status(), await res.text()).toBe(201);
  return (await res.json()).id as number;
}
const nota = async (id: number) => (await (await ana.get(`/api/data/AcE2eNota/${id}`)).json()) as Record<string, unknown> & { _rowVersion: string };

// ---------------- Pruebas ----------------
test('preparar: catálogo con proceso y aviso, una persona sin rol de modelador y la clave', async () => {
  admin = await apiAs(ADMIN.username, ADMIN.password);
  await applyPlan(admin, { description: 'E2E 047: catálogo', operations: [
    { op: 'createEntity', name: 'AcE2eNota', label: 'Nota Acompañante', labelPlural: 'Notas Acompañante', group: GROUP, icon: 'notebook-pen', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true }, { name: 'Telefono', label: 'Teléfono', type: 'text' },
      { name: 'Monto', label: 'Monto', type: 'decimal', precision: 18, scale: 2 }] },
    { op: 'setRules', entity: 'AcE2eNota', rules: [{ name: 'MontoAlto', field: 'Monto', severity: 'warning', assert: 'ISBLANK(Monto) OR Monto <= 1000', message: AVISO }] },
    { op: 'setWorkflow', entity: 'AcE2eNota', workflow: {
      initial: 'Borrador', states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Aprobada', label: 'Aprobada', color: 'success' }],
      transitions: [{ name: 'aprobar', label: 'Aprobar', from: ['Borrador'], to: 'Aprobada' }] } },
  ] });
  const role = await admin.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 047', canManageModel: false, canManageUsers: false,
    canViewAudit: false, permissions: [{ entity: 'AcE2eNota', canRead: true, canCreate: true, canUpdate: true, canDelete: false }] } });
  expect([200, 409], await role.text()).toContain(role.status());
  const roles = await (await admin.get('/api/admin/roles')).json() as { id: number; name: string }[];
  const u = await admin.post('/api/admin/users', { data: { username: USER, displayName: 'Ana Acompañante', password: PWD, roleIds: [roles.find((r) => r.name === ROLE)!.id] } });
  expect([201, 409], await u.text()).toContain(u.status());
  const key = await admin.put('/api/assistant/key', { data: { apiKey: API_KEY } });
  expect(key.status(), await key.text()).toBe(200);
  ana = await apiAs(USER, PWD);
});

test('H1 CA2: cargando, error con «Intentar de nuevo» y no disponible sin clave', async ({ page }) => {
  admin ??= await apiAs(ADMIN.username, ADMIN.password);
  await login(page);
  // Cargando y error: la primera lectura del estado tarda y falla; «Intentar de nuevo» la repite.
  let release!: () => void;
  const gate = new Promise<void>((r) => (release = r));
  await page.route('**/api/companion', async (route) => { await gate; await route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Error del servidor', status: 500 }) }); });
  await toggle(page).click();
  await expect(panel(page).getByRole('status').filter({ hasText: 'Cargando' })).toBeAttached();
  release();
  await expect(panel(page).getByRole('alert').filter({ hasText: 'No se pudo cargar el Acompañante' })).toBeVisible();
  await page.unroute('**/api/companion');
  await panel(page).getByRole('button', { name: 'Intentar de nuevo' }).click();
  await expect(panel(page).getByRole('heading', { name: 'Hola, Ana' })).toBeVisible();

  // No disponible: sin clave, el motivo del servidor; el botón sigue ahí.
  expect((await admin.delete('/api/assistant/key')).status()).toBeLessThan(300);
  await page.reload();
  await expect(panel(page).getByText('El Acompañante no está disponible', { exact: true })).toBeVisible();
  await expect(panel(page).getByText(/guardar la clave de Anthropic/)).toBeVisible();
  await expect(input(page)).toHaveCount(0);
  const key = await admin.put('/api/assistant/key', { data: { apiKey: API_KEY } });
  expect(key.status(), await key.text()).toBe(200);
  await page.reload();
  await expect(input(page)).toBeVisible();
});

test('H1 CA1 · CA3: una persona sin rol de modelador abre el panel en cualquier pantalla, con teclado, y lo recuerda', async ({ page }) => {
  await login(page);
  await expect(toggle(page)).toHaveAttribute('aria-expanded', 'false');
  await expect(panel(page)).toHaveCount(0);
  await toggle(page).focus();
  await page.keyboard.press('Enter');
  await expect(toggle(page)).toHaveAttribute('aria-expanded', 'true');
  await expect(panel(page)).toBeVisible();
  await expect(input(page)).toBeFocused();
  // Regresión (revisión exploratoria 047, F-01): con el panel abierto la barra superior se apretaba y el atajo de la búsqueda
  // se encimaba con los botones: los textos secundarios se ocultan.
  await expect(page.getByRole('button', { name: 'Búsqueda global' }).getByText(/Ctrl K|⌘K/)).toBeHidden();
  // Sugerencias: rellenan el compositor y no se envían solas.
  const before = received.length;
  const suggestion = panel(page).getByRole('list', { name: 'Sugerencias' }).getByRole('button').first();
  const text = (await suggestion.textContent())!;
  await suggestion.click();
  await expect(input(page)).toHaveValue(text);
  expect(received.length).toBe(before);
  await expectAccessible(page);

  // Sigue abierto al navegar y al recargar (por navegador); la persona no ve Studio.
  await page.getByRole('link', { name: 'Inicio' }).first().click();
  await expect(panel(page)).toBeVisible();
  await page.goto('/c/AcE2eNota');
  await expect(panel(page)).toBeVisible();
  await page.reload();
  await expect(panel(page)).toBeVisible();
  await expect(page.getByRole('link', { name: /ERP AI Studio/ })).toHaveCount(0);

  // Cerrar: el foco vuelve al botón.
  await panel(page).getByRole('button', { name: 'Cerrar el Acompañante' }).click();
  await expect(panel(page)).toHaveCount(0);
  await expect(toggle(page)).toBeFocused();
  await expect(page.getByRole('button', { name: 'Búsqueda global' }).getByText(/Ctrl K|⌘K/)).toBeVisible();
  await page.reload();
  await expect(panel(page)).toHaveCount(0);
});

test('H1 CA1: en móvil el panel flota sobre el contenido y Esc lo cierra', async ({ page }) => {
  await login(page); // en escritorio: uiLogin espera el menú, que en móvil está oculto
  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload();
  await toggle(page).click();
  await expect(panel(page)).toBeVisible();
  const box = (await panel(page).boundingBox())!;
  expect(box.x).toBeLessThanOrEqual(1);
  expect(box.width).toBeGreaterThanOrEqual(388);
  await page.keyboard.press('Escape');
  await expect(panel(page)).toHaveCount(0);
  await expect(toggle(page)).toBeFocused();
});

test('H2 CA1 · CA2 · H3 CA1: conversa como en Studio (pensando, consultas con nombres para personas, texto en vivo, varios turnos)', async ({ page }) => {
  await login(page);
  const answer = 'Tienes **una nota** en Borrador que puedes aprobar. ¿Quieres que te diga cuál?';
  scripts['pendientes'] = steps(
    { blocks: [tool('mis_pendientes')], delayMs: 1500 },
    { blocks: [say(answer)], chunkMs: 120 },
    { blocks: [say('Es la nota «Papelería».')] },
  );
  await newNota({ Nombre: 'Papelería', Monto: 10 });
  received.marca();
  await start(page, '[guion:pendientes] ¿Qué tengo pendiente?');
  // Pensando (el avatar y su texto) mientras el modelo no responde.
  await expect(panel(page).getByRole('heading', { name: /Pensando…|En cola…/ })).toBeVisible();
  await expect(log(page).getByText('¿Qué tengo pendiente?', { exact: false })).toBeVisible();
  // Texto en vivo: aparece por partes antes de terminar.
  await expect(log(page).getByText('Tienes', { exact: false })).toBeVisible();
  await idle(page);
  await expect(log(page).getByText('una nota', { exact: true })).toBeVisible();
  await expect(log(page).getByRole('button', { name: 'Revisó tus pendientes' })).toBeVisible();
  await expect(log(page).getByText('mis_pendientes')).toHaveCount(0);
  await expect(panel(page).getByRole('heading', { name: /Listo/ })).toBeVisible();
  expect((await received.siguiente((r) => r.tag === 'pendientes')).turn).toBe(0);

  // Multi-turno: el segundo mensaje sigue la misma conversación con su historial.
  received.marca();
  await followUp(page, '¿Cuál es?');
  await idle(page);
  await expect(log(page).getByText('Es la nota «Papelería».')).toBeVisible();
  await expect(log(page).getByRole('region', { name: 'Mensaje 2' })).toBeVisible();
  const second = await received.siguiente((r) => r.tag === 'pendientes');
  expect(second.turn).toBeGreaterThanOrEqual(2);
});

test('H2 CA1: «Detener respuesta» corta el turno y lo dice', async ({ page }) => {
  await login(page);
  scripts['detener'] = steps({ blocks: [], hold: true });
  await start(page, '[guion:detener] Explícame este catálogo');
  await expect(stopButton(page)).toBeVisible();
  await stopButton(page).click();
  for (const r of held.splice(0)) r.destroy();
  await expect(log(page).getByText('Detuviste la respuesta.')).toBeVisible({ timeout: 30_000 });
  await idle(page);
});

test('H6 CA1: manda el contexto de la pantalla y lo muestra', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Contexto', Monto: 5 });
  scripts['contexto'] = steps({ blocks: [say('Veo la nota que tienes abierta.')] });
  await page.goto(`/c/AcE2eNota/${id}`);
  await openPanel(page);
  await expect(panel(page).getByText(`Nota Acompañante · n.º ${id}`)).toBeVisible();
  received.marca();
  await start(page, '[guion:contexto] ¿Por qué no puedo avanzar este?');
  const first = await received.siguiente((r) => r.tag === 'contexto');
  expect(first.last).toContain('pantalla');
  expect(first.last).toContain('AcE2eNota');
  expect(first.last).toContain(String(id));
  await idle(page);
  // En otra pantalla el panel deja de nombrar el registro y no manda contexto.
  await page.getByRole('link', { name: 'Inicio' }).first().click();
  await expect(panel(page).getByText(`n.º ${id}`)).toHaveCount(0);
  received.marca();
  await followUp(page, '¿Y ahora?');
  const next = await received.siguiente((r) => r.tag === 'contexto');
  expect(next.last).not.toContain('<pantalla>');
  await idle(page);
});

test('H5 CA1 · H3 CA1: vista previa aparte de la explicación, como texto, y el avatar espera la confirmación', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Proveedor', Telefono: '555', Monto: 1 });
  scripts['vista'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '777' }, explicacion: HOSTIL })] },
    { blocks: [say('Te dejé la propuesta para que la revises.')] },
  );
  await start(page, '[guion:vista] Cambia el teléfono a 777');
  await idle(page);
  const c = card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`);
  await expect(c).toBeVisible();
  await expect(c.getByText('Pendiente de tu confirmación')).toBeVisible();
  const change = c.getByRole('definition').filter({ hasText: '777' });
  await expect(change).toContainText('555');
  await expect(c.getByRole('term').filter({ hasText: 'Teléfono' })).toBeVisible();
  // La explicación del modelo va aparte, marcada como suya y como texto (sin marcado).
  const says = c.getByRole('blockquote');
  await expect(says).toContainText('Según el Acompañante');
  await expect(says).toContainText(HOSTIL);
  expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
  await expect(panel(page).getByRole('heading', { name: /Esperando tu respuesta/ })).toBeVisible();
  // Nada cambió todavía.
  expect((await nota(id)).Telefono).toBe('555');
});

test('H5 CA2 · CA3: confirmar manda la huella y aplica una vez (doble clic); la tarjeta enlaza al registro', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Doble clic', Telefono: '100', Monto: 1 });
  scripts['confirmar'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '200' }, explicacion: 'Cambio el teléfono.' })] },
    { blocks: [say('Listo para que confirmes.')] },
  );
  await page.goto(`/c/AcE2eNota/${id}`);
  await expect(page.getByLabel('Teléfono')).toHaveValue('100');
  await start(page, '[guion:confirmar] Cambia el teléfono a 200');
  await idle(page);
  const c = card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`);
  const confirms: number[] = [];
  page.on('response', (r) => { if (/\/api\/companion\/actions\/\d+\/confirm$/.test(r.url())) confirms.push(r.status()); });
  const hashSent = page.waitForRequest((r) => /\/confirm$/.test(r.url()));
  await c.getByRole('button', { name: 'Sí, adelante' }).dblclick();
  const body = (await hashSent).postDataJSON() as { hash: string };
  expect(body.hash).toMatch(/^[0-9a-f]{64}$/);
  await expect(c.getByText('Aplicada', { exact: true })).toBeVisible();
  await expect(c.getByRole('button', { name: 'Sí, adelante' })).toHaveCount(0);
  await expect(c).toBeFocused();
  expect(confirms).toEqual([200]);
  expect((await nota(id)).Telefono).toBe('200');
  // Spec 048 (H1 CA4): el turno sigue solo y el modelo recibe «aplicada» como resultado de su propuesta.
  await expect(log(page).getByText('Listo para que confirmes.')).toBeVisible();
  const resumed = received.todos().find((r) => r.tag === 'confirmar' && r.turn === 1);
  expect(resumed?.last).toContain('aplicada');
  // Regresión (revisión exploratoria 047, F-02): con ese registro abierto, «Ver el registro» no hacía nada y la pantalla
  // seguía con el teléfono de antes.
  await expect(page.getByLabel('Teléfono')).toHaveValue('100');
  await c.getByRole('link', { name: 'Ver el registro' }).click();
  await expect(page).toHaveURL(new RegExp(`/c/AcE2eNota/${id}$`));
  await expect(page.getByLabel('Teléfono')).toHaveValue('200');
  await expect(card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`).getByText('Aplicada', { exact: true })).toBeVisible();
});

test('H1 CA2 · H6 CA1 (048): «No, y dile qué cambiar» le llega al Acompañante y el turno sigue', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Motivo', Telefono: '1', Monto: 1 });
  scripts['motivo'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '2' }, explicacion: 'x' })] },
    { blocks: [say('Entendido, no lo cambio.')] },
  );
  await start(page, '[guion:motivo] Cambia el teléfono');
  await idle(page);
  const c = card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`);
  await expect(c.getByText('¿Lo hago?')).toBeVisible();
  received.marca();
  await c.getByRole('button', { name: 'No, y dile qué cambiar' }).click();
  const why = c.getByRole('textbox', { name: '¿Qué debe cambiar?' });
  await expect(why).toBeFocused();
  await why.fill('Mejor ponle el 3');
  const res = await tras(page.waitForResponse((r) => /\/discard$/.test(r.url())), () => why.press('Enter'));
  expect(res.request().postDataJSON()).toEqual({ reason: 'Mejor ponle el 3' });
  await expect(c.getByText('Descartada', { exact: true })).toBeVisible();
  await expect(c.getByText('Le dijiste: «Mejor ponle el 3»')).toBeVisible();
  await expect(log(page).getByText('Entendido, no lo cambio.')).toBeVisible();
  expect((await received.siguiente((r) => r.tag === 'motivo')).last).toContain('Mejor ponle el 3');
  expect((await nota(id)).Telefono).toBe('1');
});

test('H1 CA3 · H6 CA2 · CA3 (048): esperando, el avatar lo dice y escribir es decir que no con ese texto', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Escrito', Telefono: '1', Monto: 1 });
  scripts['escrito'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '2' }, explicacion: 'x' })] },
    { blocks: [say('Va, primero te explico.')] },
  );
  await start(page, '[guion:escrito] Cambia el teléfono');
  await idle(page);
  await expect(panel(page).getByRole('heading', { name: /Esperando tu respuesta/ })).toBeVisible();
  await expect(panel(page).getByText('Lo que escribas cuenta como «no» y se lo dice.')).toBeVisible();
  received.marca();
  await followUp(page, 'Espera, ¿qué hace este catálogo?');
  await expect(log(page).getByText('Espera, ¿qué hace este catálogo?')).toBeVisible();
  await expect(log(page).getByText('Va, primero te explico.')).toBeVisible();
  const c = card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`);
  await expect(c.getByText('Descartada', { exact: true })).toBeVisible();
  expect((await received.siguiente((r) => r.tag === 'escrito')).last).toContain('rechazada');
  expect((await nota(id)).Telefono).toBe('1');
});

test('H5 CA2: los avisos piden confirmar otra vez (428) y la transición se aplica', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Grande', Monto: 5000 });
  scripts['avisos'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'transicion', catalogo: 'AcE2eNota', id, transicion: 'aprobar', comentario: 'Revisada', explicacion: 'La apruebo.' })] },
    { blocks: [say('Propuesta lista.')] },
  );
  await start(page, '[guion:avisos] Aprueba la nota grande');
  await idle(page);
  const c = card(page, `Propuesta: Aprobar: Nota Acompañante n.º ${id}`);
  await expect(c.getByRole('definition').filter({ hasText: 'Aprobada' })).toContainText('Borrador');
  await expect(c.getByText('Revisada')).toBeVisible();
  const first = await tras(page.waitForResponse((r) => /\/confirm$/.test(r.url())), () => c.getByRole('button', { name: 'Sí, adelante' }).click());
  expect(first.status()).toBe(428);
  await expect(c.getByRole('alert')).toContainText(AVISO);
  expect((await nota(id))._workflow).toMatchObject({ state: 'Borrador' });
  const second = await tras(page.waitForResponse((r) => /\/confirm$/.test(r.url())), () => c.getByRole('button', { name: 'Sí, con los avisos' }).click());
  expect(second.status()).toBe(200);
  expect(((second.request().postDataJSON()) as { acceptWarnings: string[] }).acceptWarnings).toEqual([AVISO]);
  await expect(c.getByText('Aplicada', { exact: true })).toBeVisible();
  expect((await nota(id))._workflow).toMatchObject({ state: 'Aprobada' });
});

test('H5 CA2 · CA3: un rechazo se muestra en la tarjeta y descartar la cierra', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Cambiada', Telefono: '1', Monto: 1 });
  scripts['rechazo'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '2' }, explicacion: 'x' }),
      tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Nombre: 'Otro nombre' }, explicacion: 'y' })] },
    { blocks: [say('Dos propuestas.')] },
  );
  await start(page, '[guion:rechazo] Dos cambios');
  await idle(page);
  const cards = log(page).getByRole('article', { name: `Propuesta: Editar Nota Acompañante n.º ${id}` });
  await expect(cards).toHaveCount(2);
  // Otra persona (aquí, la API) cambia el registro: la versión de la vista previa ya no es la vigente.
  const cur = await nota(id);
  const put = await ana.put(`/api/data/AcE2eNota/${id}`, { data: { Nombre: 'Cambiada', Telefono: '9', Monto: 1, _rowVersion: cur._rowVersion } });
  expect(put.status(), await put.text()).toBe(200);
  const first = cards.nth(0);
  // Spec 048 (H6 CA1): una por una; la segunda espera su turno.
  await expect(first.getByText('Propuesta 1 de 2')).toBeVisible();
  // Revisión exploratoria 048 (F-01): «Propuesta 1 de 2» en la cabecera aplastaba el título a una columna.
  expect((await first.locator('.cmp-action-title').boundingBox())!.width).toBeGreaterThan(100);
  await expect(cards.nth(1).getByText('En espera: te la pregunto cuando decidas la anterior.')).toBeVisible();
  await expect(cards.nth(1).getByRole('button', { name: 'Sí, adelante' })).toHaveCount(0);
  const res = await tras(page.waitForResponse((r) => /\/confirm$/.test(r.url())), () => first.getByRole('button', { name: 'Sí, adelante' }).click());
  expect(res.status()).toBe(409);
  await expect(first.getByRole('alert')).toBeVisible();
  await expect(first.getByText('No se aplicó', { exact: true })).toBeVisible();
  // Regresión (revisión adversarial 047, F-03): los botones desaparecen y el foco queda en la tarjeta, no fuera del panel.
  await expect(first).toBeFocused();
  expect((await nota(id)).Telefono).toBe('9');
  // Descartar la otra.
  const second = cards.nth(1);
  await second.getByRole('button', { name: 'No', exact: true }).click();
  await expect(second.getByText('Descartada', { exact: true })).toBeVisible();
  await expect(second.getByRole('button', { name: 'Sí, adelante' })).toHaveCount(0);
  expect((await nota(id)).Nombre).toBe('Cambiada');
});

test('Regresión (revisión adversarial 047, F-01 · F-02): sin respuesta al confirmar, la tarjeta lo dice y la conversación sigue a la vista', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Sin red', Telefono: '10', Monto: 1 });
  scripts['sinred'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '20' }, explicacion: 'x' })] },
    { blocks: [say('Propuesta lista.')] },
  );
  await start(page, '[guion:sinred] Cambia el teléfono a 20');
  await idle(page);
  const c = card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`);
  // F-01: la respuesta de confirmar no llega (antes decía «No se pudo conectar con el servidor», sin pedir revisar el estado).
  // F-02: la relectura del estado del panel falla: antes reemplazaba la conversación por «No se pudo cargar el Acompañante».
  await page.route(/\/api\/companion\/actions\/\d+\/confirm$/, (route) => route.abort());
  await page.route(/\/api\/companion$/, (route) => route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'No disponible', status: 503 }) }));
  await c.getByRole('button', { name: 'Sí, adelante' }).click();
  await expect(c.getByRole('alert')).toContainText('no sabemos si se aplicó');
  await expect(log(page)).toBeVisible();
  await expect(panel(page).getByText('No se pudo cargar el Acompañante')).toHaveCount(0);
  expect((await nota(id)).Telefono).toBe('10');
  await page.unroute(/\/api\/companion\/actions\/\d+\/confirm$/);
  await page.unroute(/\/api\/companion$/);
  await c.getByRole('button', { name: 'Sí, adelante' }).click();
  await expect(c.getByText('Aplicada', { exact: true })).toBeVisible();
  expect((await nota(id)).Telefono).toBe('20');
});

test('H5 CA3: aplicándose, caducó y «sin confirmar si se aplicó» se dicen en la tarjeta', async ({ page }) => {
  await login(page);
  const id = await newNota({ Nombre: 'Estados', Monto: 1 });
  scripts['estados'] = steps(
    { blocks: [tool('proponer_accion', { tipo: 'editar', catalogo: 'AcE2eNota', id, valores: { Telefono: '3' }, explicacion: 'x' })] },
    { blocks: [say('Hecho.')] },
  );
  await start(page, '[guion:estados] Un cambio');
  await idle(page);
  // Estados que el servidor alcanza por el barrido o por una caída (046): se simulan en la lectura de la conversación.
  for (const [status, label, extra] of [
    ['applying', 'Aplicándose…', null],
    ['expired', 'Caducó', /caducó sin confirmarse/],
    ['unknown', 'Sin confirmar si se aplicó', /Revisa el registro antes de repetirla/],
  ] as const) {
    await page.route(/\/api\/companion\/conversations\/\d+(\?.*)?$/, async (route) => {
      const r = await route.fetch();
      const json = await r.json() as { actions: Record<string, unknown>[] };
      json.actions = json.actions.map((a) => ({ ...a, status, preview: null, decidedAt: '2026-11-01T10:00:00Z',
        error: status === 'unknown' ? 'No sabemos si se aplicó: el servidor se detuvo mientras la ejecutaba. Revisa el registro antes de repetirla.' : null }));
      await route.fulfill({ response: r, json });
    });
    await page.reload();
    const c = card(page, `Propuesta: Editar Nota Acompañante n.º ${id}`);
    await expect(c.getByText(label, { exact: true })).toBeVisible();
    await expect(c.getByRole('button', { name: 'Sí, adelante' })).toHaveCount(0);
    if (extra) await expect(c.getByText(extra)).toBeVisible();
    if (status === 'unknown') await expect(c.getByRole('link', { name: 'Revisar el registro' })).toBeVisible();
    await page.unroute(/\/api\/companion\/conversations\/\d+(\?.*)?$/);
  }
});

test('H4 CA1 · CA2: retoma una conversación anterior y borra las propias (con confirmación; 409 si trabaja)', async ({ page }) => {
  await login(page);
  scripts['vieja'] = steps({ blocks: [say('Respuesta de la conversación vieja.')] });
  scripts['nueva'] = steps({ blocks: [say('Respuesta de la nueva.')] });
  await start(page, '[guion:vieja] Primera pregunta');
  await idle(page);
  await start(page, '[guion:nueva] Segunda pregunta');
  await idle(page);
  await panel(page).getByRole('button', { name: 'Conversaciones' }).click();
  const history = panel(page).getByRole('navigation', { name: 'Conversaciones con el Acompañante' });
  await expect(history).toBeVisible();
  await history.getByRole('button', { name: /^\[guion:vieja\]/ }).click();
  await expect(log(page).getByText('Respuesta de la conversación vieja.')).toBeVisible();

  // Borrar pide confirmación y la quita de la lista.
  await panel(page).getByRole('button', { name: 'Conversaciones' }).click();
  await history.getByRole('button', { name: /Borrar «\[guion:vieja\] Primera pregunta»/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Borrar la conversación' });
  await expect(dialog).toContainText('Primera pregunta');
  const del = await tras(page.waitForResponse((r) => r.request().method() === 'DELETE'), () => dialog.getByRole('button', { name: 'Borrar' }).click());
  expect(del.status()).toBe(204);
  await expect(history.getByRole('button', { name: /^\[guion:vieja\]/ })).toHaveCount(0);
  await expect(history.getByRole('button', { name: /^\[guion:nueva\]/ })).toBeVisible();

  // Con un turno en curso, el servidor responde 409 y el panel lo dice.
  scripts['ocupada'] = steps({ blocks: [], hold: true });
  await start(page, '[guion:ocupada] Esta se queda trabajando');
  await panel(page).getByRole('button', { name: 'Conversaciones' }).click();
  await history.getByRole('button', { name: /Borrar «\[guion:ocupada\]/ }).click();
  const busy = await tras(page.waitForResponse((r) => r.request().method() === 'DELETE'), () => dialog.getByRole('button', { name: 'Borrar' }).click());
  expect(busy.status()).toBe(409);
  await expect(history.getByRole('alert')).toContainText('respuesta en curso');
  await expect(history.getByRole('button', { name: /^\[guion:ocupada\]/ })).toBeVisible();
  await stopActive();
});

// ---------------- Capturas (claro, oscuro y móvil) ----------------
test.describe('capturas del panel', () => {
  // El avatar se queda quieto con movimiento reducido (H3 CA2): la captura no depende de un parpadeo.
  test.use({ reducedMotion: 'reduce' });

  async function proposal(page: Page) {
    scripts['captura'] = steps(
      { blocks: [tool('mis_catalogos'), tool('proponer_accion', { tipo: 'crear', catalogo: 'AcE2eNota', valores: { Nombre: 'Material de oficina', Telefono: '55 1234 5678', Monto: 250 }, explicacion: 'Creo la nota con los datos que me diste.' })] },
      { blocks: [say('Te dejé la propuesta: **revísala y confírmala** si está bien.')] },
    );
    await start(page, '[guion:captura] Crea una nota de material de oficina');
    await idle(page);
    await expect(card(page, 'Propuesta: Crear Nota Acompañante')).toBeVisible();
  }
  const shot = (p: Locator) => ({ mask: [p.locator('time')], animations: 'disabled' as const });

  for (const scheme of ['light', 'dark'] as const) {
    test(`panel con una propuesta (${scheme})`, async ({ page }) => {
      // Como visual.spec.ts: el tema elegido, sin depender de una preferencia guardada.
      await page.emulateMedia({ colorScheme: scheme });
      await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
      await login(page);
      await proposal(page);
      await expect(panel(page)).toHaveScreenshot(`acompanante-${scheme}.png`, shot(panel(page)));
      await expectAccessible(page);
      await stopActive();
    });
  }

  test('panel en móvil', async ({ page }) => {
    await login(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.reload();
    await toggle(page).click();
    await expect(panel(page).getByRole('heading', { name: 'Hola, Ana' })).toBeVisible();
    await expect(page).toHaveScreenshot('acompanante-movil.png', shot(panel(page)));
  });
});
