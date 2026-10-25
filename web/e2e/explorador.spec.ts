// Spec 025: explorador del modelo y de la base de datos — diagrama ER físico con deriva, relaciones, ficha, ¿dónde se usa?,
// automatizaciones, seguridad y navegación. Solo lectura y solo para quien administra el modelo.
// Corre después de compras (orden alfabético): Compras ya está instalado.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import type { APIRequestContext, Browser, Locator, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test, uiLogin } from './helpers';

const PWD = 'frase larga del explorador 2026';
const users = { modelador: 'e2e-exp-modelador', lector: 'e2e-exp-lector' } as const;
type Who = 'admin' | keyof typeof users;
const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
const APP = 'ExpVentas';
const BIG = 'ExpMasivo';
const BIG_COUNT = 105;
const big = (i: number) => `ExpT${String(i).padStart(3, '0')}`;

async function pageAs(browser: Browser, w: keyof typeof users, width = 1280): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined, viewport: { width, height: 800 } });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

/** SQL directo contra la base desechable de la suite (solo para provocar deriva, como lo haría un DBA). */
function sql(query: string) {
  const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';
  const db = process.env.E2E_DB ?? 'ErpAi_E2E';
  if (!db.endsWith('_E2E')) throw new Error('Solo contra la base desechable.');
  execFileSync('sqlcmd', ['-S', server, '-E', '-b', '-I', '-d', db, '-Q', query], { stdio: 'pipe' });
}

const diagram = (page: Page, name = /Diagrama entidad-relación/) => page.getByRole('group', { name });
const panel = (page: Page, name: string | RegExp) => page.getByRole('complementary', { name });
/** Un interruptor se acciona con un clic en su etiqueta, como lo hace una persona (el input está oculto bajo la pista). */
async function toggle(scope: Page | Locator, name: string, on: boolean) {
  const sw = scope.getByRole('switch', { name });
  if ((await sw.isChecked()) !== on) await scope.locator('label.switch').filter({ hasText: name }).click();
  await expect(sw).toBeChecked({ checked: on });
}

async function openDb(page: Page, app = 'Exploración') {
  await page.goto('/admin/explorer?vista=bd');
  await page.getByLabel('Módulo', { exact: true }).selectOption({ label: app });
  await expect(page.getByText(/^\d+ tablas?/)).toBeVisible();
}

test('preparar roles, usuarios y un módulo con catálogos, partidas, proceso, reglas y automatizaciones', async () => {
  const admin = await as('admin');
  const body = (name: string, data: object) => ({ name, description: 'E2E spec 025', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [], ...data });
  const role = async (name: string, data: object) => {
    const res = await admin.post('/api/admin/roles', { data: body(name, data) });
    expect(res.status(), await res.text()).toBe(200);
    return (await res.json()).id as number;
  };
  // Los permisos van después del plan: los catálogos aún no existen; el plan sí necesita el rol (tarea y seguridad).
  const ventas = await role('Ventas exp', {});
  await role('Gerente exp', { inheritsRoleIds: [ventas] });
  const modeler = await role('Modelador exp', { canManageModel: true });
  for (const [u, roleIds] of [[users.modelador, [modeler]], [users.lector, [ventas]]] as const) {
    const res = await admin.post('/api/admin/users', { data: { username: u, displayName: u, password: PWD, roleIds } });
    expect(res.status(), await res.text()).toBe(201);
  }

  await applyPlan(admin, { description: 'Explorador (spec 025)', operations: [
    { op: 'setApp', app: { name: APP, label: 'Exploración', icon: 'network', order: 90, description: 'Catálogos de prueba del explorador.' } },
    { op: 'createEntity', group: APP, name: 'ExpZona', label: 'Zona', labelPlural: 'Zonas', fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true, unique: true }] },
    { op: 'createEntity', group: APP, name: 'ExpCliente', label: 'Cliente exp', labelPlural: 'Clientes exp', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true, unique: true },
      { name: 'Credito', label: 'Crédito', type: 'decimal' },
      { name: 'Zona', label: 'Zona', type: 'choice', options: ['Norte', 'Sur'] },
      { name: 'ZonaRef', label: 'Zona (catálogo)', type: 'reference', referenceEntity: 'ExpZona' },
      { name: 'Vendedor', label: 'Vendedor', type: 'user' },
      { name: 'Viejo', label: 'Dato viejo', type: 'text' },
    ] },
    { op: 'createEntity', group: APP, name: 'ExpPedido', label: 'Pedido exp', labelPlural: 'Pedidos exp', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'EP-' },
      { name: 'Cliente', label: 'Cliente', type: 'reference', referenceEntity: 'ExpCliente', required: true },
      { name: 'Fecha', label: 'Fecha', type: 'date' },
    ] },
    { op: 'createEntity', name: 'ExpPedidoLinea', label: 'Partida exp', labelPlural: 'Partidas exp', fields: [
      { name: 'Pedido', label: 'Pedido', type: 'parent', referenceEntity: 'ExpPedido' },
      { name: 'Concepto', label: 'Concepto', type: 'text', required: true },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal', required: true },
      { name: 'Precio', label: 'Precio', type: 'decimal' },
      { name: 'Importe', label: 'Importe', type: 'formula', resultType: 'decimal', formula: 'Cantidad * Precio' },
      // Spec 023: ruta de tres saltos (partida → pedido → cliente → zona).
      { name: 'ZonaCliente', label: 'Zona del cliente', type: 'formula', resultType: 'text', formula: 'Pedido.Cliente.ZonaRef.Nombre' },
    ] },
    { op: 'addField', entity: 'ExpPedido', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'ExpPedidoLinea' } },
    { op: 'addField', entity: 'ExpPedido', field: { name: 'Total', label: 'Total', type: 'formula', resultType: 'decimal', formula: 'SUM(Partidas.Importe)' } },
    { op: 'setRules', entity: 'ExpPedido', rules: [
      { name: 'LimiteCredito', field: 'Total', assert: 'Total <= Cliente.Credito', message: 'Rebasa el crédito del cliente.', severity: 'warning' },
    ] },
    { op: 'setWorkflow', entity: 'ExpPedido', workflow: {
      initial: 'Borrador', states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Confirmado', label: 'Confirmado', color: 'success', editable: false }],
      transitions: [{ name: 'confirmar', label: 'Confirmar', from: ['Borrador'], to: 'Confirmado', when: "Cliente.Zona = 'Norte'" }],
    } },
    { op: 'setAutomations', entity: 'ExpPedido', automations: [
      { name: 'MarcarCliente', label: 'Marcar al cliente', on: 'enterState', state: 'Confirmado',
        actions: [{ type: 'update', target: 'Cliente', set: [{ field: 'Zona', value: "'Norte'" }] }] },
      { name: 'Recalcular', label: 'Recalcular el crédito', on: 'create', mode: 'async',
        actions: [{ type: 'update', target: 'Cliente', set: [{ field: 'Credito', value: 'Cliente.Credito' }] }] },
    ] },
    { op: 'setSchedules', entity: 'ExpCliente', schedules: [
      { name: 'Revisar', label: 'Revisar el crédito', every: 'day', at: '06:00', where: 'Credito > 0', role: 'Ventas exp',
        actions: [{ type: 'update', set: [{ field: 'Credito', value: 'Credito' }] }] },
    ] },
    { op: 'setSecurity', entity: 'ExpCliente', security: {
      fields: [{ field: 'Credito', read: ['Gerente exp'] }],
      records: [{ roles: ['Ventas exp'], scope: 'own' }],
    } },
  ] });
  const grant = await admin.put(`/api/admin/roles/${ventas}`, { data: body('Ventas exp', { permissions: [
    { entity: 'ExpCliente', canRead: true, canCreate: false, canUpdate: false, canDelete: false },
    { entity: 'ExpPedido', canRead: true, canCreate: true, canUpdate: true, canDelete: false },
  ] }) });
  expect(grant.status(), await grant.text()).toBe(200);
  // Un campo archivado conserva su columna (constitución II.9): es deriva esperada.
  await applyPlan(admin, { description: 'Archivar un campo (spec 025)', operations: [{ op: 'archiveField', entity: 'ExpCliente', field: 'Viejo' }] });
});

test('H7 CA1: sin «administrar modelo» no hay explorador: ni en configuración, ni por URL, ni por API', async ({ browser }) => {
  const lector = await as('lector');
  for (const path of ['/api/explorer/database', '/api/explorer/relations', '/api/explorer/entities/ExpPedido', '/api/explorer/usages?entity=ExpPedido',
    '/api/explorer/automations', '/api/explorer/security', '/api/explorer/navigation', '/api/explorer/database/ddl'])
    expect((await lector.get(path)).status(), path).toBe(403);
  const page = await pageAs(browser, 'lector');
  await page.goto('/admin/explorer?vista=bd');
  await expect(page.getByText('Sin acceso', { exact: true })).toBeVisible();
  // En la pantalla del catálogo tampoco aparece el enlace al explorador.
  await page.goto('/c/ExpPedido');
  await expect(page.getByRole('heading', { name: 'Pedidos exp' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Ver en el explorador' })).toHaveCount(0);
  await page.context().close();
});

test('H7 CA2 · H1 CA6: el esquema sale de una lista blanca y meta es solo del superusuario', async ({ browser }) => {
  const modelador = await as('modelador');
  expect((await modelador.get('/api/explorer/database?schema=meta')).status()).toBe(403);
  expect((await modelador.get('/api/explorer/database?schema=dbo')).status()).toBe(400);
  expect((await modelador.get(`/api/explorer/database?schema=${encodeURIComponent("app' OR 1=1--")}`)).status()).toBe(400);
  expect((await modelador.get(`/api/explorer/database?q=${'x'.repeat(101)}`)).status()).toBe(400);
  const app = await (await modelador.get('/api/explorer/database')).json();
  expect(app.tables.every((t: { schema: string }) => t.schema === 'app')).toBe(true);
  const meta = await (await (await as('admin')).get('/api/explorer/database?schema=meta')).json();
  expect(meta.tables.map((t: { name: string }) => t.name)).toEqual(expect.arrayContaining(['Entity', 'Field', 'User', 'Role']));

  // El modelador ve el explorador en configuración, sin el selector de esquema.
  const page = await pageAs(browser, 'modelador');
  await page.goto('/admin');
  await page.getByRole('main').getByRole('link', { name: /Explorador/ }).click();
  await expect(page.getByRole('heading', { name: 'Explorador del modelo' })).toBeVisible();
  await expect(page.getByLabel('Módulo', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Esquema', { exact: true })).toHaveCount(0);
  await page.context().close();
});

test('H1 CA1 · CA2 · CA5: cada tabla con sus columnas, tipos SQL exactos, llaves, índices y su catálogo', async ({ page }) => {
  await openDb(page);
  await diagram(page).getByRole('button', { name: /^Tabla app\.ExpPedido,/ }).click();
  const detail = panel(page, 'Detalle de app.ExpPedido');
  await expect(detail).toBeVisible();
  await expect(detail.getByText(/Catálogo\s+Pedido exp/)).toBeVisible();
  const cols = detail.getByRole('table', { name: 'Columnas de ExpPedido' });
  const row = (c: string) => cols.getByRole('row').filter({ has: page.getByRole('cell', { name: new RegExp(`(^|\\s)${c}(\\s|$)`) }).first() });
  await expect(row('Folio')).toContainText('nvarchar(30)');
  await expect(row('Cliente')).toContainText('bigint');
  await expect(row('Total')).toContainText('decimal(18,2)');
  await expect(row('Fecha')).toContainText('date');
  await expect(row('RowVer')).toContainText('rowversion');
  await expect(row('CreatedAt')).toContainText('datetime2(3)');
  await expect(row('CreatedAt')).toContainText('(sysutcdatetime())');
  await expect(row('IsDeleted')).toContainText('Sistema');
  await expect(row('Id')).toContainText('identidad');
  // Las partidas no tienen columna: viven en su tabla.
  await expect(cols.getByRole('cell', { name: 'Partidas', exact: true })).toHaveCount(0);
  await expect(detail.getByText('PK_ExpPedido (Id)')).toBeVisible();
  await expect(detail.getByText('Cliente → app.ExpCliente (Id)')).toBeVisible();
  await expect(detail.getByText('UX_ExpPedido_Folio')).toBeVisible();
  await expect(detail.getByText(/\(\[Folio\] IS NOT NULL AND \[IsDeleted\]=\(0\)\)/)).toBeVisible();
  await expect(detail.getByRole('link', { name: /Abrir en el modelador/ })).toHaveAttribute('href', '/admin/model?edit=ExpPedido');
  await expect(detail.getByRole('link', { name: /Ver pantalla/ })).toHaveAttribute('href', '/c/ExpPedido');
  // La llave a meta.User (campo usuario) se dibuja como tabla externa.
  await diagram(page).getByRole('button', { name: /^Tabla app\.ExpCliente,/ }).click();
  await expect(diagram(page).getByRole('button', { name: 'Tabla externa meta.User' })).toBeVisible();
  await expectAccessible(page);
});

test('H1 CA3: pata de gallo y cardinalidad según la nulabilidad y unicidad de la llave', async ({ page }) => {
  await openDb(page);
  await diagram(page).getByRole('button', { name: /^Tabla app\.ExpPedidoLinea,/ }).click();
  const detail = panel(page, 'Detalle de app.ExpPedidoLinea');
  await expect(detail.getByText('Cada ExpPedidoLinea tiene cero o un ExpPedido; cada ExpPedido tiene cero o muchos ExpPedidoLinea.')).toBeVisible();
  // Del lado de la tabla que refiere se dibuja «cero o muchos» (pata de gallo con anillo).
  expect(await diagram(page).locator('circle').count()).toBeGreaterThan(0);
  expect(await diagram(page).locator('line').count()).toBeGreaterThan(0);
});

test('H1 CA4 · CA2: la deriva se lee de SQL Server: campo archivado, columna sin campo y tipo distinto', async ({ page }) => {
  // Como lo haría alguien directo en SQL Server: el explorador lo ve porque lee el catálogo, no los metadatos.
  sql('ALTER TABLE [app].[ExpCliente] ADD [Huerfana] int NULL; ALTER TABLE [app].[ExpCliente] ALTER COLUMN [Credito] decimal(18,4) NULL;');
  await openDb(page);
  const alert = page.getByRole('status').filter({ hasText: 'Deriva entre la base y el modelo' });
  await expect(alert).toContainText('Campo archivado: 1');
  await expect(alert).toContainText('Columna sin campo: 1');
  await expect(alert).toContainText('Tipo distinto: 1');
  await alert.getByRole('button', { name: 'Ver detalle' }).click();
  const drift = page.getByRole('table', { name: 'Deriva entre la base de datos y el modelo' });
  await expect(drift.getByRole('row').filter({ hasText: 'Viejo' })).toContainText('El campo Viejo está archivado; su columna y sus datos se conservan.');
  await expect(drift.getByRole('row').filter({ hasText: 'Huerfana' })).toContainText('no corresponde a ningún campo de ExpCliente');
  await expect(drift.getByRole('row').filter({ hasText: 'Credito' })).toContainText('es decimal(18,4); el motor generaría decimal(18,2)');
  // Solo con deriva: queda la tabla del cliente.
  await toggle(page, 'Solo con deriva', true);
  await page.getByRole('radio', { name: 'Tabla' }).click();
  const list = page.getByRole('table', { name: /alternativa al diagrama/ });
  await expect(list.getByRole('row')).toHaveCount(2);
  await list.getByRole('button', { name: 'app.ExpCliente' }).click();
  await expect(panel(page, 'Detalle de app.ExpCliente').getByText('(el motor generaría decimal(18,2))')).toBeVisible();
});

test('H1 CA7: exporta el diagrama como SVG y PNG y el DDL de las tablas mostradas, sin datos', async ({ page }) => {
  await openDb(page);
  const [svg] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'SVG' }).click()]);
  expect(svg.suggestedFilename()).toBe('diagrama-app.svg');
  const svgText = readFileSync((await svg.path())!, 'utf8');
  expect(svgText).toMatch(/^<\?xml/);
  expect(svgText).toContain('<title>Diagrama ER del esquema app</title>');
  expect(svgText).toContain('app.ExpPedido');
  expect(svgText).not.toContain('class=');
  const [png] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'PNG' }).click()]);
  expect(png.suggestedFilename()).toBe('diagrama-app.png');
  expect(readFileSync((await png.path())!).subarray(1, 4).toString()).toBe('PNG');

  const [ddl] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'DDL' }).click()]);
  expect(ddl.suggestedFilename()).toBe('esquema-app.sql');
  const script = readFileSync((await ddl.path())!, 'utf8');
  expect(script).toContain('CREATE TABLE [app].[ExpPedido] (');
  expect(script).toContain('[Id] bigint IDENTITY(1,1) NOT NULL');
  expect(script).toContain('CONSTRAINT [PK_ExpPedido] PRIMARY KEY ([Id])');
  expect(script).toContain('ADD CONSTRAINT [FK_ExpPedido_Cliente] FOREIGN KEY ([Cliente]) REFERENCES [app].[ExpCliente] ([Id]);');
  expect(script).toMatch(/CREATE UNIQUE INDEX \[UX_ExpPedido_Folio\] ON \[app\]\.\[ExpPedido\] \(\[Folio\]\) WHERE/);
  // Solo las tablas mostradas (el módulo filtrado) y nunca datos.
  expect(script).not.toContain('[app].[OrdenCompra]');
  expect(script).not.toMatch(/\bINSERT\b/);
});

test('H1 CA8: con 100+ tablas se filtra en el servidor, se enfoca a uno o dos niveles y las cajas no se traslapan', async ({ page }) => {
  const admin = await as('admin');
  const ops = [
    { op: 'setApp', app: { name: BIG, label: 'Modelo grande', order: 95 } },
    ...Array.from({ length: BIG_COUNT }, (_, k) => {
      const i = k + 1;
      return { op: 'createEntity', group: BIG, name: big(i), label: big(i), labelPlural: big(i), fields: [
        { name: 'Nombre', label: 'Nombre', type: 'text' },
        // Árbol binario: cada tabla refiere a la de la mitad de su número (como documentos que refieren a catálogos).
        ...(i > 1 ? [{ name: 'Padre', label: 'Padre', type: 'reference', referenceEntity: big(Math.floor(i / 2)) }] : []),
      ] };
    }),
  ];
  // Un plan admite hasta 100 operaciones.
  for (let k = 0; k < ops.length; k += 60) await applyPlan(admin, { description: 'Modelo grande (spec 025)', operations: ops.slice(k, k + 60) });
  const res = await (await as('modelador')).get(`/api/explorer/database?app=${BIG}`);
  expect((await res.json()).tables).toHaveLength(BIG_COUNT);

  await openDb(page, 'Modelo grande');
  await expect(page.getByText(`${BIG_COUNT} tablas`)).toBeVisible();
  const boxes = diagram(page).getByRole('button');
  await expect(boxes).toHaveCount(BIG_COUNT);
  // Regresión del trazado: ninguna caja se encima con otra.
  const rects = await boxes.evaluateAll((els) => els.map((e) => e.getBoundingClientRect()).map((r) => [r.left, r.top, r.right, r.bottom]));
  for (let a = 0; a < rects.length; a++)
    for (let b = a + 1; b < rects.length; b++) {
      const [l1, t1, r1, b1] = rects[a], [l2, t2, r2, b2] = rects[b];
      expect(l1 < r2 - 1 && l2 < r1 - 1 && t1 < b2 - 1 && t2 < b1 - 1, `cajas ${a} y ${b} se traslapan`).toBe(false);
    }
  // Foco en una tabla con sus vecinas a 1 y 2 niveles.
  await page.getByRole('radio', { name: 'Tabla' }).click();
  await page.getByRole('button', { name: `app.${big(50)}`, exact: true }).click();
  await page.getByRole('radio', { name: 'Diagrama' }).click();
  await toggle(page, 'Enfocar la tabla elegida', true);
  // 1 nivel: la 50, la que refiere (25) y las que la refieren (100 y 101); la 12 (que refiere la 25) queda como externa.
  const tables = diagram(page).getByRole('button', { name: /^Tabla app\./ });
  await expect(tables).toHaveCount(4);
  await expect(diagram(page).getByRole('button', { name: `Tabla externa app.${big(12)}` })).toBeVisible();
  await page.getByLabel('Niveles de vecinas').selectOption('2');
  // 2 niveles: además la 12 (de la 25) y la 51 (hermana por la 25).
  await expect(tables).toHaveCount(6);
  // Zoom con botones y con el teclado.
  await page.getByRole('button', { name: 'Ajustar a la pantalla' }).click();
  const before = await page.getByText(/^\d+ %$/).textContent();
  await page.getByRole('button', { name: 'Acercar' }).click();
  await expect(page.getByText(/^\d+ %$/)).not.toHaveText(before!);
  // Regresión (revisión exploratoria 025, F-05): al llegar con una tabla elegida, el diagrama ya viene enfocado en ella.
  await page.goto(`/admin/explorer?vista=bd&catalogo=${big(50)}`);
  await expect(page.getByRole('switch', { name: 'Enfocar la tabla elegida' })).toBeChecked();
  await expect(tables).toHaveCount(4);
  await page.getByLabel('Módulo', { exact: true }).selectOption({ label: 'Modelo grande' });
  // Búsqueda en el servidor por texto.
  await toggle(page, 'Enfocar la tabla elegida', false);
  await page.getByLabel('Buscar tabla o columna').fill(big(77));
  await expect(page.getByText(/^1 tabla · 1 externa$/)).toBeVisible();
});

test('H8: el input oculto de cada interruptor no escapa de su etiqueta ni alarga la página', async ({ page }) => {
  // Regresión (revisión exploratoria 025, F-01): sin `position: relative` en .switch, el input absoluto tomaba como
  // contenedor a un ancestro lejano y alargaba el documento (franja vacía de 74 px al pie del modelador).
  await page.goto('/admin/model?new=1');
  await expect(page.getByRole('switch').first()).toBeAttached();
  const escaped = await page.getByRole('switch').evaluateAll((els) => els.filter((e) => (e as HTMLElement).offsetParent !== e.closest('label')).length);
  expect(escaped).toBe(0);
  expect(await page.evaluate(() => document.documentElement.scrollHeight - innerHeight)).toBeLessThanOrEqual(0);
});

test('H1 CA9: el diagrama se recorre con el teclado y Enter abre el detalle', async ({ page }) => {
  await openDb(page);
  const boxes = diagram(page).getByRole('button');
  // Una sola parada de tabulación en el diagrama.
  await expect(diagram(page).locator('[tabindex="0"]')).toHaveCount(1);
  await boxes.first().focus();
  const first = await page.evaluate(() => document.activeElement?.getAttribute('aria-label'));
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowRight');
  const moved = await page.evaluate(() => document.activeElement?.getAttribute('aria-label'));
  expect(moved).not.toBe(first);
  expect(moved).toMatch(/^Tabla app\./);
  await page.keyboard.press('Enter');
  const name = /^Tabla (app\.\S+),/.exec(moved!)![1];
  await expect(panel(page, `Detalle de ${name}`)).toBeVisible();
  // La alternativa en tabla también se usa solo con teclado.
  await page.getByRole('radio', { name: 'Tabla' }).focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('table', { name: /alternativa al diagrama/ })).toBeVisible();
  await page.getByRole('button', { name: 'app.ExpZona', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(panel(page, 'Detalle de app.ExpZona')).toBeVisible();
});

test('H2 CA1 · CA2 · CA3 · CA4: relaciones con color por módulo, foco y campos con insignias', async ({ page }) => {
  await page.goto('/admin/explorer?vista=relaciones');
  await page.getByLabel('Módulo', { exact: true }).selectOption({ label: 'Exploración' });
  await page.getByLabel('Catálogo en foco').selectOption({ label: 'Pedidos exp' });
  const graph = page.getByRole('group', { name: /Relaciones entre/ });
  await expect(graph.getByRole('button', { name: /^Catálogo Clientes exp/ })).toBeVisible();
  await expect(graph.getByRole('button', { name: /^Catálogo Partidas exp/ })).toBeVisible();
  // A un nivel del pedido no está la zona (está a dos: pedido → cliente → zona).
  await expect(graph.getByRole('button', { name: /^Catálogo Zonas/ })).toHaveCount(0);
  await page.getByLabel('Niveles de vecinos').selectOption('2');
  await expect(graph.getByRole('button', { name: /^Catálogo Zonas/ })).toBeVisible();
  const detail = panel(page, 'Detalle de Pedidos exp');
  await expect(detail.getByRole('listitem').filter({ hasText: /^Cliente/ }).getByText('Obligatorio')).toBeVisible();
  await expect(detail.getByRole('listitem').filter({ hasText: /^Total/ }).getByText('Fórmula', { exact: true })).toBeVisible();
  await expect(page.getByRole('list', { name: 'Colores por módulo' })).toContainText('Exploración');
  await page.getByRole('radio', { name: 'Lista' }).click();
  const list = page.getByRole('table', { name: /alternativa al diagrama/ });
  const pedidos = list.getByRole('row').filter({ has: page.getByRole('button', { name: 'Pedidos exp', exact: true }) });
  await expect(pedidos).toContainText('Clientes exp (Cliente, referencia)');
  await expect(pedidos).toContainText('Partidas exp (Partidas, partidas)');
  await expectAccessible(page);
});

test('H3 CA1 · CA2: la ficha reúne campos, reglas, proceso, automatizaciones, seguridad y tabla física', async ({ page }) => {
  // Desde la pantalla del catálogo.
  await page.goto('/c/ExpPedido');
  await page.getByRole('link', { name: 'Ver en el explorador' }).click();
  await expect(page).toHaveURL(/vista=ficha&catalogo=ExpPedido/);
  await expect(page.getByRole('heading', { name: /Pedidos exp\s+ExpPedido/ })).toBeVisible();
  const summary = page.getByRole('region', { name: 'Resumen del catálogo' });
  await expect(summary).toContainText('app.ExpPedido');
  await expect(summary).toContainText('Exploración');
  await expect(summary).toContainText('Creado en el modelador');
  await expect(page.getByRole('table', { name: 'Campos de Pedidos exp' })).toContainText('SUM(Partidas.Importe)');
  const rules = page.getByRole('region', { name: /^Reglas/ });
  await expect(rules).toContainText('LimiteCredito');
  await expect(rules.getByText('Aviso')).toBeVisible();
  await expect(page.getByRole('img', { name: /Proceso de Pedidos exp: 2 estados y 1 transiciones/ })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Transiciones del proceso' })).toContainText("Cliente.Zona = 'Norte'");
  const autos = page.getByRole('region', { name: /^Automatizaciones/ });
  await expect(autos).toContainText('Marcar al cliente');
  await expect(autos).toContainText('Síncrona');
  await expect(autos).toContainText('Asíncrona');
  const roles = page.getByRole('table', { name: 'Permisos de cada rol en Pedidos exp' });
  await expect(roles.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Ventas exp', exact: true }) })).toContainText('Sí');
  await expect(roles.getByRole('row').filter({ hasText: 'Gerente exp' })).toContainText('hereda de Ventas exp');
  await expect(page.getByRole('link', { name: /Abrir en el modelador/ }).first()).toHaveAttribute('href', '/admin/model?edit=ExpPedido');
  await expectAccessible(page);

  // Un catálogo de un paquete dice qué paquete y versión lo instalaron.
  await page.getByLabel('Catálogo', { exact: true }).selectOption('OrdenCompra');
  await expect(page.getByRole('region', { name: 'Resumen del catálogo' })).toContainText(/Instalado por\s*Compras \d+\.\d+\.\d+/);
});

test('H4 CA1 · CA2 · CA3: ¿dónde se usa? con rutas de tres niveles, reglas, transiciones, tareas y seguridad', async ({ page }) => {
  await page.goto('/admin/explorer?vista=usos&catalogo=ExpZona&campo=Nombre');
  await expect(page.getByText(/^\d+ menci(ón|ones): el campo Nombre de Zonas\.$/)).toBeVisible();
  const formula = page.getByRole('region', { name: /^Fórmula/ });
  await expect(formula).toContainText('Fórmula de Zona del cliente');
  await expect(formula).toContainText('Pedido.Cliente.ZonaRef.Nombre');
  await expect(formula.getByRole('link', { name: /Abrir en el modelador/ })).toHaveAttribute('href', '/admin/model?edit=ExpPedidoLinea');

  await page.getByLabel('Catálogo', { exact: true }).selectOption({ label: 'Clientes exp' });
  await page.getByLabel('Campo', { exact: true }).selectOption('Credito');
  await expect(page.getByRole('region', { name: /^Regla/ })).toContainText('Aviso «LimiteCredito»');
  await expect(page.getByRole('region', { name: /^Tarea programada/ })).toContainText('Tarea «Revisar el crédito»');
  await expect(page.getByRole('region', { name: /^Seguridad/ })).toContainText('Seguridad por campo');
  await expect(page.getByRole('region', { name: /^Automatización/ })).toContainText('Recalcular el crédito');

  await page.getByLabel('Campo', { exact: true }).selectOption('Zona');
  const transition = page.getByRole('region', { name: /^Transición/ });
  await expect(transition).toContainText('Transición «Confirmar» (condición)');
  await expect(transition.getByRole('link', { name: /Abrir en el modelador/ })).toHaveAttribute('href', '/admin/model?edit=ExpPedido&tab=proceso');
  await expectAccessible(page);
});

test('H5 CA1 · CA2 · CA3: el flujo de automatizaciones distingue síncrona, asíncrona y programada', async ({ page }) => {
  await page.goto('/admin/explorer?vista=automatizaciones');
  await expect(page.getByRole('group', { name: /Flujo de automatizaciones/ })).toBeVisible();
  // Regresión (revisión exploratoria 025, F-03): mismos colores y etiquetas de módulo que en Relaciones.
  await expect(page.getByRole('list', { name: 'Colores por módulo' })).toContainText('Exploración');
  await expect(page.getByRole('list', { name: 'Colores por módulo' })).not.toContainText(APP);
  await page.getByRole('radio', { name: 'Lista' }).click();
  const list = page.getByRole('table', { name: /alternativa al diagrama/ });
  const row = (text: string) => list.getByRole('row').filter({ hasText: text });
  await expect(row('Marcar al cliente')).toContainText('Al entrar a Confirmado');
  await expect(row('Marcar al cliente')).toContainText('Síncrona');
  await expect(row('Marcar al cliente')).toContainText('Clientes exp');
  await expect(row('Recalcular el crédito')).toContainText('Asíncrona');
  await expect(row('Revisar el crédito')).toContainText('Programada');
  await expect(row('Revisar el crédito')).toContainText('Cada día a las 06:00');
  // El compilador rechaza los ciclos: con el modelo de la suite no hay ninguno.
  await expect(page.getByRole('alert').filter({ hasText: 'ciclo' })).toHaveCount(0);
  await page.getByLabel('Modo', { exact: true }).selectOption('schedule');
  await expect(row('Revisar el crédito')).toBeVisible();
  for (const r of await list.getByRole('row').all()) if (await r.getByRole('cell').count()) await expect(r).toContainText('Programada');
  await expect(row('Marcar al cliente')).toHaveCount(0);
  await expectAccessible(page);
});

test('H6 CA1 · CA2: matriz de roles con herencia, alcance y campos ocultos, y árbol de navegación con teclado', async ({ page }) => {
  await page.goto('/admin/explorer?vista=seguridad');
  const matrix = page.getByRole('table', { name: 'Permisos de cada rol en cada catálogo' });
  await expect(matrix.getByRole('columnheader', { name: /Gerente exp/ })).toContainText('hereda de Ventas exp');
  // Regresión (revisión exploratoria 025, F-04): el módulo con su etiqueta y sin «ocultos» en roles que no leen el catálogo.
  const clientes = matrix.getByRole('row').filter({ has: page.getByRole('rowheader', { name: /Clientes exp/ }) });
  await expect(clientes.getByRole('rowheader')).toContainText('Exploración');
  await expect(clientes.getByRole('rowheader')).not.toContainText(APP);
  const sinLectura = await clientes.getByRole('cell').filter({ hasText: 'oculto' }).allTextContents();
  expect(sinLectura.every((t) => t.includes('L'))).toBe(true);
  await page.getByLabel('Rol', { exact: true }).selectOption('Ventas exp');
  const byRole = page.getByRole('table', { name: 'Permisos del rol Ventas exp' });
  const cliente = byRole.getByRole('row').filter({ has: page.getByRole('rowheader', { name: 'Clientes exp' }) });
  await expect(cliente).toContainText('leer');
  await expect(cliente).toContainText('escribe propios');
  await expect(cliente).toContainText('Credito');
  await page.getByLabel('Rol', { exact: true }).selectOption('Gerente exp');
  const gerente = page.getByRole('table', { name: 'Permisos del rol Gerente exp' }).getByRole('row').filter({ has: page.getByRole('rowheader', { name: 'Clientes exp' }) });
  await expect(gerente).toContainText('Ventas exp'); // heredado
  await expect(gerente.getByRole('cell').nth(3)).toHaveText('—'); // ve el crédito

  const app = page.getByText('Exploración', { exact: true }).locator('xpath=ancestor::summary');
  await app.focus();
  await page.keyboard.press('Enter');
  const pedidos = page.locator('summary').filter({ hasText: 'Pedidos exp' });
  await pedidos.focus();
  await page.keyboard.press('Enter');
  const screen = pedidos.locator('xpath=..');
  const form = screen.locator('summary').filter({ hasText: 'Formulario (en el orden de los campos)' });
  await form.focus();
  await page.keyboard.press('Enter');
  await expect(screen.getByRole('listitem').filter({ hasText: /^Folio/ })).toBeVisible();
  await expectAccessible(page);
});

test('H8: móvil (390 px) sin desborde horizontal y tema oscuro en la base de datos y la ficha', async ({ browser }) => {
  const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json', viewport: { width: 390, height: 800 }, colorScheme: 'dark' });
  const page = await ctx.newPage();
  await page.addInitScript(() => { try { localStorage.setItem('erp.theme', 'dark'); } catch { /* sin storage */ } });
  for (const url of ['/admin/explorer?vista=bd&catalogo=ExpPedido', '/admin/explorer?vista=ficha&catalogo=ExpPedido', '/admin/explorer?vista=seguridad', '/admin/explorer?vista=usos&catalogo=ExpCliente']) {
    await page.goto(url);
    await expect(page.getByRole('heading', { name: 'Explorador del modelo' })).toBeVisible();
    await page.waitForLoadState('networkidle');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow, url).toBeLessThanOrEqual(0);
  }
  await expectAccessible(page);
  await ctx.close();
});

for (const scheme of ['light', 'dark'] as const) {
  test(`visual: diagrama ER del módulo en tema ${scheme === 'light' ? 'claro' : 'oscuro'}`, async ({ browser }) => {
    const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json', viewport: { width: 1280, height: 800 }, colorScheme: scheme });
    const page = await ctx.newPage();
    await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    await openDb(page);
    await diagram(page).getByRole('button', { name: /^Tabla app\.ExpPedido,/ }).click();
    await expect(panel(page, 'Detalle de app.ExpPedido')).toBeVisible();
    await expect(page).toHaveScreenshot(`explorador-bd-${scheme}.png`);
    await ctx.close();
  });
}

test('limpieza: archivar el modelo grande para no cargar el resto de la suite', async () => {
  const ops = Array.from({ length: BIG_COUNT }, (_, k) => ({ op: 'archiveEntity', entity: big(BIG_COUNT - k) }));
  for (let k = 0; k < ops.length; k += 60) await applyPlan(await as('admin'), { description: 'Archivar el modelo grande (spec 025)', operations: ops.slice(k, k + 60) });
});
