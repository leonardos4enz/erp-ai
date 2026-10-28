// Spec 035 · ERP AI Studio: pantalla. Un proveedor simulado (127.0.0.1, PORTS.asistente: Assistant__BaseUrl de la suite)
// responde como la API de mensajes de Anthropic con `tool_use` según un guion por pedido (`[guion:…]`): el motor de la 033
// corre de verdad (herramientas, copia aislada, entrega) y la suite nunca llama a Anthropic. La clave es ficticia.
import { createServer, type Server, type ServerResponse } from 'node:http';
import type { APIRequestContext, Browser, Locator, Page } from '@playwright/test';
import { ADMIN, PORTS, apiAs, applyPlan, expect, expectAccessible, expectToast, receptor, roleId, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas Studio'; // grupo propio del menú
const PWD = 'frase larga de studio 2026';
const API_KEY = 'sk-ant-e2e-studio-clave-ficticia-0001';
const HOSTIL = '<img src=x onerror="window.__xss=1"> [Haz clic aquí](https://phishing.example/login) https://phishing.example/otra';

// ---------------- Proveedor simulado ----------------
type Block = { type: 'text'; text: string } | { type: 'tool_use'; id: string; name: string; input: unknown };
/** Un turno del «modelo»: bloques a responder, retraso, o `hold` (no responde hasta que el ERP corte la conexión). */
type Turn = { blocks: Block[]; delayMs?: number; hold?: boolean };
let ids = 0;
const say = (text: string): Block => ({ type: 'text', text });
const tool = (name: string, input: object = {}): Block => ({ type: 'tool_use', id: `toolu_e2e${++ids}`, name, input });
const deliver = (input: object): Turn => ({ blocks: [tool('entregar', input)] });
/** Guion por turnos; después del último, entrega sin plan. */
const steps = (...turns: Turn[]) => (i: number): Turn => turns[i] ?? deliver({ summary: 'Fin del guion.' });
const scripts: Record<string, (turn: number) => Turn> = {};
const received = receptor<{ tag: string; turn: number }>();
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
      const body = JSON.parse(raw) as { messages: { role: string; content: unknown }[] };
      const tag = /\[guion:([a-z0-9-]+)\]/.exec(JSON.stringify(body.messages[0]?.content))?.[1] ?? '';
      const turn = body.messages.filter((m) => m.role === 'assistant').length;
      received.push({ tag, turn });
      const next = scripts[tag]?.(turn) ?? { blocks: [say('Sin guion.')] };
      if (next.hold) { held.push(res); return; }
      const reply = JSON.stringify({
        id: `msg_e2e_${received.length}`, type: 'message', role: 'assistant', model: 'claude-sonnet-5-5', content: next.blocks,
        stop_reason: next.blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn', stop_sequence: null,
        usage: { input_tokens: 1500, output_tokens: 250 },
      });
      setTimeout(() => res.end(reply), next.delayMs ?? 0);
    });
  });
  await new Promise<void>((r) => server.listen(PORTS.asistente, '127.0.0.1', r));
});

// ---------------- Sesiones de API ----------------
const apis: Record<string, APIRequestContext> = {};
const as = async (who: string) => (apis[who] ??= who === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(who, PWD));

test.afterAll(async () => {
  // Deja el entorno como lo encontró, también si se cortó a mitad: sin sesión activa, sin clave y sin el catálogo de prueba.
  const admin = await as('admin');
  const status = await (await admin.get('/api/studio')).json().catch(() => null);
  if (status?.active) await admin.post(`/api/studio/sessions/${status.active}/cancel`);
  await admin.delete('/api/assistant/key');
  const model = await (await admin.get('/api/model/entities')).json() as { name: string }[];
  const mine = ['StE2eTarea', 'StE2eHostil'].filter((n) => model.some((e) => e.name === n));
  if (mine.length) await applyPlan(admin, { description: 'E2E 035: limpieza', operations: mine.map((entity) => ({ op: 'archiveEntity', entity })) });
  for (const r of held.splice(0)) r.destroy();
  server.closeAllConnections();
  await new Promise((r) => server.close(r));
});

async function pageAs(browser: Browser, user: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined, viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

const sessionRegion = (page: Page) => page.getByRole('region', { name: 'Sesión' });
const pedido = (page: Page) => sessionRegion(page).getByRole('article', { name: 'Pedido' });
const pasos = (page: Page) => sessionRegion(page).getByRole('region', { name: /Pasos del agente/ });
const stepRow = (page: Page, name: string) => pasos(page).getByRole('listitem').filter({ has: page.locator('.studio-step-name', { hasText: new RegExp(`^${name}$`) }) });
const sessionId = (page: Page) => Number(new URL(page.url()).searchParams.get('s'));

/** Pide un desarrollo desde la pantalla (con los alcances que ya tenga el formulario) y espera a que la sesión se abra. */
async function ask(page: Page, prompt: string, opts: { aislado?: boolean } = {}) {
  await page.goto('/admin/studio');
  await page.getByLabel('¿Qué desarrollo necesitas?').fill(prompt);
  const sandbox = page.getByRole('switch', { name: 'Probar en una copia aislada' });
  if (opts.aislado === false) await sandbox.uncheck();
  await tras(page.waitForResponse((r) => r.url().endsWith('/api/studio/sessions') && r.request().method() === 'POST'),
    () => page.getByRole('button', { name: 'Pedir a Studio' }).click());
  await expect(pedido(page)).toBeVisible();
  return sessionId(page);
}

const waitStatus = (page: Page, label: string) => expect(pedido(page).getByText(label, { exact: true })).toBeVisible({ timeout: 60_000 });

const plan = (operations: object[], description = 'Peso en tareas') => ({ description, operations });
const PLAN_PESO = plan([
  { op: 'addField', entity: 'StE2eTarea', field: { name: 'Peso', label: 'Peso (kg)', type: 'decimal', precision: 18, scale: 2 } },
  { op: 'setRules', entity: 'StE2eTarea', rules: [{ name: 'PesoPositivo', field: 'Peso', message: 'El peso no puede ser negativo.', assert: 'Peso >= 0' }] },
  { op: 'archiveField', entity: 'StE2eTarea', field: 'Notas' },
]);

test('preparar: catálogos, un modelador y un lector', async () => {
  const admin = await as('admin');
  await applyPlan(admin, { description: 'E2E 035: catálogos', operations: [
    { op: 'createEntity', name: 'StE2eTarea', label: 'Tarea Studio', labelPlural: 'Tareas Studio', group: GROUP, icon: 'list-checks', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true }, { name: 'Monto', label: 'Monto', type: 'decimal', precision: 18, scale: 2 },
      { name: 'Notas', label: 'Notas', type: 'text' }] },
    { op: 'createEntity', name: 'StE2eHostil', label: 'Hostil Studio', labelPlural: 'Hostiles Studio', group: GROUP, icon: 'skull',
      description: HOSTIL, fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
  ] });
  const res = await admin.post('/api/admin/roles', { data: { name: 'ModeladorStudio', description: 'E2E spec 035', canManageModel: true, canManageUsers: false,
    canViewAudit: false, permissions: [{ entity: 'StE2eTarea', canRead: true, canCreate: true, canUpdate: true, canDelete: false }] } });
  expect(res.status(), await res.text()).toBe(200);
  const modelador = (await res.json()).id;
  for (const [username, roles] of [['e2e-st-modelador', [modelador]], ['e2e-st-lector', [await roleId(admin, 'Usuario')]]] as const) {
    const u = await admin.post('/api/admin/users', { data: { username, displayName: username, password: PWD, roleIds: roles } });
    expect(u.status(), await u.text()).toBe(201);
  }
});

test('H1 CA3: sin clave de Anthropic la pantalla lo explica y no ofrece pedir', async ({ page }) => {
  const admin = await as('admin');
  await admin.delete('/api/assistant/key');
  await page.goto('/admin/studio');
  await expect(page.getByText('ERP AI Studio no está configurado')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Configuración › Asistente IA' })).toBeVisible();
  await expect(page.getByLabel('¿Qué desarrollo necesitas?')).toHaveCount(0);
  // Con la clave (probada contra el proveedor simulado), la pantalla ofrece pedir.
  const key = await admin.put('/api/assistant/key', { data: { apiKey: API_KEY } });
  expect(key.status(), await key.text()).toBe(200);
  await page.reload();
  await expect(page.getByLabel('¿Qué desarrollo necesitas?')).toBeVisible();
});

test('H1 CA1: solo un modelador ve ERP AI Studio en Configuración', async ({ browser }) => {
  const modelador = await pageAs(browser, 'e2e-st-modelador');
  await modelador.goto('/admin');
  await modelador.getByRole('main').getByRole('link', { name: /ERP AI Studio/ }).click();
  await expect(modelador.getByRole('heading', { name: 'ERP AI Studio', level: 1 })).toBeVisible();
  await expect(modelador.getByLabel('¿Qué desarrollo necesitas?')).toBeVisible();
  await modelador.context().close();

  const lector = await pageAs(browser, 'e2e-st-lector');
  await expect(lector.getByRole('link', { name: /ERP AI Studio/ })).toHaveCount(0);
  await lector.goto('/admin/studio');
  await expect(lector.getByText('Sin acceso')).toBeVisible();
  await expect(lector.getByLabel('¿Qué desarrollo necesitas?')).toHaveCount(0);
  await lector.context().close();
});

test('H1 CA2: datos apagado con aviso y aislado según el servidor', async ({ page }) => {
  await page.goto('/admin/studio');
  const datos = page.getByRole('switch', { name: 'Leer registros (datos)' });
  await expect(datos).not.toBeChecked();
  await expect(page.getByRole('switch', { name: 'Modelo del ERP' })).toBeChecked();
  await expect(page.getByRole('switch', { name: 'Modelo del ERP' })).toBeDisabled();
  await expect(page.getByText('Los registros que el agente lea viajan a Anthropic')).toHaveCount(0);
  await datos.check();
  await expect(page.getByText('Los registros que el agente lea viajan a Anthropic')).toBeVisible();
  await expect(page.getByRole('switch', { name: 'Probar en una copia aislada' })).toBeChecked();
  await expectAccessible(page);

  // Un servidor sin copia aislada (Studio:Sandbox=false o sin permiso CREATE DATABASE): la opción se ve, apagada y con su motivo.
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

const VIVO = '[guion:vivo] Agrega el peso a las tareas con una regla que no permita pesos negativos, y pruébala.';
let vivoId = 0;

test('H2 CA1 CA2: el avance llega en vivo con pasos expandibles y consumo contra los topes', async ({ page }) => {
  scripts.vivo = steps(
    { blocks: [say('Voy a revisar el catálogo de tareas.'), tool('listar_catalogos')], delayMs: 1500 },
    { blocks: [tool('ver_catalogo', { entity: 'StE2eTarea' })], delayMs: 1500 },
    { blocks: [tool('compilar_plan', { plan: PLAN_PESO })], delayMs: 1500 },
    { blocks: [tool('aislado_aplicar_plan', { plan: PLAN_PESO }), tool('aislado_crear', { entity: 'StE2eTarea', values: { Nombre: 'Negativa', Peso: -1 } }),
      tool('aislado_crear', { entity: 'StE2eTarea', values: { Nombre: 'Positiva', Peso: 2.5 } })], delayMs: 1000 },
    deliver({
      summary: 'Agregué «Peso (kg)» a Tareas Studio con una regla que rechaza pesos negativos. Archivo «Notas», que no se usa.',
      plan: PLAN_PESO,
      tested: [{ description: 'Un peso negativo se rechaza con el mensaje de la regla', ok: true, detail: '400: El peso no puede ser negativo.' },
        { description: 'Un peso positivo se guarda', ok: true }],
      failures: ['No probé la importación masiva con pesos.'],
      risks: ['Las tareas existentes quedan sin peso hasta que alguien lo capture.'],
    }),
  );
  const polls: string[] = [];
  page.on('request', (r) => { if (/\/api\/studio\/sessions\/\d+\?after=\d+$/.test(r.url())) polls.push(r.url()); });
  vivoId = await ask(page, VIVO);

  // Mientras trabaja, los pasos llegan solos.
  await expect(stepRow(page, 'listar_catalogos')).toBeVisible({ timeout: 30_000 });
  await expect(pedido(page).getByText('Trabajando', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Cancelar', exact: true })).toBeVisible();
  // Revisión adversarial 035 (F-05): mientras sondea, ningún paso se repite (antes de la lectura completa final).
  await expect(stepRow(page, 'ver_catalogo')).toBeVisible({ timeout: 30_000 });
  await expect(stepRow(page, 'listar_catalogos')).toHaveCount(1);
  await expect(pedido(page).getByText('Trabajando', { exact: true })).toBeVisible();
  // Regresión (revisión exploratoria 035, F-09): el historial se quedaba «En cola» mientras la sesión trabajaba.
  await expect(page.getByRole('navigation', { name: 'Sesiones' }).getByRole('button', { name: /Agrega el peso a las tareas/ })).toContainText('Trabajando');
  await waitStatus(page, 'Propuesta lista');
  expect(polls.length, 'la pantalla sondea con after').toBeGreaterThan(0);
  await expect(page.getByRole('button', { name: 'Cancelar', exact: true })).toHaveCount(0);

  // Pasos: herramienta, estado y duración; al expandir, entrada y salida.
  for (const name of ['listar_catalogos', 'ver_catalogo', 'compilar_plan', 'aislado_preparar', 'aislado_aplicar_plan']) await expect(stepRow(page, name)).toBeVisible();
  const crear = stepRow(page, 'aislado_crear');
  await expect(crear).toHaveCount(2);
  await expect(crear.first().getByText('Error', { exact: true })).toBeVisible();
  await expect(crear.last().getByText('Correcto', { exact: true })).toBeVisible();
  const ver = stepRow(page, 'ver_catalogo');
  await ver.locator('summary').click();
  await expect(ver.getByLabel(/Entrada del paso/)).toContainText('StE2eTarea');
  await expect(ver.getByLabel(/Salida del paso/)).toContainText('Tarea Studio');
  await expect(crear.first().locator('.studio-duration')).toHaveText(/\d+ (ms|s)$/);

  // Consumo contra los topes de la sesión (Studio:* por omisión).
  const pasosMeter = page.getByRole('meter', { name: 'Pasos' });
  await expect(pasosMeter).toHaveAttribute('aria-valuemax', '30');
  await expect(pasosMeter).toHaveAttribute('aria-valuenow', '5');
  await expect(page.getByRole('meter', { name: 'Tokens' })).toHaveAttribute('aria-valuetext', /8,750 de 1,500,000/);
  await expect(page.getByRole('meter', { name: 'Costo estimado' })).toHaveAttribute('aria-valuetext', /0\.0275 USD de 5\.00 USD/);
  for (const name of ['Herramientas', 'Minutos']) await expect(page.getByRole('meter', { name })).toBeVisible();

  // H3 CA1: la propuesta, con lo que el agente informa como afirmación suya y la vista previa del Modelador.
  const propuesta = sessionRegion(page).getByRole('article', { name: 'Propuesta' });
  await expect(propuesta.getByText(/Agregué «Peso \(kg\)»/)).toBeVisible();
  await expect(propuesta.getByText('Es una afirmación del agente: compárala con los pasos de arriba.')).toBeVisible();
  await expect(propuesta.getByRole('listitem').filter({ hasText: 'Un peso negativo se rechaza' })).toContainText('Superada');
  await expect(propuesta.getByText('No probé la importación masiva con pesos.')).toBeVisible();
  await expect(propuesta.getByText('Las tareas existentes quedan sin peso hasta que alguien lo capture.')).toBeVisible();
  await expect(propuesta.getByText(/no ejecutó ninguna herramienta en la copia aislada/)).toHaveCount(0);
  await expect(propuesta.getByText('Archiva el campo «Notas» de «Tarea Studio» (sus datos se conservan).')).toBeVisible();
  await expect(propuesta.getByText('El plan es válido')).toBeVisible();
  await expect(propuesta.getByRole('button', { name: 'Revisar y aplicar…' })).toBeEnabled();
  await expectAccessible(page);
});

// El contenido se desplaza dentro de `main` (no el documento): la vista es alta para que la captura tenga la propuesta entera.
for (const [name, scheme, viewport] of [['light', 'light', { width: 1280, height: 2300 }], ['dark', 'dark', { width: 1280, height: 2300 }], ['movil', 'light', { width: 390, height: 4200 }]] as const) {
  test(`visual: la propuesta en ${name === 'movil' ? 'móvil' : name === 'light' ? 'claro' : 'oscuro'}`, async ({ browser }) => {
    test.skip(!vivoId, 'depende de la sesión «vivo»');
    const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json', viewport, colorScheme: scheme });
    const page = await ctx.newPage();
    await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    await page.goto(`/admin/studio?s=${vivoId}`);
    await expect(sessionRegion(page).getByRole('article', { name: 'Propuesta' })).toBeVisible();
    await page.mouse.move(0, 0);
    await page.evaluate(() => document.fonts.ready);
    // Lo que depende del momento: fechas, duraciones, minutos y el tiempo de preparar la copia.
    const mask: Locator[] = [page.locator('.studio-date'), page.locator('.studio-duration'), page.locator('.studio-meter-minutes'),
      stepRow(page, 'aislado_preparar').locator('.studio-step-summary')];
    await expect(page.getByRole('main')).toHaveScreenshot(`studio-propuesta-${name}.png`, { mask });
    if (name === 'movil') {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    }
    await ctx.close();
  });
}

test('H3 CA1 CA2: la propuesta se revisa y se aplica con la huella; si el modelo cambió, se recarga', async ({ page }) => {
  const admin = await as('admin');
  scripts.aplicar = steps(deliver({
    summary: 'Agrego la referencia a las tareas.',
    plan: plan([{ op: 'addField', entity: 'StE2eTarea', field: { name: 'Referencia', label: 'Referencia', type: 'text' } }], 'Referencia en tareas'),
    tested: [{ description: 'La referencia se guarda', ok: true }], failures: [], risks: [],
  }));
  await ask(page, '[guion:aplicar] Agrega una referencia a las tareas.', { aislado: false });
  await waitStatus(page, 'Propuesta lista');
  const propuesta = sessionRegion(page).getByRole('article', { name: 'Propuesta' });
  // R-033-11: dice haber probado sin correr nada en la copia: la pantalla lo advierte.
  await expect(propuesta.getByText(/no ejecutó ninguna herramienta en la copia aislada/)).toBeVisible();

  // Otra persona cambia el catálogo después de la revisión: la huella ya no vale.
  await applyPlan(admin, plan([{ op: 'updateField', entity: 'StE2eTarea', field: 'Monto', label: 'Monto total' }], 'E2E 035: cambio ajeno'));
  await propuesta.getByRole('button', { name: 'Revisar y aplicar…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Aplicar la propuesta de ERP AI Studio' });
  await expect(dialog.getByText(/Se aplicarán 1 operaciones/)).toBeVisible();
  await expectToast(page, /El modelo cambió/, () => dialog.getByRole('button', { name: 'Aplicar cambios' }).click());
  await expect(propuesta.getByText('La propuesta se actualizó')).toBeVisible();
  await expect(pedido(page).getByText('Propuesta lista', { exact: true })).toBeVisible();

  // Revisada de nuevo, se aplica una vez.
  await propuesta.getByRole('button', { name: 'Revisar y aplicar…' }).click();
  await expectToast(page, 'Cambios aplicados', () => dialog.getByRole('button', { name: 'Aplicar cambios' }).click());
  await expect(pedido(page).getByText('Aplicada', { exact: true })).toBeVisible();
  await expect(pedido(page).getByText(/Conjunto de cambios #\d+/)).toBeVisible();
  await expect(propuesta.getByRole('button', { name: 'Revisar y aplicar…' })).toHaveCount(0);
  const fields = (await (await admin.get('/api/model/entities/StE2eTarea')).json()).fields as { name: string }[];
  expect(fields.map((f) => f.name)).toContain('Referencia');
});

let aprobacionId = 0;

test('H3 CA3: en un entorno con aprobación no se aplica y se descarga el plan', async ({ page }) => {
  scripts.aprobacion = steps(deliver({
    summary: 'Agrego un folio a las tareas.',
    plan: plan([{ op: 'addField', entity: 'StE2eTarea', field: { name: 'Folio', label: 'Folio', type: 'text' } }], 'Folio en tareas'),
  }));
  aprobacionId = await ask(page, '[guion:aprobacion] Agrega un folio a las tareas.', { aislado: false });
  await waitStatus(page, 'Propuesta lista');
  // La suite permite cambios directos; la respuesta del servidor con aprobación (StudioTests › H4_CA2_sin_cambios_directos_no_se_aplica)
  // se reproduce en la lectura completa de la sesión.
  const reason = 'Este entorno exige la aprobación de otro administrador: la propuesta no se aplica aquí. Los cambios al modelo llegan como paquete desde Configuración › Entornos.';
  await page.route(/\/api\/studio\/sessions\/\d+$/, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), canApply: false, blockedReason: reason } });
  });
  await page.reload();
  const propuesta = sessionRegion(page).getByRole('article', { name: 'Propuesta' });
  await expect(propuesta.getByText('No se aplica en este entorno')).toBeVisible();
  await expect(propuesta.getByText(/exige la aprobación de otro administrador/)).toBeVisible();
  await expect(propuesta.getByRole('button', { name: 'Revisar y aplicar…' })).toHaveCount(0);
  const download = await tras(page.waitForEvent('download'), () => propuesta.getByRole('button', { name: 'Descargar plan' }).click());
  expect(download.suggestedFilename()).toBe(`erp-ai-studio-sesion-${aprobacionId}.json`);
  await page.unroute(/\/api\/studio\/sessions\/\d+$/);
});

test('H3 CA4: descartar cierra la propuesta', async ({ page }) => {
  test.skip(!aprobacionId, 'depende de la propuesta de H3 CA3');
  await page.goto(`/admin/studio?s=${aprobacionId}`);
  const propuesta = sessionRegion(page).getByRole('article', { name: 'Propuesta' });
  await expectToast(page, 'Propuesta descartada', () => propuesta.getByRole('button', { name: 'Descartar' }).click());
  await expect(pedido(page).getByText('Descartada', { exact: true })).toBeVisible();
  await expect(propuesta.getByRole('button', { name: 'Descartar' })).toHaveCount(0);
  await expect(propuesta.getByRole('button', { name: 'Revisar y aplicar…' })).toHaveCount(0);
});

test('H2 CA3: cancelar detiene la sesión en curso', async ({ page }) => {
  scripts.cancelar = steps({ blocks: [tool('listar_catalogos')] }, { blocks: [], hold: true });
  received.marca();
  await ask(page, '[guion:cancelar] Algo que tarda mucho.', { aislado: false });
  await received.siguiente((r) => r.tag === 'cancelar' && r.turn === 1);
  await expect(stepRow(page, 'listar_catalogos')).toBeVisible({ timeout: 30_000 });
  await expectToast(page, 'Cancelando la sesión', () => page.getByRole('button', { name: 'Cancelar', exact: true }).click());
  await waitStatus(page, 'Cancelada');
  await expect(page.getByRole('button', { name: 'Cancelar', exact: true })).toHaveCount(0);
  await expect(page.getByRole('meter', { name: 'Pasos' })).toHaveAttribute('aria-valuenow', '1');
});

test('H2 CA4: el texto del agente y las salidas se muestran como dato, sin HTML ni enlaces', async ({ page }) => {
  scripts.hostil = steps(
    { blocks: [say(`Revisé esto: ${HOSTIL}`), tool('ver_catalogo', { entity: 'StE2eHostil' })] },
    deliver({ summary: `Resumen ${HOSTIL} <b>negritas</b>`, failures: [`Falla ${HOSTIL}`] }),
  );
  await ask(page, '[guion:hostil] Revisa el catálogo hostil.', { aislado: false });
  await waitStatus(page, 'Respondida');
  const region = sessionRegion(page);
  await region.locator('details').evaluateAll((all) => all.forEach((d) => ((d as HTMLDetailsElement).open = true)));
  await expect(stepRow(page, 'ver_catalogo').getByLabel(/Salida del paso/)).toContainText('onerror');
  const respuesta = region.getByRole('article', { name: 'Respuesta del agente' });
  await expect(respuesta.getByRole('paragraph').filter({ hasText: 'Resumen <img src=x onerror="window.__xss=1">' })).toContainText('<b>negritas</b>');
  await expect(respuesta.getByRole('listitem').filter({ hasText: 'Falla <img src=x' })).toBeVisible();
  await expect(region.getByText(/\[Haz clic aquí\]\(https:\/\/phishing\.example\/login\)/).first()).toBeVisible();
  await expect(region.locator('a, img, b, script, iframe')).toHaveCount(0);
  expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
});

test('H4 CA1: el historial abre una sesión anterior completa', async ({ page }) => {
  test.skip(!vivoId, 'depende de la sesión «vivo»');
  await page.goto('/admin/studio');
  const history = page.getByRole('navigation', { name: 'Sesiones' });
  const item = history.getByRole('button', { name: /Agrega el peso a las tareas/ });
  await expect(item).toContainText('Propuesta lista');
  await item.click();
  await expect(page).toHaveURL(new RegExp(`[?&]s=${vivoId}$`));
  await expect(item).toHaveAttribute('aria-current', 'true');
  await expect(pedido(page).getByText(VIVO)).toBeVisible();
  await expect(stepRow(page, 'aislado_aplicar_plan')).toBeVisible();
  await expect(sessionRegion(page).getByRole('article', { name: 'Propuesta' }).getByText('El plan es válido')).toBeVisible();
  await expect(history.getByRole('button', { name: /Agrega una referencia/ })).toContainText('Aplicada');
});

test('H1 CA3: con una sesión en curso la pantalla la abre y otra pestaña no puede pedir otra', async ({ page }) => {
  scripts.larga = steps({ blocks: [], hold: true });
  // La segunda pestaña abrió el formulario antes de que hubiera una sesión activa.
  const other = await page.context().newPage();
  await other.goto('/admin/studio');
  await other.getByLabel('¿Qué desarrollo necesitas?').fill('[guion:larga] Otro pedido desde otra pestaña.');

  received.marca();
  const id = await ask(page, '[guion:larga] Algo largo.', { aislado: false });
  await received.siguiente((r) => r.tag === 'larga');
  await other.getByRole('button', { name: 'Pedir a Studio' }).click();
  await expect(other.getByRole('alert').filter({ hasText: 'Ya tienes una sesión de ERP AI Studio en curso' })).toBeVisible();
  await other.close();

  // Al volver a la pantalla, abre la sesión en curso y el formulario no deja pedir otra.
  await page.goto('/admin/studio');
  await expect(page).toHaveURL(new RegExp(`[?&]s=${id}$`));
  await page.getByRole('button', { name: 'Nuevo pedido' }).click();
  await expect(page.getByText('Ya tienes una sesión en curso')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Pedir a Studio' })).toBeDisabled();
  await page.getByRole('button', { name: 'Ver la sesión' }).click();
  await expectToast(page, 'Cancelando la sesión', () => page.getByRole('button', { name: 'Cancelar', exact: true }).click());
  await waitStatus(page, 'Cancelada');
});
