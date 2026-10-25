// Spec 029 · H1, H5 CA3 y H6 CA2: capacidad declarada por ruta. Recorre el inventario versionado (docs/sdd/rutas.md, que
// genera RouteInventoryTests de los metadatos de cada ruta) y comprueba que cada ruta cumple lo declarado: sin sesión, 401;
// sin la capacidad, 403 (antes de leer el cuerpo). Corre antes de compras.spec.ts: Compras aún no está instalado.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, anonApi, apiAs, applyPlan, expect, test } from './helpers';

const HERE = path.dirname(fileURLToPath(import.meta.url));

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de capacidades 029';
const BASICO = 'e2e-cp-basico';
const ROLE = 'CP Básico';

let adminApi: APIRequestContext | undefined;
let basicoApi: APIRequestContext | undefined;
const admin = async () => (adminApi ??= await apiAs(ADMIN.username, ADMIN.password));
const basico = async () => (basicoApi ??= await apiAs(BASICO, PWD));

type InventoryRoute = { method: string; path: string; capability: string };
const inventory = (): InventoryRoute[] =>
  fs.readFileSync(path.join(HERE, '..', '..', 'docs', 'sdd', 'rutas.md'), 'utf8').split('\n')
    .filter((l) => l.startsWith('| ') && l.includes('`'))
    .map((l) => l.split('|').map((c) => c.trim()))
    .map((c) => ({ method: c[1], path: c[2].replace(/`/g, ''), capability: c[3] }));

/** Un valor cualquiera por parámetro: la capacidad se comprueba antes de buscar nada. */
const concrete = (template: string) => template
  .replace('{entity}', 'Inexistente').replace(/\{id:(int|long)\}/g, '1').replace('{name}', 'x').replace('{key}', 'x')
  .replace('{token}', 'x').replace('{segment}', 'x').replace('{**rest}', 'x');
const send = (api: APIRequestContext, r: InventoryRoute) =>
  api.fetch(concrete(r.path), { method: r.method, ...(r.method === 'GET' ? {} : { data: {} }) });
const PUBLIC = ['anónima'];
const ANY_SESSION = ['sesión', 'registros'];

test('preparar: un usuario con sesión y sin ninguna capacidad administrativa', async () => {
  const a = await admin();
  const roles = (await (await a.get('/api/admin/roles')).json()) as { id: number; name: string }[];
  let role = roles.find((r) => r.name === ROLE)?.id;
  if (!role) {
    const r = await a.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 029', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] } });
    expect(r.status(), await r.text()).toBe(200);
    role = (await r.json()).id;
  }
  const users = (await (await a.get('/api/admin/users')).json()) as { username: string }[];
  if (!users.some((u) => u.username === BASICO)) {
    const u = await a.post('/api/admin/users', { data: { username: BASICO, displayName: 'Básico capacidades', password: PWD, roleIds: [role] } });
    expect(u.status(), await u.text()).toBe(201);
  }
});

test('H1 CA4: cada ruta que exige una capacidad responde 403 a quien no la tiene (también con cuerpo inválido)', async () => {
  const b = await basico();
  const routes = inventory().filter((r) => r.method !== '*' && !r.capability.split(' o ').some((c) => [...PUBLIC, ...ANY_SESSION].includes(c)));
  expect(routes.length).toBeGreaterThan(80);
  const wrong: string[] = [];
  for (const r of routes) {
    const res = await send(b, r);
    if (res.status() !== 403) wrong.push(`${r.method} ${r.path} → ${res.status()} (${r.capability})`);
  }
  expect(wrong).toEqual([]);
});

test('H1 CA4: sin sesión, toda ruta fuera de la lista blanca responde 401', async () => {
  const anon = await anonApi();
  const wrong: string[] = [];
  for (const r of inventory().filter((x) => x.method !== '*' && !PUBLIC.includes(x.capability))) {
    const res = await send(anon, r);
    if (res.status() !== 401) wrong.push(`${r.method} ${r.path} → ${res.status()}`);
  }
  expect(wrong).toEqual([]);
  await anon.dispose();
});

test('H1 CA5: las imágenes del entorno solo las sirve a quien administra el modelo', async () => {
  const b = await basico();
  // Regresión (auditoría 4): cualquier sesión leía cualquier imagen por su id secuencial.
  for (const id of [1, 2, 3]) expect((await b.get(`/api/files/${id}`)).status()).toBe(403);
});

test('H5 CA3: la búsqueda global tiene límite por usuario (429)', async () => {
  const b = await basico();
  const statuses: number[] = [];
  for (let i = 0; i < 130; i++) statuses.push((await b.get('/api/search', { params: { q: `x${i}` } })).status());
  expect(statuses[0]).toBe(200);
  // La suite fija Security__SearchesPerMinute en 120 (en producción, 60).
  expect(statuses).toContain(429);
});

test('H6 CA2: instalar un módulo exige la huella de su vista previa y la rechaza si el modelo cambió', async () => {
  const a = await admin();
  const mods = (await (await a.get('/api/admin/modules')).json()) as { id: string; installed: boolean }[];
  test.skip(mods.find((m) => m.id === 'compras')!.installed, 'Compras ya está instalado (otro archivo corrió antes)');
  const preview = await (await a.post('/api/admin/modules/compras/preview')).json();
  expect(preview.baseHash).toMatch(/^[0-9a-f]{64}$/);
  // Regresión (auditoría 4 D-04): sin huella o con otra, no se instala.
  const sin = await a.post('/api/admin/modules/compras/install');
  expect(sin.status(), await sin.text()).toBe(400);
  expect(await sin.text()).toContain('Falta la huella');
  const otra = await a.post('/api/admin/modules/compras/install', { data: { baseHash: '0'.repeat(64) } });
  expect(otra.status()).toBe(409);
  // El modelo cambia entre la vista previa y la aplicación: la huella revisada ya no vale.
  await applyPlan(a, { description: 'E2E 029: cambio entre revisar y aplicar', operations: [
    { op: 'createEntity', group: 'Pruebas 029', name: 'CpIntermedio', label: 'Intermedio 029', labelPlural: 'Intermedios 029',
      fields: [{ name: 'Nombre', label: 'Nombre', type: 'text' }] }] });
  try {
    const stale = await a.post('/api/admin/modules/compras/install', { data: { baseHash: preview.baseHash } });
    expect(stale.status(), await stale.text()).toBe(409);
    expect(await stale.text()).toContain('vuelve a revisarla');
  } finally {
    await applyPlan(a, { description: 'E2E 029: limpieza', operations: [{ op: 'archiveEntity', entity: 'CpIntermedio' }] });
  }
  expect((await (await a.get('/api/admin/modules')).json()).find((m: { id: string }) => m.id === 'compras').installed).toBe(false);
  // La misma vista previa, otra vez, da otra huella (el modelo cambió) y con ella el módulo sí se podría instalar.
  expect((await (await a.post('/api/admin/modules/compras/preview')).json()).baseHash).not.toBe(preview.baseHash);
});

// Regresión (revisión exploratoria 029, F-01): con la vista previa ya vieja, «Instalar» respondía 409 y había que cerrar y
// volver a abrir el diálogo para revisarla otra vez; ahora se vuelve a calcular en el mismo diálogo.
test('H6 CA2 (UI): si el modelo cambió, el diálogo avisa y vuelve a calcular la vista previa sin instalar', async ({ page }) => {
  const a = await admin();
  const mods = (await (await a.get('/api/admin/modules')).json()) as { id: string; installed: boolean }[];
  test.skip(mods.find((m) => m.id === 'compras')!.installed, 'Compras ya está instalado (otro archivo corrió antes)');
  let previews = 0;
  page.on('request', (r) => { if (r.url().endsWith('/api/admin/modules/compras/preview')) previews++; });
  await page.goto('/admin/modules');
  await page.getByRole('button', { name: 'Instalar Compras' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  expect(previews).toBe(1);
  await applyPlan(a, { description: 'E2E 029: cambio con el diálogo abierto', operations: [
    { op: 'createEntity', group: 'Pruebas 029', name: 'CpIntermedio2', label: 'Intermedio 029 (2)', labelPlural: 'Intermedios 029 (2)',
      fields: [{ name: 'Nombre', label: 'Nombre', type: 'text' }] }] });
  try {
    await dialog.getByRole('button', { name: 'Instalar módulo' }).click();
    await expect(dialog.getByText(/cambiaron desde la vista previa/)).toBeVisible();
    await expect.poll(() => previews).toBe(2);
    await dialog.getByRole('button', { name: 'Cancelar' }).click();
  } finally {
    await applyPlan(a, { description: 'E2E 029: limpieza', operations: [{ op: 'archiveEntity', entity: 'CpIntermedio2' }] });
  }
  expect((await (await a.get('/api/admin/modules')).json()).find((m: { id: string }) => m.id === 'compras').installed).toBe(false);
});

// Spec 030: de api-security.spec.ts (sus demás pruebas pasaron a tests/Erp.IntegrationTests/ApiSecurityTests.cs). La
// cabecera Server la quita Kestrel (AddServerHeader = false), que la integración no usa (TestServer): se prueba aquí.
test('spec 001: la API no anuncia el servidor', async () => {
  const api = await anonApi();
  const res = await api.get('/api/health');
  expect(res.status()).toBe(200);
  expect(res.headers()['server']).toBeUndefined();
  await api.dispose();
});
