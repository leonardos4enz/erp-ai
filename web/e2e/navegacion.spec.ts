// Spec 024: navegación por módulos — selector, menú por módulo, área de configuración, análisis por módulo y búsqueda global.
// Corre después de compras e inventario (orden alfabético): Compras e Inventario ya están instalados con su módulo declarado.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test, uiLogin } from './helpers';

const PWD = 'frase larga de navegación 2026';
const users = { lector: 'e2e-nav-lector', nada: 'e2e-nav-nada' } as const;
type Who = 'admin' | keyof typeof users;
const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));

async function pageAs(browser: Browser, w: keyof typeof users, width = 1280): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined, viewport: { width, height: 800 } });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

const nav = (page: Page) => page.getByLabel('Navegación principal');
const banner = (page: Page) => page.getByRole('banner');
/** Abre la paleta con el atajo, como una persona con teclado. */
async function palette(page: Page) {
  // El atajo se escucha cuando la página ya montó su barra superior.
  await expect(page.getByRole('banner').getByRole('button', { name: 'Búsqueda global' })).toBeVisible();
  await page.keyboard.press('Control+k');
  const box = page.getByRole('combobox', { name: /Buscar/ });
  await expect(box).toBeFocused();
  return box;
}

test('preparar usuarios, un módulo declarado y uno implícito', async () => {
  const admin = await as('admin');
  const role = await admin.post('/api/admin/roles', { data: {
    name: 'Lector de proveedores', description: 'Solo lee proveedores (spec 024).', canManageModel: false, canManageUsers: false, canViewAudit: false,
    permissions: [{ entity: 'Proveedor', canRead: true, canCreate: false, canUpdate: false, canDelete: false }],
  } });
  expect(role.status(), await role.text()).toBe(200);
  const roleId = (await role.json()).id;
  for (const [u, roleIds] of [[users.lector, [roleId]], [users.nada, []]] as const) {
    const res = await admin.post('/api/admin/users', { data: { username: u, displayName: u, password: PWD, roleIds } });
    expect(res.status(), await res.text()).toBe(201);
  }
  await applyPlan(admin, { description: 'Flotilla (spec 024)', operations: [
    { op: 'setApp', app: { name: 'Flotilla', label: 'Flotilla', icon: 'truck', order: 40, description: 'Unidades, choferes y mantenimiento.' } },
    { op: 'createEntity', name: 'NavUnidad', label: 'Unidad', labelPlural: 'Unidades', icon: 'truck', group: 'Flotilla',
      fields: [{ name: 'Placa', label: 'Placa', type: 'text', required: true }] },
    // Grupo sin declarar: módulo implícito con su nombre.
    { op: 'createEntity', name: 'NavHerramienta', label: 'Herramienta', labelPlural: 'Herramientas', icon: 'wrench', group: 'Taller Norte',
      fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
  ] });
});

test('H1 CA1 · CA2 · CA4: el inicio muestra los módulos en orden con su descripción y elegir uno deja solo lo suyo en el menú', async ({ page }) => {
  await page.goto('/');
  const cards = page.getByRole('main').getByRole('list').getByRole('link');
  await expect(cards.first()).toBeVisible(); // los módulos llegan del servidor
  const names = await cards.evaluateAll((as) => as.map((a) => a.querySelector('.entity-card-title')?.textContent));
  // Declarados por orden (Compras 10, Inventario 20, Flotilla 40), luego los implícitos por nombre y General al final.
  expect(names.slice(0, 3)).toEqual(['Compras', 'Inventario', 'Flotilla']);
  expect(names).toContain('Taller Norte');
  expect(names.at(-1)).toBe('General');
  await expect(cards.filter({ hasText: 'Flotilla' })).toContainText('Unidades, choferes y mantenimiento.');
  await expect(cards.filter({ hasText: 'Flotilla' })).toContainText('1 catálogo');
  await expectAccessible(page);

  await cards.filter({ hasText: /^Compras/ }).click();
  await expect(page).toHaveURL(/\/m\/Compras$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Compras' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Órdenes de compra' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Proveedores' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Existencias' })).toHaveCount(0);
  await expect(nav(page).getByRole('link', { name: 'Unidades', exact: true })).toHaveCount(0);
  // Documentos (con proceso) antes que catálogos, también en la página del módulo.
  const titles = await nav(page).locator('.nav-section-title').allTextContents();
  expect(titles.slice(0, 2)).toEqual(['Documentos', 'Catálogos']);
  await expect(page.getByRole('region', { name: 'Documentos' }).getByRole('link', { name: /Órdenes de compra/ })).toBeVisible();
  await expect(banner(page).getByRole('navigation', { name: 'Ruta de navegación' })).toContainText('Compras');
  await expectAccessible(page);
});

test('H1 CA3: se recuerda el último módulo y un catálogo de otro módulo cambia el menú', async ({ page }) => {
  await page.goto('/m/Inventario');
  await expect(nav(page).getByRole('link', { name: 'Existencias' })).toBeVisible();
  await page.goto('/');
  await expect(nav(page).getByRole('link', { name: 'Inventario', exact: true })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Existencias' })).toBeVisible();
  // Un enlace guardado a un catálogo de Compras: el menú pasa a Compras y la miga de pan lo dice.
  await page.goto('/c/Proveedor');
  await expect(nav(page).getByRole('link', { name: 'Proveedores' })).toHaveAttribute('aria-current', 'page');
  await expect(nav(page).getByRole('link', { name: 'Existencias' })).toHaveCount(0);
  await expect(banner(page).getByRole('link', { name: 'Compras' })).toHaveAttribute('href', '/m/Compras');
  await page.reload();
  await page.goto('/');
  await expect(nav(page).getByRole('link', { name: 'Proveedores' })).toBeVisible();
});

test('H1 CA5: cada usuario ve solo los módulos con catálogos que puede leer (filtrado en el servidor)', async ({ browser }) => {
  const lector = await as('lector');
  const apps = await (await lector.get('/api/model/apps')).json();
  expect(apps).toEqual([expect.objectContaining({ name: 'Compras', declared: true, entities: ['Proveedor'] })]);
  expect((await lector.get('/api/model/apps', { params: { all: 'true' } })).status()).toBe(403);
  expect(await (await (await as('nada')).get('/api/model/apps')).json()).toEqual([]);

  const page = await pageAs(browser, 'lector');
  await expect(page.getByRole('main').getByRole('list').getByRole('link')).toHaveCount(1);
  await expect(nav(page).getByRole('link', { name: 'Proveedores' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Órdenes de compra' })).toHaveCount(0);
  await page.goto('/m/Inventario');
  await expect(page.getByText('Página no encontrada')).toBeVisible();
  await page.context().close();
});

test('H2 CA1 · CA3: los catálogos sin módulo están en General y el modelador declara un módulo implícito', async ({ page }) => {
  await page.goto('/m/General');
  await expect(page.getByRole('heading', { level: 1, name: 'General' })).toBeVisible();
  await expect(page.getByText('Catálogos que no pertenecen a otro módulo.')).toBeVisible();

  await page.goto('/admin/model');
  await page.getByRole('tab', { name: 'Módulos' }).click();
  const row = page.getByRole('row', { name: /Taller Norte/ });
  await expect(row.getByText('Implícito')).toBeVisible();
  await row.getByRole('button', { name: 'Declarar módulo Taller Norte' }).click();
  const dialog = page.getByRole('dialog', { name: 'Declarar módulo' });
  await expect(dialog.getByLabel('Nombre')).toBeDisabled();
  await expect(dialog.getByLabel('Etiqueta')).toBeFocused();
  await dialog.getByLabel('Etiqueta').fill('Taller del Norte');
  await dialog.getByLabel('Descripción').fill('Herramientas y refacciones del taller.');
  await dialog.getByLabel('Orden').fill('abc');
  await dialog.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(dialog.getByText('Un número de 0 a 9999.')).toBeVisible();
  await dialog.getByLabel('Orden').fill('50');
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Revisar cambios' }).click();
  const review = page.getByRole('dialog');
  await expect(review.getByText("Declarar el módulo 'Taller del Norte'")).toBeVisible();
  await review.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  await expect(page.getByRole('row', { name: /Taller del Norte/ }).getByText('Declarado')).toBeVisible();

  await banner(page).getByRole('button', { name: 'Cambiar de módulo' }).click();
  await expect(page.getByRole('main').getByRole('link', { name: /^Taller del Norte/ })).toContainText('Herramientas y refacciones del taller.');
});

test('H2 CA2: al crear desde un módulo el editor propone ese módulo y sugiere los existentes', async ({ page }) => {
  await page.goto('/m/Flotilla');
  await page.getByRole('link', { name: 'Nuevo catálogo' }).click();
  await expect(page).toHaveURL(/\/admin\/model\?new=1&app=Flotilla$/);
  const field = page.getByRole('combobox', { name: 'Módulo', exact: true }); // un campo con sugerencias (datalist)
  await expect(field).toHaveValue('Flotilla');
  await expect(field).toHaveAttribute('list', 'modeler-apps');
  await expect(page.locator('#modeler-apps option[value="Compras"]')).toHaveCount(1);
  await expect(page.locator('#modeler-apps option[value="General"]')).toHaveCount(0);
});

test('H3 CA1 · CA2: el engrane abre la configuración con su menú agrupado; sin capacidades no aparece', async ({ page, browser }) => {
  await page.goto('/m/Compras');
  await expect(nav(page).getByRole('link', { name: 'Modelador' })).toHaveCount(0);
  await banner(page).getByRole('button', { name: 'Configuración' }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Configuración' })).toBeVisible();
  const groups = nav(page).getByRole('group');
  await expect(groups).toHaveCount(4);
  expect(await groups.evaluateAll((gs) => gs.map((g) => g.getAttribute('aria-label')))).toEqual(['Modelo', 'Seguridad', 'Automatización e integraciones', 'Auditoría']);
  await expect(nav(page).getByRole('group', { name: 'Modelo' }).getByRole('link')).toHaveText(['Modelador', 'Módulos', 'Entornos']);
  await expect(nav(page).getByRole('group', { name: 'Seguridad' }).getByRole('link')).toHaveText(['Usuarios y roles', 'Empresas']);
  await expect(nav(page).getByRole('group', { name: 'Automatización e integraciones' }).getByRole('link')).toHaveText(['Automatizaciones', 'Integraciones', 'Asistente IA']);
  // Regresión (revisión exploratoria 024, F-01): el título del grupo más largo se cortaba en el borde del menú.
  expect(await nav(page).locator('.nav-section-title').evaluateAll((ts) => ts.filter((t) => t.scrollWidth > t.clientWidth + 1).map((t) => t.textContent))).toEqual([]);
  await expect(nav(page).getByRole('link', { name: 'Órdenes de compra' })).toHaveCount(0);
  await expectAccessible(page);
  await nav(page).getByRole('link', { name: 'Auditoría' }).click();
  await expect(page).toHaveURL(/\/admin\/audit$/);
  await expect(banner(page).getByRole('link', { name: 'Configuración' })).toHaveAttribute('href', '/admin');
  await nav(page).getByRole('link', { name: 'Volver a Compras' }).click();
  await expect(page).toHaveURL(/\/m\/Compras$/);

  const other = await pageAs(browser, 'lector');
  await expect(banner(other).getByRole('button', { name: 'Configuración' })).toHaveCount(0);
  await other.goto('/admin');
  await expect(other.getByText('Sin acceso')).toBeVisible();
  await other.context().close();
});

test('H4 CA1 · CA2: tableros y reportes dentro de su módulo, y la vista general con todos', async ({ page }) => {
  const reports = await (await (await as('admin')).get('/api/reports')).json() as { entity: string; name: string; label: string; group?: string }[];
  const compras = reports.filter((r) => r.group === 'Compras');
  const ajeno = reports.find((r) => r.group !== 'Compras');
  expect(compras.length).toBeGreaterThan(0);
  expect(ajeno).toBeTruthy();
  const boards = await (await (await as('admin')).get('/api/dashboards')).json() as { name: string; label: string; apps: string[] }[];
  const board = boards.find((b) => b.apps.includes('Compras'))!;
  expect(board).toBeTruthy();

  const reportLink = (r: { entity: string; name: string }) => `main a[href="/reports/${r.entity}/${r.name}"]`;
  await page.goto('/m/Compras');
  const analysis = page.getByRole('region', { name: 'Análisis' });
  await expect(page.locator(reportLink(compras[0]))).toBeVisible();
  await expect(analysis.locator(`a[href="/dashboards/${board.name}"]`)).toBeVisible();
  await expect(page.locator(reportLink(ajeno!))).toHaveCount(0);

  await nav(page).getByRole('link', { name: 'Reportes' }).click();
  await expect(page).toHaveURL(/\/reports\?app=Compras$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Reportes de Compras' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Reportes' })).toHaveAttribute('aria-current', 'page');
  await expect(page.locator(reportLink(compras[0]))).toBeVisible();
  await expect(page.locator(reportLink(ajeno!))).toHaveCount(0);
  await page.getByRole('link', { name: 'Ver todos los reportes' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Reportes' })).toBeVisible();
  await expect(page.locator(reportLink(ajeno!))).toBeVisible();

  await page.goto('/m/Compras');
  await nav(page).getByRole('link', { name: 'Tableros' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Tableros de Compras' })).toBeVisible();
  await expect(page.locator(`main a[href="/dashboards/${board.name}"]`)).toBeVisible();

  await page.goto('/');
  await expect(page.getByRole('region', { name: 'Análisis general' }).getByRole('link', { name: /Reportes/ })).toHaveAttribute('href', '/reports');
});

test('H5 CA1 · CA3: la paleta salta a catálogos, módulos y configuración solo con teclado', async ({ page }) => {
  await page.goto('/m/Inventario');
  let box = await palette(page);
  await expect(page.getByRole('dialog', { name: 'Buscar' })).toBeVisible();
  await box.fill('provee');
  const option = page.getByRole('dialog').getByRole('option', { name: /Proveedores/ });
  await expect(option).toHaveAttribute('aria-selected', 'true');
  await expect(box).toHaveAttribute('aria-activedescendant', (await option.getAttribute('id'))!);
  await expect(page.getByRole('status').filter({ hasText: /resultado/ })).toBeAttached();
  await expectAccessible(page);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/c\/Proveedor$/);
  await expect(page.getByRole('dialog', { name: 'Buscar' })).toHaveCount(0);

  // Sin acentos ni mayúsculas, y con flechas.
  box = await palette(page);
  await box.fill('auditoria');
  await expect(page.getByRole('dialog').getByRole('option').first()).toHaveAttribute('aria-selected', 'true');
  await box.fill('usuarios y roles');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowUp');
  await expect(page.getByRole('dialog').getByRole('option', { name: /Usuarios y roles/ })).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/admin\/security$/);

  // Regresión (revisión exploratoria 024, F-02): al llegar la búsqueda de folios la selección regresaba a la primera opción.
  box = await palette(page);
  const searched = page.waitForResponse((r) => r.url().includes('/api/search?q=almacen'));
  await box.fill('almacen');
  const second = page.getByRole('dialog').getByRole('option').nth(1);
  await expect(second).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(second).toHaveAttribute('aria-selected', 'true');
  const chosen = await second.textContent();
  await searched;
  await page.waitForTimeout(300); // que React pinte los folios recibidos
  await expect(page.getByRole('dialog').getByRole('option', { selected: true })).toHaveText(chosen ?? '');
  await page.keyboard.press('Escape');

  // Esc cierra y devuelve el foco al botón que la abrió.
  await banner(page).getByRole('button', { name: 'Búsqueda global' }).click();
  await expect(page.getByRole('combobox', { name: /Buscar/ })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(banner(page).getByRole('button', { name: 'Búsqueda global' })).toBeFocused();
});

test('H5 CA2: documentos por folio con la seguridad del servidor', async ({ page, browser }) => {
  const admin = await as('admin');
  const req = (await (await admin.get('/api/data/Requisicion', { params: { pageSize: 1 } })).json()).items[0];
  const folio = String(req.Folio);
  const term = folio.slice(-4);
  const hits = await (await admin.get('/api/search', { params: { q: term } })).json();
  expect(hits).toEqual(expect.arrayContaining([expect.objectContaining({ entity: 'Requisicion', id: req.id, label: folio })]));
  expect((await admin.get('/api/search', { params: { q: 'x' } })).status()).toBe(400);
  expect((await admin.get('/api/search', { params: { q: 'x'.repeat(51) } })).status()).toBe(400);
  // Quien no lee requisiciones no las encuentra (ni por API ni en la paleta).
  const ajenos = await (await (await as('lector')).get('/api/search', { params: { q: term } })).json();
  expect(ajenos.filter((h: { entity: string }) => h.entity === 'Requisicion')).toEqual([]);

  await page.goto('/');
  const box = await palette(page);
  await box.fill(folio);
  const option = page.getByRole('dialog').getByRole('option', { name: new RegExp(`${folio}.*Requisición`) });
  await expect(option).toBeVisible();
  await expect(page.getByRole('group', { name: 'Documentos' })).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(new RegExp(`/c/Requisicion/${req.id}$`));

  const other = await pageAs(browser, 'lector');
  const otherBox = await palette(other);
  await otherBox.fill(folio);
  await expect(other.getByRole('status').filter({ hasText: 'Sin resultados' })).toBeAttached();
  await expect(other.getByRole('dialog').getByRole('option')).toHaveCount(0);
  await other.context().close();
});

test('H6: las rutas existentes siguen funcionando', async ({ page }) => {
  for (const [path, heading] of [['/c/Proveedor', 'Proveedores'], ['/admin/audit', 'Auditoría'], ['/reports', 'Reportes'], ['/dashboards', 'Tableros'],
    ['/reports/Requisicion/PorEstado', 'Requisiciones por estado'], ['/admin/modules', 'Módulos']] as const) {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1, name: heading }), path).toBeVisible();
  }
});

test('H6: en móvil (390 px) el inicio, el menú del módulo y la paleta caben sin desplazamiento horizontal', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(await overflow()).toBeLessThanOrEqual(0);
  await page.getByRole('main').getByRole('list').getByRole('link').filter({ hasText: /^Compras/ }).click();
  await banner(page).getByRole('button', { name: 'Expandir menú' }).click();
  const link = nav(page).getByRole('link', { name: 'Proveedores' });
  // El panel flotante queda encima del contenido: una persona puede tocar el enlace.
  await expect.poll(() => link.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  })).toBe(true);
  await link.click();
  await expect(page).toHaveURL(/\/c\/Proveedor$/);
  await banner(page).getByRole('button', { name: 'Búsqueda global' }).click();
  await page.getByRole('combobox', { name: /Buscar/ }).fill('inv');
  await expect(page.getByRole('dialog').getByRole('option').first()).toBeVisible();
  expect(await overflow()).toBeLessThanOrEqual(0);
  await page.keyboard.press('Escape');
});

for (const scheme of ['light', 'dark'] as const) {
  test(`visual: selector de módulos y área de configuración (${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    await page.goto('/m/Compras');
    await page.goto('/');
    await expect(page.getByRole('main').getByRole('list').getByRole('link').first()).toBeVisible();
    // El saludo depende de la hora: se tapa para que la captura no cambie durante el día.
    await expect(page).toHaveScreenshot(`inicio-${scheme}.png`, { mask: [page.getByRole('heading', { level: 1 })] });
    await page.goto('/admin');
    await expect(page.getByRole('heading', { level: 1, name: 'Configuración' })).toBeVisible();
    await expect(page).toHaveScreenshot(`configuracion-${scheme}.png`);
  });
}
