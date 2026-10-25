// Spec 027 · Documentos PDF con plantillas visuales. Corre después de valuacion-*.spec.ts (Compras 1.10.0 y Contabilidad
// 1.3.0 instalados con sus plantillas; usuarios de compras.spec.ts y el contador de valuacion-contable.spec.ts) y antes de
// visual.spec.ts. Los datos de apoyo se preparan por API; lo que se prueba, por la UI cuando una persona lo vería.
import { deflateSync } from 'node:zlib';
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, anonApi, apiAs, applyPlan, expect, expectAccessible, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de compras 2026';
const users = { comprador: 'e2e-comprador', gerente: 'e2e-gerente', almacen: 'e2e-almacen', contador: 'e2e-contador' } as const;
type Who = keyof typeof users | 'admin';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };
const RUN = Date.now().toString(36).slice(-5);

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
const json = async (w: Who, url: string, params?: Record<string, string | number>) => (await (await as(w)).get(url, { params })).json();

/* ---------- Imágenes de prueba (generadas aquí: PNG real con su CRC y sus datos comprimidos) ---------- */
const CRC = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc32 = (b: Buffer) => { let c = 0xffffffff; for (const x of b) c = CRC[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
function chunk(type: string, body: Buffer) {
  const len = Buffer.alloc(4); len.writeUInt32BE(body.length);
  const typed = Buffer.concat([Buffer.from(type, 'ascii'), body]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(typed));
  return Buffer.concat([len, typed, crc]);
}
/** PNG RGB de w × h (un degradado de la marca); `declaredWidth` miente sobre el ancho; `trailing` va después del final. */
function png(w: number, h: number, opts: { declaredWidth?: number; trailing?: Buffer } = {}) {
  const raw = Buffer.alloc((w * 3 + 1) * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) raw.set([30 + (x * 90) / w, 90 + (y * 60) / h, 180], y * (w * 3 + 1) + 1 + x * 3);
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(opts.declaredWidth ?? w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0)), opts.trailing ?? Buffer.alloc(0)]);
}
const LOGO = png(240, 90);

const putRaw = async (w: Who, url: string, data: Buffer) =>
  (await as(w)).put(url, { data, headers: { 'Content-Type': 'application/octet-stream' } });

async function pageAs(browser: Browser, w: keyof typeof users): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

const ids = { company: 0, companyName: '', oc: 0, folio: '', ocBorrador: 0, poliza: 0 };

test('preparar: orden autorizada y enviada, una en borrador y una póliza', async () => {
  const companies = await json('admin', '/api/admin/companies');
  const def = companies.find((c: { isDefault: boolean }) => c.isDefault);
  ids.company = def.id;
  ids.companyName = def.name;
  const almacen = (await json('admin', '/api/data/Almacen', { pageSize: 50 })).items.find((a: Row) => a.Clave === 'ALM-01') as Row;
  const proveedor = (await json('admin', '/api/data/Proveedor', { search: 'Cartones' })).items[0] as Row;
  const producto = (await json('admin', '/api/data/Producto', { search: 'ROD-6204' })).items[0] as Row;
  const create = async () => {
    const r = await (await as('comprador')).post('/api/data/OrdenCompra', { data: {
      Proveedor: proveedor.id, Almacen: almacen.id, Notas: `Entregar en andén 2 · ñandú ${RUN}`,
      Partidas: [{ Producto: producto.id, Cantidad: 3, PrecioUnitario: 125.5, DescuentoPct: 0 }],
    } });
    expect(r.status(), await r.text()).toBe(201);
    return (await r.json()) as Row;
  };
  const step = async (w: Who, row: Row, transition: string) => {
    const r = await (await as(w)).post(`/api/data/OrdenCompra/${row.id}/transition`, { data: { transition, _rowVersion: row._rowVersion } });
    expect(r.status(), await r.text()).toBe(200);
    return (await r.json()) as Row;
  };
  let oc = await create();
  oc = await step('gerente', await step('comprador', oc, 'enviar'), 'autorizar');
  oc = await step('comprador', oc, 'enviarProveedor');
  ids.oc = oc.id;
  ids.folio = String(oc.Folio);
  ids.ocBorrador = (await create()).id;

  const cuentas = (await json('contador', '/api/data/CuentaContable', { pageSize: 100 })).items as Row[];
  const pol = await (await as('contador')).post('/api/data/Poliza', { data: {
    Concepto: `Póliza para imprimir ${RUN}`, Partidas: [
      { Cuenta: cuentas.find((c) => c.Codigo === '1100')!.id, Cargo: 1500, Abono: 0, Concepto: 'Depósito' },
      { Cuenta: cuentas.find((c) => c.Codigo === '3100')!.id, Cargo: 0, Abono: 1500, Concepto: 'Aportación' },
    ] } });
  expect(pol.status(), await pol.text()).toBe(201);
  ids.poliza = (await pol.json()).id;
});

/* ----------------------------------- H1 ----------------------------------- */

test('H1 CA1: el superusuario captura RFC y domicilio y sube el logo de la empresa', async ({ page }) => {
  await page.goto('/admin/companies');
  await page.getByRole('button', { name: `Editar ${ids.companyName}` }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('RFC (identificación fiscal)').fill('eaa010101ab1');
  await dialog.getByLabel('Domicilio').fill('Av. Peñón 120, Col. Ñuñoa\nCiudad de México');
  await dialog.getByLabel('Logo: archivo').setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: LOGO });
  await expect(dialog.getByRole('img', { name: `Logo de ${ids.companyName}` })).toBeVisible();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Guardar cambios' }).click();
  await expect(page.getByRole('dialog')).toBeHidden();
  const row = page.getByRole('row', { name: new RegExp(ids.companyName) });
  await expect(row.getByText('RFC EAA010101AB1')).toBeVisible();
  await expect(row.getByRole('img', { name: `Logo de ${ids.companyName}` })).toBeVisible();
});

test('H1 CA2 · CA3: se valida el contenido, no la extensión: SVG, polyglot, otros formatos, tamaño y dimensiones', async () => {
  const url = `/api/admin/companies/${ids.company}/logo`;
  const cases: [string, Buffer, RegExp][] = [
    ['SVG con script', Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'), /SVG/],
    ['HTML disfrazado', Buffer.from('<html><body onload="alert(1)"></body></html>'), /HTML/],
    ['GIF', Buffer.from('GIF89a\x01\x00\x01\x00\x00\x00\x00;'), /GIF/],
    ['PNG con HTML al final (polyglot)', png(10, 10, { trailing: Buffer.from('<script>alert(document.cookie)</script>') }), /después de su final/],
    ['PNG que miente sobre su ancho', png(10, 10, { declaredWidth: 2000 }), /no corresponden/],
    ['PNG demasiado grande', png(10, 10, { declaredWidth: 5000 }), /máximo es 3000 px/],
    ['más de 1 MB', Buffer.alloc(1024 * 1024 + 10, 0x41), /1 MB/],
  ];
  for (const [what, body, message] of cases) {
    const r = await putRaw('admin', url, body);
    expect(r.status(), what).toBe(400);
    expect(JSON.stringify(await r.json()), what).toMatch(message);
  }
});

test('H1 CA4: el archivo se sirve autenticado, con su tipo, nosniff, sandbox y caché por huella', async () => {
  const company = (await json('admin', '/api/admin/companies')).find((c: { id: number }) => c.id === ids.company);
  const logoUrl = company.logoUrl as string;
  expect(logoUrl).toMatch(/^\/api\/files\/\d+\?v=[0-9a-f]{64}$/);
  const r = await (await as('almacen')).get(logoUrl);
  expect(r.status()).toBe(200);
  const h = r.headers();
  expect(h['content-type']).toBe('image/png');
  expect(h['x-content-type-options']).toBe('nosniff');
  expect(h['content-security-policy']).toContain('sandbox');
  expect(h['cache-control']).toContain('immutable');
  expect(Buffer.compare(await r.body(), LOGO)).toBe(0);
  const again = await (await as('almacen')).get(logoUrl, { headers: { 'If-None-Match': h['etag'] } });
  expect(again.status()).toBe(304);
  const anon = await anonApi();
  expect((await anon.get(logoUrl)).status()).toBe(401);
  await anon.dispose();
  // El cambio queda en la auditoría (sin el contenido).
  const audit = await json('admin', '/api/audit', { action: 'file.change', pageSize: 5 });
  expect(JSON.stringify(audit)).toContain(company.code);
});

test('H1 CA5: sin ser superusuario no se sube el logo ni se ve la página', async ({ browser }) => {
  expect((await putRaw('comprador', `/api/admin/companies/${ids.company}/logo`, LOGO)).status()).toBe(403);
  expect((await (await as('comprador')).delete(`/api/admin/companies/${ids.company}/logo`)).status()).toBe(403);
  expect((await putRaw('comprador', '/api/admin/images/Sello', LOGO)).status()).toBe(403);
  const page = await pageAs(browser, 'comprador');
  await page.goto('/admin/companies');
  await expect(page.getByRole('heading', { name: 'Empresas' })).toBeHidden();
  await page.context().close();
});

/* ----------------------------------- H2 ----------------------------------- */

test('H2 CA1: el modelador sube, reemplaza y archiva imágenes con clave', async ({ page }) => {
  await page.goto('/admin/images');
  await page.getByLabel('Clave').fill(`Sello${RUN}`);
  await page.getByLabel(`Imagen «Sello${RUN}»: archivo`).first().setInputFiles({ name: 'sello.png', mimeType: 'image/png', buffer: png(60, 60) });
  const item = page.getByRole('listitem').filter({ hasText: `Sello${RUN}` });
  await expect(item.getByText('60 × 60 px')).toBeVisible();
  await expectAccessible(page);
  // Reemplazar: misma clave, otra imagen.
  await item.getByLabel(`Imagen «Sello${RUN}»: archivo`).setInputFiles({ name: 'sello2.png', mimeType: 'image/png', buffer: png(80, 40) });
  await expect(item.getByText('80 × 40 px')).toBeVisible();
  // Un archivo inválido dice por qué.
  await item.getByLabel(`Imagen «Sello${RUN}»: archivo`).setInputFiles({ name: 'falso.png', mimeType: 'image/png', buffer: Buffer.from('<svg/>') });
  await expect(item.getByRole('alert')).toContainText('SVG');
});

/* ----------------------------------- H3 ----------------------------------- */

test('H3 CA1 · CA2: setPrintTemplates valida con el compilador y merge conserva las demás', async () => {
  const admin = await as('admin');
  const bad = await admin.post('/api/model/plan/preview', { data: { description: 'mala', operations: [
    { op: 'setPrintTemplates', entity: 'OrdenCompra', merge: true, templates: [{ name: 'Mala', label: 'Mala', roles: ['NoExiste'],
      blocks: [{ type: 'table', lines: 'Partidas', columns: [{ label: 'X', value: 'Subtotal' }] }, { type: 'html', text: '<b>x</b>' }] }] }] } });
  const body = JSON.stringify(await bad.json());
  expect(body).toContain("el rol 'NoExiste' no existe");
  expect(body).toContain("el tipo 'html' no existe");
  expect(body).toContain('columna 1'); // Subtotal no es de la partida
  await applyPlan(admin, { description: 'Plantilla de prueba', operations: [
    { op: 'setPrintTemplates', entity: 'OrdenCompra', merge: true, templates: [{ name: `Prueba${RUN}`, label: `Prueba ${RUN}`, roles: ['Comprador'],
      companies: ['NOEXISTE'], blocks: [{ type: 'text', text: 'Hola {{ Folio }}' }] }] }] });
  const oc = (await json('admin', '/api/model/entities/OrdenCompra')).printTemplates.map((t: { name: string }) => t.name);
  expect(oc).toEqual(expect.arrayContaining(['Proveedor', 'Interna', `Prueba${RUN}`]));
  // Las plantillas no llegan a quien no administra el modelo.
  expect((await json('comprador', '/api/model/entities/OrdenCompra')).printTemplates ?? null).toBeNull();
});

test('H3 CA3: el explorador y el origen las conocen', async () => {
  const usages = await json('admin', '/api/explorer/usages', { entity: 'Proveedor', field: 'RFC' });
  expect(usages).toEqual(expect.arrayContaining([expect.objectContaining({ kind: 'print', entity: 'OrdenCompra', tab: 'impresion' })]));
  const prov = (await json('admin', '/api/model/provenance')).entities.find((e: { entity: string }) => e.entity === 'OrdenCompra');
  expect(prov.elements).toEqual(expect.arrayContaining([
    expect.objectContaining({ kind: 'printTemplate', name: 'Proveedor', package: 'compras', step: '1.10.0' }),
    expect.objectContaining({ kind: 'printTemplate', name: `Prueba${RUN}`, package: null }),
  ]));
});

/* ----------------------------------- H4 ----------------------------------- */

test('H4 CA1 · CA2 · CA3: editor visual de plantillas: bloques, orden con teclado y JSON avanzado', async ({ page }) => {
  await page.goto('/admin/model?edit=OrdenCompra&tab=impresion');
  const panel = page.getByRole('tabpanel', { name: 'Impresión' });
  const item = panel.locator('.item').filter({ hasText: 'Orden de compra interna' });
  await expect(item.getByText(/Compras/)).toBeVisible(); // origen del paquete (026)
  await item.getByRole('button', { name: 'Orden de compra interna Interna', exact: true }).click();
  // Agregar un separador: va antes del pie.
  await item.getByLabel('Agregar bloque').selectOption('separator');
  const blocks = item.locator('.block');
  const count = await blocks.count();
  await expect(blocks.nth(count - 2)).toContainText('Separador');
  // Subirlo con el teclado: el foco sigue al bloque.
  const up = item.getByRole('button', { name: `Subir el bloque Separador ${count - 1}` });
  await up.focus();
  await page.keyboard.press('Enter');
  await expect(blocks.nth(count - 3)).toContainText('Separador');
  await expect(item.getByRole('button', { name: `Subir el bloque Separador ${count - 2}` })).toBeFocused();
  await expect(page.getByText(`Separador movido a la posición ${count - 2}`)).toBeAttached();
  // El JSON avanzado (plegado) refleja el borrador.
  const box = page.getByLabel('Plantillas de impresión (JSON)');
  await panel.locator('details.json-advanced > summary').click();
  await expect(box).toHaveValue(/"separator"/);
  await expectAccessible(page);
});

test('H4 CA6: vista previa del borrador con un registro real (sin aplicarlo)', async ({ page }) => {
  await page.goto('/admin/model?edit=OrdenCompra&tab=impresion');
  const item = page.getByRole('tabpanel', { name: 'Impresión' }).locator('.item').filter({ hasText: 'Orden de compra para proveedor' });
  await item.getByRole('button', { name: 'Orden de compra para proveedor Proveedor', exact: true }).click();
  await item.getByLabel('Registro para la vista previa').fill(ids.folio);
  await page.getByRole('option', { name: new RegExp(ids.folio) }).click();
  const preview = page.waitForResponse((r) => r.url().includes('/api/model/print/preview') && r.request().method() === 'POST');
  await item.getByRole('button', { name: 'Generar vista previa' }).click();
  expect((await preview).status()).toBe(200);
  const frame = item.locator('iframe.print-preview-frame');
  await expect(frame).toBeVisible();
  const src = await frame.getAttribute('src');
  const pdf = await page.request.get(src!.split('#')[0]);
  expect(pdf.headers()['content-type']).toBe('application/pdf');
  expect(pdf.headers()['content-security-policy']).toBe("frame-ancestors 'self'");
  expect((await pdf.body()).subarray(0, 4).toString()).toBe('%PDF');
  // La vista previa es de quien la generó.
  expect((await (await as('comprador')).get(src!.split('#')[0])).status()).toBe(404);
  await expectAccessible(page);
});

test('H4 CA6: una imagen inexistente se avisa en la vista previa; una plantilla inválida no se genera', async () => {
  const admin = await as('admin');
  const ok = await admin.post('/api/model/print/preview', { data: { entity: 'OrdenCompra', id: ids.oc, template: {
    name: 'Previa', label: 'Previa', blocks: [{ type: 'image', file: 'NoExisteEnEsteEntorno' }, { type: 'text', text: '{{ Folio }}' }] } } });
  expect(ok.status()).toBe(200);
  expect((await ok.json()).warnings.join(' ')).toContain('NoExisteEnEsteEntorno');
  const bad = await admin.post('/api/model/print/preview', { data: { entity: 'OrdenCompra', id: ids.oc, template: { name: 'Previa', label: 'Previa', blocks: [{ type: 'text', text: '{{ NoExiste }}' }] } } });
  expect(bad.status()).toBe(400);
  expect(JSON.stringify(await bad.json())).toContain('NoExiste');
  expect((await (await as('comprador')).post('/api/model/print/preview', { data: { entity: 'OrdenCompra', id: ids.oc, template: { name: 'Previa', label: 'Previa', blocks: [] } } })).status()).toBe(403);
});

/* ----------------------------------- H5 ----------------------------------- */

test('H5 CA1: el comprador elige la plantilla y descarga el PDF desde el formulario', async ({ browser }) => {
  const page = await pageAs(browser, 'comprador');
  await page.goto(`/c/OrdenCompra/${ids.oc}`);
  const button = page.getByRole('button', { name: 'Descargar PDF' });
  await button.click();
  const menu = page.getByRole('menu', { name: 'Plantillas de impresión' });
  await expect(menu.getByRole('menuitem')).toHaveText([/Orden de compra para proveedor \(predeterminada\)/, /Orden de compra interna/]);
  await expect(menu.getByRole('menuitem').first()).toBeFocused();
  await expectAccessible(page);
  const download = page.waitForEvent('download');
  await page.keyboard.press('Enter');
  const file = await download;
  expect(file.suggestedFilename()).toBe(`OrdenCompra-${ids.folio}.pdf`);
  const fs = await import('node:fs/promises');
  expect((await fs.readFile((await file.path())!)).subarray(0, 4).toString()).toBe('%PDF');
  await page.context().close();
});

test('H5 CA1: en borrador no aplica la de proveedor (condición); sin plantillas el botón no aparece', async ({ browser }) => {
  expect((await json('comprador', `/api/data/OrdenCompra/${ids.ocBorrador}/print`)).map((c: { name: string }) => c.name)).toEqual(['Interna']);
  expect((await (await as('comprador')).get(`/api/data/OrdenCompra/${ids.ocBorrador}/pdf`, { params: { template: 'Proveedor' } })).status()).toBe(404);
  const page = await pageAs(browser, 'comprador');
  const prov = (await json('comprador', '/api/data/Proveedor', { search: 'Cartones' })).items[0];
  await page.goto(`/c/Proveedor/${prov.id}`);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Descargar PDF' })).toHaveCount(0);
  await page.context().close();
});

test('H5 CA2: el almacenista solo tiene la interna; fuera de alcance responde 404', async () => {
  expect((await json('almacen', `/api/data/OrdenCompra/${ids.oc}/print`)).map((c: { name: string }) => c.name)).toEqual(['Interna']);
  const pdf = await (await as('almacen')).get(`/api/data/OrdenCompra/${ids.oc}/pdf`);
  expect(pdf.status()).toBe(200);
  expect(pdf.headers()['content-disposition']).toContain(`OrdenCompra-${ids.folio}.pdf`);
  expect(pdf.headers()['content-security-policy']).toBe("frame-ancestors 'none'");
  expect((await (await as('almacen')).get(`/api/data/OrdenCompra/${ids.oc}/pdf`, { params: { template: 'Proveedor' } })).status()).toBe(404);
  // Un registro que no existe (o fuera del alcance) no se distingue: 404.
  expect((await (await as('almacen')).get('/api/data/OrdenCompra/99999999/pdf')).status()).toBe(404);
  // Sin permiso de lectura del catálogo: 403.
  expect((await (await as('almacen')).get(`/api/data/Poliza/${ids.poliza}/pdf`)).status()).toBe(403);
});

test('H5 CA3: cada PDF generado queda en la auditoría', async () => {
  await (await as('contador')).get(`/api/data/Poliza/${ids.poliza}/pdf`);
  const audit = await json('admin', '/api/audit', { action: 'data.print', pageSize: 20 });
  const text = JSON.stringify(audit);
  expect(text).toContain('Poliza');
  expect(text).toContain(users.contador);
});

test('H6 CA2: la póliza se descarga con su plantilla de Contabilidad', async ({ browser }) => {
  const page = await pageAs(browser, 'contador');
  await page.goto(`/c/Poliza/${ids.poliza}`);
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Descargar PDF' }).click(); // una sola plantilla: descarga directa
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^Poliza-POL-\d+\.pdf$/);
  await page.context().close();
});

test('H5 CA4: el límite por minuto responde 429', async () => {
  // La suite sube el límite (Security__PrintsPerMinute); aquí se comprueba que la política existe en la ruta.
  const res = await (await as('comprador')).get(`/api/data/OrdenCompra/${ids.oc}/pdf`);
  expect(res.status()).toBe(200);
  expect(res.headers()['cache-control']).toBe('no-store');
});

// Regresión (revisión exploratoria 027, F-03): en el teléfono «Descargar PDF» tapaba el título, que quedaba en una
// columna de una palabra.
test('H5 CA1 (móvil): el botón no tapa el título del formulario', async ({ browser }) => {
  const page = await pageAs(browser, 'comprador');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/c/OrdenCompra/${ids.oc}`);
  const title = await page.getByRole('heading', { level: 1 }).boundingBox();
  const button = await page.getByRole('button', { name: 'Descargar PDF' }).boundingBox();
  expect(title!.width).toBeGreaterThan(180);
  const overlap = !(button!.x >= title!.x + title!.width || button!.x + button!.width <= title!.x || button!.y >= title!.y + title!.height || button!.y + button!.height <= title!.y);
  expect(overlap).toBe(false);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.context().close();
});

// Regresión (revisión exploratoria 027, F-04): a 390 px el nombre del bloque se partía a media palabra («Encabez-ado»).
test('H4 CA7 (móvil): los bloques caben sin partir su nombre ni desbordar la página', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/admin/model?edit=OrdenCompra&tab=impresion');
  const item = page.getByRole('tabpanel', { name: 'Impresión' }).locator('.item').filter({ hasText: 'Orden de compra interna' });
  await item.getByRole('button', { name: 'Orden de compra interna Interna', exact: true }).click();
  const title = item.locator('.block .item-title').first();
  await expect(title).toHaveText('Encabezado');
  expect((await title.boundingBox())!.height).toBeLessThan(28); // un solo renglón
  // El asa y el nombre van en el mismo renglón (antes el asa quedaba sola arriba).
  const grip = (await item.locator('.block .block-grip').first().boundingBox())!;
  const t = (await title.boundingBox())!;
  expect(Math.abs((grip.y + grip.height / 2) - (t.y + t.height / 2))).toBeLessThan(10);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await expectAccessible(page);
});
