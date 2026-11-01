// Spec 042 · Tableros personales y compartir. Catálogo propio declarado por plan (no depende de otro archivo): pedidos con
// zona, monto y un campo interno que solo ve Ventas; la dueña tiene Ventas y Equipo; el lector, solo Equipo (sin la zona
// «Privada» ni el campo Interno); «otro» tiene un rol con el que no se comparte nada. Lo que se prueba, por la UI; la
// concurrencia, la revalidación del reparto, el tope de corridas y la bitácora, en
// tests/Erp.IntegrationTests/PersonalDashboardsTests.cs.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, expectToast, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });
// La página de cada prueba (con las guardias de helpers.ts) entra como la dueña o el lector, no como el administrador.
test.use({ storageState: { cookies: [], origins: [] } });

const PWD = 'frase larga de tableros 042';
const EQUIPO = 'Tp42 Equipo';
const VENTAS = 'Tp42 Ventas';
const AJENO = 'Tp42 Ajeno';
const DUENA = 'e2e-tp42-duena';
const LECTOR = 'e2e-tp42-lector';
const OTRO = 'e2e-tp42-otro';
const E = 'Tp42Pedido';
const ZONAS = 'Zonas de la dueña 042';
const INTERNO = 'Por interno 042';
const TABLERO = 'Mi tablero 042';
let admin: APIRequestContext;
test.beforeAll(async () => { admin = await apiAs(ADMIN.username, ADMIN.password); });

async function pageAs(browser: Browser, user: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

const def = (field: string) => ({ entity: E, groupBy: [{ field }], measures: [{ fn: 'sum', field: 'Monto', label: 'Monto' }], chart: 'bar' });

test('preparar: pedidos con zona y un campo interno, roles, reportes del modelo y personales, y datos', async () => {
  const roles: Record<string, number> = {};
  for (const name of [EQUIPO, VENTAS, AJENO]) {
    const role = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 042', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] } });
    // En modo independiente, tras una falla se vuelve a preparar: el rol ya existe.
    if (role.status() !== 200) {
      const list = (await (await admin.get('/api/admin/roles')).json()) as { id: number; name: string }[];
      roles[name] = list.find((r) => r.name === name)!.id;
      continue;
    }
    roles[name] = (await role.json()).id;
  }
  await applyPlan(admin, { description: 'Spec 042 · tableros personales', operations: [
    { op: 'createEntity', group: 'Tableros 042', name: E, label: 'Pedido T', labelPlural: 'Pedidos T', fields: [
      { name: 'Fecha', label: 'Fecha', type: 'date', required: true },
      { name: 'Zona', label: 'Zona', type: 'text', required: true },
      { name: 'Monto', label: 'Monto', type: 'decimal', required: true },
      { name: 'Interno', label: 'Interno', type: 'text' }] },
    { op: 'setSecurity', entity: E, security: {
      fields: [{ field: 'Interno', read: ['Administrador', VENTAS] }],
      records: [{ roles: ['Administrador', VENTAS, AJENO], access: 'read' }, { roles: [EQUIPO], where: "Zona <> 'Privada'", access: 'read' }] } },
    { op: 'setReports', entity: E, reports: [
      { name: 'PorZona', label: 'Ventas por zona', groupBy: [{ field: 'Zona' }], measures: [{ fn: 'sum', field: 'Monto', label: 'Ventas' }], chart: 'bar' },
      { name: 'Total', label: 'Total vendido', measures: [{ fn: 'sum', field: 'Monto', label: 'Ventas' }] }] },
  ] });
  const perms = [{ entity: E, canRead: true, canCreate: false, canUpdate: false, canDelete: false }];
  for (const name of [EQUIPO, VENTAS, AJENO])
    expect((await admin.put(`/api/admin/roles/${roles[name]}`, { data: { name, description: 'E2E spec 042', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: perms } })).status()).toBe(200);
  for (const [username, displayName, roleIds] of [[DUENA, 'Dueña 042', [roles[EQUIPO], roles[VENTAS]]], [LECTOR, 'Lector 042', [roles[EQUIPO]]], [OTRO, 'Otro 042', [roles[AJENO]]]] as const) {
    const u = await admin.post('/api/admin/users', { data: { username, displayName, password: PWD, roleIds } });
    expect([201, 400, 409], await u.text()).toContain(u.status());
  }
  if (!(await (await admin.get(`/api/data/${E}`)).json()).items.length)
    for (const [Fecha, Zona, Monto, Interno] of [['2026-10-01', 'Norte', 100, 'nota 042'], ['2026-10-02', 'Sur', 50, null], ['2026-10-03', 'Privada', 500, null]] as const) {
      const res = await admin.post(`/api/data/${E}`, { data: { Fecha, Zona, Monto, Interno } });
      expect(res.status(), await res.text()).toBe(201);
    }
  const duena = await apiAs(DUENA, PWD);
  const mine = (await (await duena.get('/api/my-reports')).json()) as { label: string }[];
  for (const [label, field] of [[ZONAS, 'Zona'], [INTERNO, 'Interno']])
    if (!mine.some((r) => r.label === label)) expect((await duena.post('/api/my-reports', { data: { label, definition: def(field) } })).status()).toBe(201);
});

async function reportId(user: string, label: string): Promise<{ id: number; version: number }> {
  const list = (await (await (await apiAs(user, PWD)).get('/api/my-reports')).json()) as { id: number; label: string; version: number }[];
  const r = list.find((x) => x.label === label);
  expect(r, `el reporte «${label}»`).toBeTruthy();
  return r!;
}

async function boardId(): Promise<{ id: number; version: number }> {
  const list = (await (await (await apiAs(DUENA, PWD)).get('/api/my-dashboards')).json()) as { mine: { id: number; label: string; version: number }[] };
  const d = list.mine.find((x) => x.label === TABLERO);
  expect(d, `el tablero «${TABLERO}» de H1 CA1`).toBeTruthy();
  return d!;
}

const card = (page: Page, n: number) => page.getByRole('group', { name: `Tarjeta ${n}` });
const dashboardGet = (page: Page) => page.waitForResponse((r) => /\/api\/my-dashboards\/\d+$/.test(r.url()) && r.request().method() === 'GET');

test('H1 CA1: la dueña arma un tablero con reportes del modelo y suyos, lo reordena y aparece en «Mis tableros»', async ({ page }) => {
  await uiLogin(page, DUENA, PWD);
  // Regresión (F-01, al escribir esta prueba): sin tableros del modelo, el inicio no ofrecía «Tableros» y no había cómo llegar
  // a «Nuevo tablero».
  await page.goto('/');
  await page.getByRole('region', { name: 'Análisis general' }).getByRole('link', { name: /Tableros/ }).click();
  await page.getByRole('link', { name: 'Nuevo tablero' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Nuevo tablero' })).toBeVisible();
  // Regresión (revisión exploratoria 042, F-03): el foco quedaba en body y lo que se escribía se perdía.
  await expect(page.getByLabel('Nombre del tablero')).toBeFocused();
  await page.getByLabel('Nombre del tablero').fill(TABLERO);
  await card(page, 1).getByLabel('Reporte').selectOption({ label: 'Total vendido · Pedidos T' });
  // Regresión (revisión exploratoria 042, F-02): los campos de la tarjeta ocupaban su contenido, no el ancho de su columna.
  const [select, group] = await Promise.all([card(page, 1).getByLabel('Reporte').boundingBox(), card(page, 1).boundingBox()]);
  expect(select!.width).toBeGreaterThan(group!.width * 0.5);
  await page.getByRole('button', { name: 'Agregar tarjeta' }).click();
  await card(page, 2).getByLabel('Reporte').selectOption({ label: INTERNO });
  await card(page, 2).getByLabel('Tipo').selectOption({ label: 'Tabla' });
  await page.getByRole('button', { name: 'Agregar tarjeta' }).click();
  await card(page, 3).getByLabel('Reporte').selectOption({ label: ZONAS });
  await card(page, 3).getByLabel('Título (opcional)').fill('Mis zonas');
  // Un interruptor se acciona con un clic en su etiqueta, como lo hace una persona (el input está oculto bajo la pista).
  await card(page, 3).locator('label.switch').filter({ hasText: 'Ancho completo' }).click();
  await expect(card(page, 3).getByRole('switch', { name: 'Ancho completo' })).toBeChecked();
  // La de zonas sube al segundo lugar.
  await card(page, 3).getByRole('button', { name: 'Subir la tarjeta 3' }).click();
  await expect(card(page, 2).getByLabel('Título (opcional)')).toHaveValue('Mis zonas');
  await page.getByRole('button', { name: 'Agregar tarjeta' }).click();
  await card(page, 4).getByLabel('Reporte').selectOption({ label: 'Ventas por zona · Pedidos T' });
  await expectAccessible(page);
  await expectToast(page, 'Tablero guardado', () => tras(dashboardGet(page), () => page.getByRole('button', { name: 'Guardar' }).click()));
  await expect(page).toHaveURL(/\/my-dashboards\/\d+$/);
  await expect(page.getByRole('heading', { level: 1, name: TABLERO })).toBeVisible();
  // Regresión (revisión exploratoria 042, F-04): la ruta de navegación decía solo «Inicio».
  await expect(page.getByRole('navigation', { name: 'Ruta de navegación' }).getByRole('link', { name: 'Tableros' })).toHaveAttribute('href', '/dashboards');
  await expect(page.getByRole('region', { name: 'Total vendido' })).toContainText('650.00');
  await expect(page.getByRole('region', { name: 'Mis zonas' }).getByRole('img')).toBeVisible();
  await expect(page.getByRole('region', { name: INTERNO }).getByRole('table')).toContainText('nota 042');
  await expect(page.getByRole('region', { name: 'Ventas por zona' }).getByRole('link', { name: 'Ventas por zona' })).toHaveAttribute('href', `/reports/${E}/PorZona`);
  await expectAccessible(page);
  await page.goto('/dashboards');
  await expect(page.getByRole('region', { name: 'Mis tableros' }).getByRole('link', { name: new RegExp(TABLERO) })).toBeVisible();
});

test('H1 CA2: una tarjeta que no tiene sentido dice qué cambiar y no pierde lo armado', async ({ page }) => {
  await uiLogin(page, DUENA, PWD);
  const { id } = await boardId();
  await tras(dashboardGet(page), () => page.goto(`/my-dashboards/${id}`));
  await page.getByRole('button', { name: 'Editar' }).click();
  await expect(page.getByLabel('Nombre del tablero')).toBeFocused(); // F-03
  await card(page, 1).getByLabel('Tipo').selectOption({ label: 'Gráfica' });
  const res = await tras(page.waitForResponse((r) => r.url().endsWith(`/api/my-dashboards/${id}`) && r.request().method() === 'PUT'),
    () => page.getByRole('button', { name: 'Guardar' }).click());
  expect(res.status()).toBe(400);
  await expect(page.getByRole('alert')).toContainText('Tarjeta 1: una gráfica usa un reporte con agrupación.');
  await expect(card(page, 1).getByLabel('Tipo')).toHaveValue('chart');
  await expect(card(page, 2).getByLabel('Título (opcional)')).toHaveValue('Mis zonas');
  await page.getByRole('button', { name: 'Cancelar' }).click();
  await expect(page.getByRole('button', { name: 'Editar' })).toBeFocused(); // F-03
  await expect(page.getByRole('region', { name: 'Total vendido' })).toContainText('650.00');
});

test('H1 CA3: si otra pestaña lo cambió antes, guardar responde 409 y no pisa el cambio', async ({ page }) => {
  await uiLogin(page, DUENA, PWD);
  const a = page;
  const b = await a.context().newPage();
  const { id } = await boardId();
  for (const p of [a, b]) {
    await tras(dashboardGet(p), () => p.goto(`/my-dashboards/${id}`));
    await p.getByRole('button', { name: 'Editar' }).click();
  }
  await a.getByLabel('Descripción (opcional)').fill('Cambiada en la pestaña A');
  await expectToast(a, 'Cambios guardados', () => a.getByRole('button', { name: 'Guardar' }).click());
  await b.getByLabel('Descripción (opcional)').fill('Cambiada en la pestaña B');
  const res = await tras(b.waitForResponse((r) => r.url().endsWith(`/api/my-dashboards/${id}`) && r.request().method() === 'PUT'),
    () => b.getByRole('button', { name: 'Guardar' }).click());
  expect(res.status()).toBe(409);
  await expect(b.getByRole('alert')).toContainText('Otra pestaña cambió este tablero');
  await tras(dashboardGet(b), () => b.getByRole('button', { name: 'Volver a abrir' }).click());
  await expect(b.getByText('Cambiada en la pestaña A')).toBeVisible();
  await b.close();
});

test('H1 CA4: una tarjeta que dejó de correr se marca con el motivo y el resto sigue', async ({ page }) => {
  const duena = await apiAs(DUENA, PWD);
  const temp = await duena.post('/api/my-reports', { data: { label: `Temporal 042 ${Date.now()}`, definition: def('Zona') } });
  const tempId = (await temp.json()).id as number;
  const board = await duena.post('/api/my-dashboards', { data: { label: `Con baja 042 ${Date.now()}`, widgets: [
    { type: 'chart', personalReport: tempId, label: 'Se borrará' }, { type: 'kpi', entity: E, report: 'Total' }] } });
  expect(board.status(), await board.text()).toBe(201);
  expect((await duena.delete(`/api/my-reports/${tempId}`, { params: { version: '1' } })).status()).toBe(204);
  const boardRef = (await board.json()).id as number;
  await uiLogin(page, DUENA, PWD);
  await tras(dashboardGet(page), () => page.goto(`/my-dashboards/${boardRef}`));
  await expect(page.getByRole('region', { name: 'Se borrará' }).getByRole('status')).toContainText('El reporte personal ya no existe.');
  await expect(page.getByRole('region', { name: 'Total vendido' })).toContainText('650.00');
  await expect.soft(page.getByRole('region', { name: 'Se borrará' }).getByRole('link')).toHaveCount(0);
});

test('H2 CA1 · CA2 · H4 CA1: compartir un reporte con un rol propio; el lector lo abre con sus datos y guarda su copia', async ({ page, browser }) => {
  await uiLogin(page, DUENA, PWD);
  const { id } = await reportId(DUENA, ZONAS);
  await page.goto(`/explore/${id}`);
  await expect(page.getByRole('heading', { level: 1, name: ZONAS })).toBeVisible();
  await page.getByRole('button', { name: 'Compartir' }).click();
  const dialog = page.getByRole('dialog', { name: 'Compartir el reporte' });
  // Regresión (revisión exploratoria 042, F-05): el diálogo enfocaba «Cerrar» en lugar de la primera casilla.
  await expect(dialog.getByRole('checkbox').first()).toBeFocused();
  // Solo los roles que tiene.
  await expect(dialog.getByRole('checkbox')).toHaveCount(2);
  await expect(dialog.getByRole('checkbox', { name: AJENO })).toHaveCount(0);
  await dialog.getByRole('checkbox', { name: EQUIPO }).check();
  await expectToast(page, 'Reporte compartido', () => dialog.getByRole('button', { name: 'Guardar' }).click());
  await expect(page.getByRole('button', { name: 'Compartir (1)' })).toBeFocused();
  await expect(page.getByText(`Reporte personal compartido con ${EQUIPO}`)).toBeVisible();

  const lector = await pageAs(browser, LECTOR);
  await lector.goto('/reports');
  const shared = lector.getByRole('region', { name: 'Compartidos conmigo' });
  await expect(shared.getByRole('link', { name: new RegExp(ZONAS) })).toContainText('De Dueña 042');
  await tras(lector.waitForResponse((r) => r.url().endsWith('/api/my-reports/explore')), () => shared.getByRole('link', { name: new RegExp(ZONAS) }).click());
  await expect(lector.getByRole('heading', { level: 1, name: ZONAS })).toBeVisible();
  await expect(lector.getByText('Compartido por Dueña 042')).toBeVisible();
  // Con sus datos: sin la zona Privada.
  const rows = lector.getByRole('region', { name: 'Datos del reporte' }).getByRole('row');
  await expect(rows.filter({ hasText: 'Norte' })).toContainText('100.00');
  await expect(lector.getByText('Privada')).toHaveCount(0);
  // Sin guardar, borrar ni compartir lo de otro: solo su copia.
  await expect(lector.getByRole('button', { name: 'Guardar', exact: true })).toHaveCount(0);
  await expect(lector.getByRole('button', { name: /Compartir/ })).toHaveCount(0);
  await expect(lector.getByRole('button', { name: 'Borrar el reporte' })).toHaveCount(0);
  await expectAccessible(lector);
  await lector.getByRole('button', { name: 'Guardar como mío' }).click();
  const naming = lector.getByRole('dialog', { name: 'Guardar como mío' });
  await naming.getByLabel('Nombre del reporte').fill(`Copia del lector 042 ${Date.now()}`);
  await expectToast(lector, 'Reporte guardado', () => naming.getByLabel('Nombre del reporte').press('Enter'));
  await expect(lector).toHaveURL(new RegExp(`/explore/(?!${id}$)\\d+$`));
  await expect(lector.getByRole('button', { name: 'Guardar', exact: true })).toBeVisible();
  await lector.context().close();
});

test('H4 CA2: un reporte compartido que usa lo que el lector no ve no le llega ni lo nombra', async ({ browser }) => {
  const duena = await apiAs(DUENA, PWD);
  const r = await reportId(DUENA, INTERNO);
  const res = await duena.put(`/api/my-reports/${r.id}/share`, { data: { roles: [EQUIPO], version: r.version } });
  expect(res.status(), await res.text()).toBe(200);
  const lector = await pageAs(browser, LECTOR);
  await lector.goto('/reports');
  const link = lector.getByRole('region', { name: 'Compartidos conmigo' }).getByRole('link', { name: new RegExp(INTERNO) });
  await expect(link).toContainText('No disponible para ti');
  await link.click();
  await expect(lector.getByRole('status').filter({ hasText: 'Este reporte no está disponible para ti' })).toBeVisible();
  await expect(lector.getByText('Interno', { exact: true })).toHaveCount(0);
  await expect(lector.getByText('nota 042')).toHaveCount(0);
  await lector.context().close();
});

test('H3 CA1 · CA2 · H4 CA1: compartir el tablero; el lector ve cada tarjeta con sus datos y solo enlaza a lo que abre', async ({ page, browser }) => {
  await uiLogin(page, DUENA, PWD);
  const { id } = await boardId();
  await tras(dashboardGet(page), () => page.goto(`/my-dashboards/${id}`));
  await page.getByRole('button', { name: 'Compartir' }).click();
  const dialog = page.getByRole('dialog', { name: 'Compartir el tablero' });
  await dialog.getByRole('checkbox', { name: EQUIPO }).check();
  await expectToast(page, 'Tablero compartido', () => dialog.getByRole('button', { name: 'Guardar' }).click());
  await expect(page.getByRole('button', { name: 'Compartir (1)' })).toBeFocused();
  // El reporte de zonas deja de compartirse aparte: va solo con el tablero.
  const z = await reportId(DUENA, ZONAS);
  expect((await (await apiAs(DUENA, PWD)).put(`/api/my-reports/${z.id}/share`, { data: { roles: [], version: z.version } })).status()).toBe(200);

  const lector = await pageAs(browser, LECTOR);
  await lector.goto('/');
  await lector.getByRole('region', { name: 'Análisis general' }).getByRole('link', { name: /Tableros/ }).click();
  const shared = lector.getByRole('region', { name: 'Compartidos conmigo' });
  await expect(shared.getByRole('link', { name: new RegExp(TABLERO) })).toContainText('De Dueña 042');
  await tras(dashboardGet(lector), () => shared.getByRole('link', { name: new RegExp(TABLERO) }).click());
  await expect(lector.getByRole('heading', { level: 1, name: TABLERO })).toBeVisible();
  await expect(lector.getByText('Compartido por Dueña 042')).toBeVisible();
  // Su total (sin la zona Privada); sin la tarjeta del campo Interno; sin editar ni compartir.
  await expect(lector.getByRole('region', { name: 'Total vendido' })).toContainText('150.00');
  await expect(lector.getByRole('region', { name: INTERNO })).toHaveCount(0);
  await expect(lector.getByText('nota 042')).toHaveCount(0);
  await expect(lector.getByRole('button', { name: 'Editar' })).toHaveCount(0);
  await expect(lector.getByRole('button', { name: /Compartir/ })).toHaveCount(0);
  // La de zonas corre con él pero no enlaza a su definición; la del modelo sí.
  await expect(lector.getByRole('region', { name: 'Mis zonas' }).getByRole('img')).toBeVisible();
  await expect(lector.getByRole('region', { name: 'Mis zonas' }).getByRole('link')).toHaveCount(0);
  await expect(lector.getByRole('region', { name: 'Ventas por zona' }).getByRole('link', { name: 'Ventas por zona' })).toBeVisible();
  await expectAccessible(lector);
  await lector.context().close();
});

test('H2 CA3 · H4 CA4: dejar de compartir lo quita de inmediato; quien no tiene el rol no lo abre por su dirección', async ({ browser }) => {
  const { id } = await boardId();
  const otro = await pageAs(browser, OTRO);
  await otro.goto(`/my-dashboards/${id}`);
  await expect(otro.getByText('Página no encontrada')).toBeVisible();
  await otro.goto('/dashboards');
  await expect(otro.getByRole('region', { name: 'Compartidos conmigo' })).toHaveCount(0);
  await otro.context().close();
  // La dueña deja de compartir el reporte de interno: el lector ya no lo tiene.
  const r = await reportId(DUENA, INTERNO);
  expect((await (await apiAs(DUENA, PWD)).put(`/api/my-reports/${r.id}/share`, { data: { roles: [], version: r.version } })).status()).toBe(200);
  const lector = await pageAs(browser, LECTOR);
  await lector.goto('/reports');
  await expect(lector.getByRole('heading', { level: 1, name: 'Reportes' })).toBeVisible();
  await expect(lector.getByText(INTERNO)).toHaveCount(0);
  await lector.goto(`/explore/${r.id}`);
  await expect(lector.getByText('Página no encontrada')).toBeVisible();
  await lector.context().close();
});

test('H5 CA1 · CA2: con demasiadas consultas a la vez la pantalla lo dice; el tablero queda en la bitácora', async ({ page }) => {
  // La prueba del tope (cuatro corridas detenidas por otra conexión, la quinta 429) está en integración; aquí, la pantalla.
  await uiLogin(page, DUENA, PWD);
  const { id } = await boardId();
  await page.route(`**/api/my-dashboards/${id}`, (route) => route.request().method() === 'GET'
    ? route.fulfill({ status: 429, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Tienes demasiadas consultas en curso. Espera a que terminen e intenta de nuevo.', status: 429 }) })
    : route.fallback());
  await page.goto(`/my-dashboards/${id}`);
  await expect(page.getByRole('alert')).toContainText('Tienes demasiadas consultas en curso');
  await page.unroute(`**/api/my-dashboards/${id}`);
  await tras(dashboardGet(page), () => page.getByRole('button', { name: 'Reintentar' }).click());
  await expect(page.getByRole('heading', { level: 1, name: TABLERO })).toBeVisible();
  const audit = await (await admin.get('/api/audit', { params: { entity: 'PersonalDashboard', pageSize: '200' } })).json();
  const mine = audit.items.filter((x: { action: string; recordId: string }) => x.action === 'dashboard.personal' && x.recordId === String(id));
  expect(mine.length).toBeGreaterThanOrEqual(3); // alta, cambio (H1 CA3) y reparto (H3)
  expect(JSON.stringify(mine)).not.toContain('Mis zonas');
});

// Capturas: el editor del tablero (datos propios del archivo), en claro, oscuro y móvil.
for (const [name, scheme, viewport] of [['light', 'light', { width: 1280, height: 900 }], ['dark', 'dark', { width: 1280, height: 900 }],
  ['movil', 'light', { width: 390, height: 844 }]] as const) {
  test(`visual: editor del tablero personal en ${name === 'movil' ? 'móvil' : name === 'light' ? 'claro' : 'oscuro'}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await uiLogin(page, DUENA, PWD);
    await page.setViewportSize(viewport);
    const { id } = await boardId();
    await tras(dashboardGet(page), () => page.goto(`/my-dashboards/${id}`));
    await page.getByRole('button', { name: 'Editar' }).click();
    await expect(card(page, 4).getByLabel('Reporte')).toHaveValue(`m:${E}/PorZona`);
    await page.mouse.move(0, 0);
    await page.evaluate(() => document.fonts.ready);
    await expect(page.getByRole('main')).toHaveScreenshot(`editor-${name}.png`);
    if (name === 'movil') {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    }
  });
}
