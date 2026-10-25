// Spec 029 · H2-H4: canario de fugas. Un valor único (CANARIO-<guid>) vive donde el usuario «CN Lector» no debe llegar: un
// campo oculto para su rol, un registro fuera de su alcance, ese mismo registro detrás de uno visible (2 y 3 saltos) y un
// registro de otra empresa. Luego se recorren TODAS las rutas GET del inventario (docs/sdd/rutas.md) y los POST que
// devuelven datos, como ese usuario, y ninguna respuesta puede contener el canario (JSON, CSV, XLSX ni el texto del PDF).
// Spec 031 (auditoría 5): un parámetro de ruta sin resolver hace fallar el recorrido, las rutas que no son GET salen del
// inventario y lo que se le dice al lector en la importación es lo mismo que en /compute (el bit, no solo la cadena).
// Autocontenido: crea sus catálogos (grupo «Pruebas 029»), su empresa y su usuario, y los archiva al terminar.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, test } from './helpers';

const HERE = path.dirname(fileURLToPath(import.meta.url));

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 029';
const PWD = 'frase larga del canario 029';
const LECTOR = 'e2e-cn-lector';
const ROLE = 'CN Lector';
const COMPANY = { code: 'CN029', name: 'Empresa canario 029', baseCurrency: 'MXN' };
const AVISO = 'Aviso del canario: el dato secreto está vacío.';
const AVISO_OCULTO = 'Aviso del canario: el campo oculto tiene valor.';
const ENTITIES = ['CnDoc', 'CnPuente2', 'CnPuente', 'CnSecreto', 'CnEmpresa'] as const;

// El canario se guarda en un archivo de la corrida (el proceso se reinicia tras una falla con E2E_INDEPENDENT).
const STATE = path.join(HERE, '.results', 'canario-029.json');
type State = { canary: string; ids: Record<string, number> };
const load = (): State | null => { try { return JSON.parse(fs.readFileSync(STATE, 'utf8')); } catch { return null; } };
let state: State = load() ?? { canary: `CANARIO-${randomUUID()}`, ids: {} };
const save = () => { fs.mkdirSync(path.dirname(STATE), { recursive: true }); fs.writeFileSync(STATE, JSON.stringify(state)); };

let adminApi: APIRequestContext | undefined;
/** Versión del registro (la transición la exige: concurrencia optimista). */
const version = async (id: number) => (await (await (await admin()).get(`/api/data/CnDoc/${id}`)).json())._rowVersion as string;
let lectorApi: APIRequestContext | undefined;
const admin = async () => (adminApi ??= await apiAs(ADMIN.username, ADMIN.password));
const lector = async () => (lectorApi ??= await apiAs(LECTOR, PWD));

const MODEL = (canary: string) => ({
  description: 'E2E 029: canario de fugas',
  operations: [
    { op: 'createEntity', group: GROUP, name: 'CnSecreto', label: 'Secreto 029', labelPlural: 'Secretos 029', displayField: 'Nombre', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'CNS-', padding: 4 },
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'Dato', label: 'Dato', type: 'text', maxLength: 120 } ] },
    { op: 'createEntity', group: GROUP, name: 'CnEmpresa', label: 'Dato por empresa 029', labelPlural: 'Datos por empresa 029', displayField: 'Nombre', perCompany: true, fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'Dato', label: 'Dato', type: 'text', maxLength: 120 } ] },
    // Su campo para mostrar es una fórmula que llega al secreto (revisión adversarial 029, F-B: etiquetas, buscador, reportes).
    { op: 'createEntity', group: GROUP, name: 'CnPuente', label: 'Puente 029', labelPlural: 'Puentes 029', displayField: 'Etiqueta', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'Secreto', label: 'Secreto', type: 'reference', referenceEntity: 'CnSecreto' },
      { name: 'Empresa', label: 'Empresa', type: 'reference', referenceEntity: 'CnEmpresa' },
      { name: 'Etiqueta', label: 'Etiqueta', type: 'formula', resultType: 'text', formula: "Nombre & ' ' & COALESCE(Secreto.Dato, '')" } ] },
    { op: 'createEntity', group: GROUP, name: 'CnPuente2', label: 'Puente doble 029', labelPlural: 'Puentes dobles 029', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'Puente', label: 'Puente', type: 'reference', referenceEntity: 'CnPuente' } ] },
    { op: 'createEntity', group: GROUP, name: 'CnDoc', label: 'Documento 029', labelPlural: 'Documentos 029', displayField: 'Folio', fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'CND-', padding: 4 },
      { name: 'Nombre', label: 'Nombre', type: 'text', maxLength: 80, required: true },
      { name: 'Puente', label: 'Puente', type: 'reference', referenceEntity: 'CnPuente' },
      { name: 'Puente2', label: 'Puente doble', type: 'reference', referenceEntity: 'CnPuente2' },
      { name: 'Oculto', label: 'Oculto', type: 'text', maxLength: 120 },
      // Dos y tres saltos a un registro fuera de alcance, detrás de uno visible (auditoría 4 D-01).
      { name: 'Dos', label: 'A dos saltos', type: 'formula', resultType: 'text', formula: 'Puente.Secreto.Dato' },
      { name: 'Tres', label: 'A tres saltos', type: 'formula', resultType: 'text', formula: 'Puente2.Puente.Secreto.Dato' },
      // De un catálogo compartido a un registro de otra empresa.
      { name: 'OtraEmpresa', label: 'De otra empresa', type: 'formula', resultType: 'text', formula: 'Puente.Empresa.Dato' },
      // Una fórmula visible que lee un campo oculto (auditoría 4 D-05).
      { name: 'Copia', label: 'Copia', type: 'formula', resultType: 'text', formula: "Oculto & ''" } ] },
    { op: 'setSecurity', entity: 'CnSecreto', security: { records: [{ roles: [ROLE], scope: 'own' }] } },
    { op: 'setSecurity', entity: 'CnDoc', security: { fields: [{ field: 'Oculto', read: ['Administrador'] }] } },
    { op: 'setLayout', entity: 'CnDoc', layout: { sections: [
      { name: 'General', label: 'General', fields: ['Nombre', 'Puente', 'Puente2', 'Oculto', 'Dos', 'Tres', 'OtraEmpresa'] },
      // Se ve solo si el dato secreto está vacío: con el canario legible, desaparecería (un bit de fuga por layout).
      { name: 'SinSecreto', label: 'Sin secreto', visibleWhen: 'ISBLANK(Puente.Secreto.Dato)', fields: ['Copia'] } ] } },
    { op: 'setRules', entity: 'CnDoc', rules: [
      { name: 'SecretoVacio', field: 'Nombre', severity: 'warning', assert: 'NOT ISBLANK(Puente2.Puente.Secreto.Dato)', message: AVISO },
      { name: 'OcultoConValor', field: 'Nombre', severity: 'warning', assert: 'ISBLANK(Oculto)', message: AVISO_OCULTO } ] },
    { op: 'setWorkflow', entity: 'CnDoc', workflow: { initial: 'Abierto', states: [
      { name: 'Abierto', label: 'Abierto' }, { name: 'Cerrado', label: 'Cerrado', editable: false } ],
      transitions: [{ name: 'cerrar', label: 'Cerrar', from: ['Abierto'], to: 'Cerrado', onWarning: 'block' }] } },
    { op: 'setReports', entity: 'CnDoc', reports: [
      { name: 'PorDato', label: 'Por dato', groupBy: [{ field: 'Dos' }], measures: [{ fn: 'count' }] },
      { name: 'PorCopia', label: 'Por copia', groupBy: [{ field: 'Copia' }], measures: [{ fn: 'count' }] },
      { name: 'PorPuente', label: 'Por puente', groupBy: [{ field: 'Puente' }], measures: [{ fn: 'count' }] },
      { name: 'Conteo', label: 'Documentos', measures: [{ fn: 'count' }] } ] },
    { op: 'setDashboard', dashboard: { name: 'CnTablero', label: 'Tablero 029', widgets: [
      { type: 'table', entity: 'CnDoc', report: 'PorDato' }, { type: 'table', entity: 'CnDoc', report: 'PorCopia' },
      { type: 'kpi', entity: 'CnDoc', report: 'Conteo' } ] } },
    { op: 'setPrintTemplates', entity: 'CnDoc', templates: [{ name: 'Canario', label: 'Canario', default: true, blocks: [
      { type: 'text', text: 'Documento {{ Folio }}' },
      { type: 'text', text: 'Dos: {{ Puente.Secreto.Dato }} · Tres: {{ Puente2.Puente.Secreto.Dato }} · Empresa: {{ Puente.Empresa.Dato }}' },
      { type: 'text', text: 'Oculto: {{ Oculto }} · Fórmulas: {{ Dos }} {{ Tres }} {{ OtraEmpresa }} {{ Copia }}' } ] }] },
  ],
}) as const;

test.afterAll(async () => {
  if (process.env.E2E_INDEPENDENT) return; // cada falla reinicia el proceso y correría la limpieza a mitad
  const a = await apiAs(ADMIN.username, ADMIN.password);
  const companies = (await (await a.get('/api/admin/companies')).json()) as { id: number; code: string; isDefault: boolean; name: string; baseCurrency: string }[];
  const def = companies.find((c) => c.isDefault)!;
  await a.post('/api/auth/company', { data: { companyId: def.id } });
  const model = (await (await a.get('/api/model/entities', { params: { includeChildren: 'true' } })).json()) as { name: string }[];
  const boards = (await (await a.get('/api/dashboards')).json()) as { name: string }[];
  const operations = [
    ...(boards.some((d) => d.name === 'CnTablero') ? [{ op: 'archiveDashboard', name: 'CnTablero' }] : []),
    ...[...ENTITIES].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity })),
  ];
  if (operations.length) await applyPlan(a, { description: 'E2E 029: limpieza del canario', operations });
  const cn = companies.find((c) => c.code === COMPANY.code);
  if (cn) await a.put(`/api/admin/companies/${cn.id}`, { data: { ...COMPANY, isActive: false } });
  await a.dispose();
  fs.rmSync(STATE, { force: true });
});

test('preparar: canario en un campo oculto, fuera de alcance, detrás de uno visible (2 y 3 saltos) y en otra empresa', async () => {
  const a = await admin();
  const canary = state.canary;
  // Rol sin capacidades administrativas salvo ver la auditoría (la bitácora también se recorre con el filtro del dato). Se
  // crea antes del plan (su seguridad lo nombra) y recibe permisos después (sobre los catálogos del plan).
  const roles = (await (await a.get('/api/admin/roles')).json()) as { id: number; name: string }[];
  let role = roles.find((r) => r.name === ROLE)?.id;
  if (!role) {
    const r0 = await a.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 029', canManageModel: false, canManageUsers: false, canViewAudit: true, permissions: [] } });
    expect(r0.status(), await r0.text()).toBe(200);
    role = (await r0.json()).id as number;
  }
  if (!(await a.get('/api/model/entities/CnDoc')).ok()) await applyPlan(a, MODEL(canary));
  const perms = [
    { entity: 'CnDoc', canRead: true, canCreate: true, canUpdate: true, canDelete: false },
    ...['CnPuente', 'CnPuente2', 'CnSecreto', 'CnEmpresa'].map((entity) => ({ entity, canRead: true, canCreate: false, canUpdate: false, canDelete: false })),
  ];
  const roleBody = { name: ROLE, description: 'E2E spec 029', canManageModel: false, canManageUsers: false, canViewAudit: true, permissions: perms };
  const r = await a.put(`/api/admin/roles/${role}`, { data: roleBody });
  expect(r.status(), await r.text()).toBe(200);
  const users = (await (await a.get('/api/admin/users')).json()) as { id: number; username: string }[];
  if (!users.some((u) => u.username === LECTOR)) {
    const u = await a.post('/api/admin/users', { data: { username: LECTOR, displayName: 'Lector canario', password: PWD, roleIds: [role] } });
    expect(u.status(), await u.text()).toBe(201);
  }
  if (state.ids.doc) return;

  // Otra empresa con su dato, y un puente (catálogo compartido) que apunta a él, creados desde esa empresa.
  let companies = (await (await a.get('/api/admin/companies')).json()) as { id: number; code: string; isDefault: boolean }[];
  if (!companies.some((c) => c.code === COMPANY.code)) expect((await a.post('/api/admin/companies', { data: COMPANY })).status()).toBe(200);
  else await a.put(`/api/admin/companies/${companies.find((c) => c.code === COMPANY.code)!.id}`, { data: { ...COMPANY, isActive: true } });
  companies = (await (await a.get('/api/admin/companies')).json()) as { id: number; code: string; isDefault: boolean }[];
  const other = companies.find((c) => c.code === COMPANY.code)!.id;
  const home = companies.find((c) => c.isDefault)!.id;
  const post = async (entity: string, data: object) => {
    const res = await a.post(`/api/data/${entity}`, { data });
    expect(res.status(), await res.text()).toBe(201);
    return (await res.json()).id as number;
  };
  expect((await a.post('/api/auth/company', { data: { companyId: other } })).status()).toBe(200);
  const ids: Record<string, number> = {};
  ids.empresa = await post('CnEmpresa', { Nombre: 'Dato de la otra empresa', Dato: canary });
  ids.puenteEmpresa = await post('CnPuente', { Nombre: 'Puente a la otra empresa', Empresa: ids.empresa });
  expect((await a.post('/api/auth/company', { data: { companyId: home } })).status()).toBe(200);

  // El secreto lo crea el administrador: el lector (alcance «propios») no lo ve.
  ids.secreto = await post('CnSecreto', { Nombre: 'Secreto', Dato: canary });
  ids.puente = await post('CnPuente', { Nombre: 'Puente visible', Secreto: ids.secreto });
  ids.puente2 = await post('CnPuente2', { Nombre: 'Puente doble visible', Puente: ids.puente });
  // Un secreto propio del lector, con un dato que sí puede ver (para comprobar que el alcance no apaga todo).
  ids.doc = await post('CnDoc', { Nombre: 'Documento visible', Puente: ids.puente, Puente2: ids.puente2, Oculto: canary });
  ids.docEmpresa = await post('CnDoc', { Nombre: 'Documento con otra empresa', Puente: ids.puenteEmpresa });
  state = { canary, ids };
  save();

  // Lo que el lector ve debe estar a su alcance (si no, el recorrido no probaría nada).
  const l = await lector();
  expect((await l.get(`/api/data/CnDoc/${ids.doc}`)).status()).toBe(200);
  expect((await l.get(`/api/data/CnPuente/${ids.puente}`)).status()).toBe(200);
  expect((await l.get(`/api/data/CnSecreto/${ids.secreto}`)).status()).toBe(404);
  // Y que el canario existe para quien sí puede verlo.
  expect(JSON.stringify(await (await a.get(`/api/data/CnDoc/${ids.doc}`)).json())).toContain(canary);
});

// ---------------- Lectura de PDF (texto con las fuentes incrustadas) ----------------

/**
 * Texto de un PDF de PDFsharp: Lato incrustada con WinAnsiEncoding, así que el texto va en cadenas literales de los
 * operadores Tj/TJ de los flujos de contenido (comprimidos con Flate). Se unen sin espacios: una palabra partida al final de
 * una línea (el canario lleva guiones) sigue apareciendo completa.
 */
function pdfText(pdf: Buffer): string {
  const raw = pdf.toString('latin1');
  const parts: string[] = [];
  const re = /<<([^]*?)>>\s*stream\r?\n/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(raw))) {
    const length = Number(/\/Length (\d+)/.exec(m[1])?.[1] ?? 0);
    const start = m.index + m[0].length;
    let content = pdf.subarray(start, start + length).toString('latin1');
    if (m[1].includes('FlateDecode'))
      // PDFsharp no cierra el flujo zlib con su suma de verificación: se infla sin exigir el final.
      try { content = zlib.inflateSync(pdf.subarray(start, start + length), { finishFlush: zlib.constants.Z_SYNC_FLUSH }).toString('latin1'); } catch { continue; }
    if (!/\bBT\b/.test(content) || !/\bT[Jj]\b/.test(content)) continue;
    for (const lit of content.matchAll(/\(((?:\\.|[^\\)])*)\)/g)) parts.push(lit[1].replace(/\\([()\\])/g, '$1'));
  }
  return parts.join('');
}

// ---------------- Recorrido del inventario ----------------

type InventoryRoute = { method: string; path: string; capability: string; effect: string };
function inventory(): InventoryRoute[] {
  const md = fs.readFileSync(path.join(HERE, '..', '..', 'docs', 'sdd', 'rutas.md'), 'utf8');
  return md.split('\n').filter((l) => l.startsWith('| ') && l.includes('`'))
    .map((l) => l.split('|').map((c) => c.trim()))
    .map((c) => ({ method: c[1], path: c[2].replace(/`/g, ''), capability: c[3], effect: c[4] }));
}

/** Rutas concretas de una plantilla, con los registros del canario en cada parámetro. */
function expand(template: string, ids: Record<string, number>): string[] {
  if (template.includes('{**')) return [];
  const byEntity: Record<string, number[]> = {
    CnDoc: [ids.doc, ids.docEmpresa], CnPuente: [ids.puente, ids.puenteEmpresa], CnPuente2: [ids.puente2],
    CnSecreto: [ids.secreto], CnEmpresa: [ids.empresa],
  };
  let urls = [template];
  const fill = (param: string, values: string[]) => {
    urls = urls.flatMap((u) => (u.includes(param) ? values.map((v) => u.split(param).join(v)) : [u]));
  };
  if (template.startsWith('/odata/{segment}'))
    return ['$metadata', ...ENTITIES, ...ENTITIES.flatMap((e) => byEntity[e].map((id) => `${e}(${id})`))].map((s) => `/odata/${s}`);
  if (template.includes('{entity}') && template.includes('{id:long}'))
    return [...ENTITIES].flatMap((e) => byEntity[e].map((id) => template.replace('{entity}', e).replace('{id:long}', String(id))));
  fill('{entity}', [...ENTITIES]);
  fill('{name}', template.startsWith('/api/reports') ? ['PorDato', 'PorCopia', 'PorPuente', 'Conteo'] : template.startsWith('/api/dashboards') ? ['CnTablero'] : [...ENTITIES]);
  fill('{id:long}', [String(ids.doc)]);
  fill('{id:int}', ['1']);
  fill('{token}', ['x']);
  fill('{key}', ['x']);
  // Spec 031: un parámetro nuevo (p. ej. {code}) se pedía con el texto literal y la ruta pasaba sin recorrerse.
  const unresolved = urls.filter((u) => /\{[^}]*\}/.test(u));
  if (unresolved.length) throw new Error(`expand() no sabe llenar ${unresolved.join(', ')}: agrega el parámetro con los datos del canario`);
  return urls;
}

/** Parámetros de consulta que hacen que una ruta devuelva más (búsquedas, formato, todas las columnas). */
const QUERIES: Record<string, Record<string, string>[]> = {
  '/api/search': [{ q: 'CND-' }, { q: 'CNS-' }, { q: 'CANARIO' }],
  '/api/data/{entity}/': [{}, { search: 'CANARIO' }, { search: 'Secreto' }, ...['Dos', 'Tres', 'Copia', 'Oculto'].map((sort) => ({ sort, dir: 'desc' }))],
  // Spec 031: el buscador filtrado (por un campo que ve y por uno oculto, que es 400).
  '/api/data/{entity}/lookup': [{}, { search: 'Secreto' }, { search: 'CANARIO' }, { filterField: 'Secreto', filterValue: '1' },
    { filterField: 'Puente', filterValue: '1' }, { filterField: 'Oculto', filterValue: 'CANARIO' }],
  '/api/data/{entity}/export': [{ format: 'csv' }, { format: 'csv', columns: 'Folio,Nombre,Oculto,Dos,Tres,OtraEmpresa,Copia,Dato,Secreto' },
    { format: 'xlsx' }, { format: 'xlsx', columns: 'Folio,Nombre,Oculto,Dos,Tres,OtraEmpresa,Copia,Dato,Secreto' }],
  '/api/reports/{entity}/{name}/export': [{ format: 'csv' }, { format: 'xlsx' }],
  '/api/audit': [{}, { entity: 'CnDoc' }, { entity: 'CnSecreto' }, { entity: 'CnEmpresa' }],
  '/api/users/lookup': [{}],
  '/odata/{segment}': [{}, { $expand: 'Puente' }],
};

/**
 * Spec 031: el texto de cada parte de un ZIP (un .xlsx), por su directorio central: el canario dentro de una celda va
 * comprimido y `includes` sobre los bytes no lo vería.
 */
function zipText(zip: Buffer): string {
  let eocd = zip.length - 22;
  while (eocd >= 0 && zip.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('no es un ZIP');
  const count = zip.readUInt16LE(eocd + 10);
  let at = zip.readUInt32LE(eocd + 16);
  const parts: string[] = [];
  for (let i = 0; i < count; i++) {
    if (zip.readUInt32LE(at) !== 0x02014b50) throw new Error('directorio central dañado');
    const method = zip.readUInt16LE(at + 10), size = zip.readUInt32LE(at + 20);
    const nameLen = zip.readUInt16LE(at + 28), extraLen = zip.readUInt16LE(at + 30), commentLen = zip.readUInt16LE(at + 32);
    const local = zip.readUInt32LE(at + 42);
    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    const data = zip.subarray(start, start + size);
    parts.push((method === 8 ? zlib.inflateRawSync(data) : data).toString('utf8'));
    at += 46 + nameLen + extraLen + commentLen;
  }
  return parts.join('\n');
}

async function bodyText(res: { headers(): Record<string, string>; body(): Promise<Buffer> }) {
  const body = await res.body();
  const type = res.headers()['content-type'] ?? '';
  if (type.includes('pdf')) return pdfText(body);
  if (type.includes('spreadsheetml')) return zipText(body);
  return body.toString('utf8');
}

test('H2 CA2: ninguna ruta GET del inventario devuelve el canario al lector', async () => {
  test.setTimeout(180_000);
  const l = await lector();
  const { canary, ids } = state;
  const routes = inventory().filter((r) => r.method === 'GET');
  expect(routes.length).toBeGreaterThan(60);
  const leaks: string[] = [];
  let visited = 0;
  for (const r of routes)
    for (const url of expand(r.path, ids))
      for (const params of QUERIES[r.path] ?? [{}]) {
        const res = await l.get(url, { params });
        visited++;
        if ((await bodyText(res)).includes(canary)) leaks.push(`${url} ${JSON.stringify(params)} (${res.status()})`);
      }
  expect(visited).toBeGreaterThan(150);
  expect(leaks, 'rutas que devuelven el canario').toEqual([]);
  // La extracción de XLSX funciona (si no, buscar el canario en Excel no probaría nada).
  const own = await (await admin()).get('/api/data/CnDoc/export', { params: { format: 'xlsx', columns: 'Nombre,Oculto' } });
  expect(zipText(await own.body())).toContain(canary);
});

test('H2 CA2 (031): ordenar por una fórmula o un campo que no ve se ignora (el orden no es un oráculo)', async () => {
  const l = await lector();
  const order = async (params: Record<string, string>) =>
    ((await (await l.get('/api/data/CnDoc', { params: { pageSize: '50', ...params } })).json()).items as { id: number }[]).map((r) => r.id);
  const base = await order({});
  expect(base.length).toBeGreaterThan(1);
  for (const sort of ['Dos', 'Tres', 'OtraEmpresa', 'Copia', 'Oculto'])
    for (const dir of ['asc', 'desc']) expect(await order({ sort, dir }), `sort=${sort}&dir=${dir}`).toEqual(base);
});

/**
 * Una fila que el lector sí puede importar. Spec 031: la de la 029 traía también «Puente visible», cuyo campo para mostrar es
 * una fórmula que no ve (F-B), así que la fila fallaba y la vista previa no calculaba avisos: la prueba no probaba nada.
 */
const IMPORT_CSV = Buffer.from('Nombre;Puente doble\nImportado;Puente doble visible\n');

test('H2 CA2: tampoco las rutas que no son GET y devuelven datos (tomadas del inventario)', async () => {
  const l = await lector();
  const { canary, ids } = state;
  const leaks: string[] = [];
  type Res = { headers(): Record<string, string>; body(): Promise<Buffer>; status(): number };
  const check = async (what: string, res: Res) => {
    if ((await bodyText(res)).includes(canary)) leaks.push(`${what} (${res.status()})`);
  };
  let mine: { id: number; _rowVersion: string } | undefined;
  // Spec 031 (auditoría 5): la lista sale del inventario. Una ruta nueva que escribe datos o no guarda nada, y que el lector
  // puede llamar, hace fallar la prueba hasta que tenga su llamada aquí. Las de efecto «sesión» (sus vistas, su empresa,
  // cerrar sesión) no devuelven registros.
  const calls: Record<string, () => Promise<void>> = {
    'POST /api/data/{entity}/compute': async () => {
      for (const values of [{ Puente: ids.puente, Puente2: ids.puente2 }, { Puente: ids.puenteEmpresa }, { Puente: ids.secreto }, {}])
        await check(`compute nuevo ${JSON.stringify(values)}`, await l.post('/api/data/CnDoc/compute', { data: { values: { Nombre: 'x', ...values } } }));
      for (const id of [ids.doc, ids.docEmpresa])
        await check(`compute ${id}`, await l.post('/api/data/CnDoc/compute', { data: { id, values: {} } }));
    },
    'POST /api/data/{entity}/import': async () => {
      const res = await l.post('/api/data/CnDoc/import', { params: { dryRun: 'true' }, data: IMPORT_CSV, headers: { 'Content-Type': 'application/octet-stream' } });
      expect((await res.json()).valid, await res.text()).toBe(true);
      await check('importación (vista previa)', res);
    },
    // Crear y editar devuelven el registro con sus fórmulas (y avisos).
    'POST /api/data/{entity}/': async () => {
      const created = await l.post('/api/data/CnDoc', { data: { Nombre: 'Del lector', Puente: ids.puente, Puente2: ids.puente2 } });
      expect(created.status(), await created.text()).toBe(201);
      await check('crear', created);
      mine = await created.json();
    },
    'PUT /api/data/{entity}/{id:long}': async () =>
      check('editar', await l.put(`/api/data/CnDoc/${mine!.id}`, { data: { Nombre: 'Del lector (editado)', _rowVersion: mine!._rowVersion } })),
    'POST /api/data/{entity}/{id:long}/transition': async () =>
      check('transición', await l.post(`/api/data/CnDoc/${ids.doc}/transition`, { data: { transition: 'cerrar', _rowVersion: await version(state.ids.doc) } })),
    // El lector no borra (403); el cuerpo del rechazo tampoco lleva el dato.
    'DELETE /api/data/{entity}/{id:long}': async () => check('borrar', await l.delete(`/api/data/CnDoc/${mine!.id}`)),
    ...Object.fromEntries(['POST', 'PUT', 'PATCH', 'DELETE', 'MERGE'].map((method) => [`${method} /odata/{**rest}`, async () =>
      check(`OData ${method}`, await l.fetch('/odata/CnDoc', { method, data: { Nombre: canary } }))])),
  };
  // Revisión adversarial 031 (F-I): una capacidad alternativa («registros o usuarios») también la puede llamar el lector.
  const routes = inventory().filter((r) => !['GET', '*'].includes(r.method)
    && r.capability.split(' o ').some((c) => ['registros', 'sesión', 'auditoría'].includes(c))
    && ['ninguno', 'datos'].includes(r.effect)).map((r) => `${r.method} ${r.path}`);
  expect(routes.length).toBeGreaterThan(8);
  expect(routes.filter((k) => !calls[k]), 'rutas del inventario que el canario no recorre').toEqual([]);
  // En el orden de la tabla de llamadas: crear antes de editar y de borrar.
  for (const [key, call] of Object.entries(calls)) if (routes.includes(key)) await call();
  expect(leaks, 'rutas que devuelven el canario').toEqual([]);
});

test('H1 CA1 (031): la vista previa de la importación le dice al lector los mismos avisos que /compute', async () => {
  const l = await lector();
  const { ids } = state;
  const compute = await (await l.post('/api/data/CnDoc/compute', { data: { values: { Nombre: 'Importado', Puente2: ids.puente2 } } })).json();
  const res = await l.post('/api/data/CnDoc/import', { params: { dryRun: 'true' }, data: IMPORT_CSV, headers: { 'Content-Type': 'application/octet-stream' } });
  expect(res.status(), await res.text()).toBe(200);
  const preview = await res.json();
  // La fila se importa (si no, la vista previa no calcularía avisos y la comparación no probaría nada).
  expect(preview.valid, JSON.stringify(preview.errors)).toBe(true);
  const imported = (preview.warnings as { message: string }[]).map((w) => w.message).sort();
  const computed = Object.values((compute.warnings ?? {}) as Record<string, string[]>).flat().sort();
  // Regresión (auditoría 5 A-01): con el secreto legible para el motor, «el dato secreto está vacío» salía en /compute y no
  // en la importación: un bit por fila sobre el registro fuera de alcance.
  expect(computed).toContain(AVISO);
  expect(imported).toEqual(computed);
});

// ---------------- H3: alcance en cada salto (D-01) ----------------

test('H3 CA1: lo calculado en vivo no lee el registro fuera de alcance a 2 y 3 saltos (valores, layout y avisos)', async () => {
  const l = await lector();
  const { ids } = state;
  const res = await l.post('/api/data/CnDoc/compute', { data: { values: { Nombre: 'x', Puente: ids.puente, Puente2: ids.puente2 } } });
  expect(res.status(), await res.text()).toBe(200);
  const body = await res.json();
  // Regresión (auditoría 4 D-01): la condición «el dato secreto está vacío» es verdadera para quien no lo ve…
  expect(body.layout.hiddenSections).not.toContain('SinSecreto');
  // …y el aviso «el dato secreto está vacío» aparece (con el secreto legible, desaparecía: un bit por salto).
  expect(JSON.stringify(body.warnings ?? {})).toContain(AVISO);
  // El registro guardado se ve igual (layout y avisos de GET).
  const doc = await (await l.get(`/api/data/CnDoc/${ids.doc}`)).json();
  expect(doc._layout.hiddenSections).not.toContain('SinSecreto');
  // Para el administrador, el secreto sí se lee: la sección se oculta y el aviso no aplica.
  const a = await (await (await admin()).post('/api/data/CnDoc/compute', { data: { values: { Nombre: 'x', Puente: ids.puente, Puente2: ids.puente2 } } })).json();
  expect(a.layout.hiddenSections).toContain('SinSecreto');
  expect(JSON.stringify(a.warnings ?? {})).not.toContain(AVISO);
});

test('H3 CA2: las fórmulas guardadas que recorren un catálogo restringido no se le muestran; el modelo las marca ocultas', async () => {
  const l = await lector();
  const doc = await (await l.get(`/api/data/CnDoc/${state.ids.doc}`)).json();
  for (const f of ['Dos', 'Tres', 'OtraEmpresa', 'Copia', 'Oculto']) expect(doc).not.toHaveProperty(f);
  expect(doc.Nombre).toBe('Documento visible');
  // Regresión (revisión adversarial 029, F-B): el campo para mostrar del puente es una fórmula que no ve: ni etiqueta ni buscador.
  expect(doc.Puente__label).toBe('(sin acceso)');
  const options = (await (await l.get('/api/data/CnPuente/lookup')).json()) as { id: number; label: string }[];
  expect(options.find((o) => o.id === state.ids.puente)?.label).toBe(String(state.ids.puente));
  const fields = ((await (await l.get('/api/model/entities/CnDoc')).json()).fields as { name: string }[]).map((f) => f.name);
  expect(fields).toEqual(expect.arrayContaining(['Nombre', 'Puente', 'Puente2']));
  for (const f of ['Dos', 'Tres', 'OtraEmpresa', 'Copia', 'Oculto']) expect(fields).not.toContain(f);
  // No se ordena ni se filtra por ellas, y los reportes que agrupan por ellas no le aplican.
  expect((await l.get('/api/data/CnDoc', { params: { filters: JSON.stringify([{ field: 'Dos', op: 'eq', value: state.canary }]) } })).status()).toBe(400);
  expect((await l.get('/api/reports/CnDoc/PorDato')).status()).toBe(404);
  expect((await l.get('/api/reports/CnDoc/PorCopia')).status()).toBe(404);
  expect((await l.get('/api/reports/CnDoc/Conteo')).status()).toBe(200);
});

test('H3 CA3: el PDF conserva «(sin acceso)» por salto y no imprime las fórmulas que no ve', async () => {
  const res = await (await lector()).get(`/api/data/CnDoc/${state.ids.doc}/pdf`);
  expect(res.status(), await res.text().catch(() => '')).toBe(200);
  const text = pdfText(await res.body());
  // La extracción funciona (si no, la búsqueda del canario no probaría nada).
  expect(text).toContain('Documento');
  expect(text).toContain('(sinacceso)'); // cada salto fuera de alcance y cada campo oculto
  expect(text).not.toContain(state.canary);
  const own = pdfText(await (await (await admin()).get(`/api/data/CnDoc/${state.ids.doc}/pdf`)).body());
  expect(own).toContain(state.canary);
});

// ---------------- H4 CA3: el bloqueo por avisos no lista los que leen ocultos (D-08) ----------------

test('H4 CA3: con onWarning block, el 400 solo dice los avisos que el usuario puede ver', async () => {
  const l = await lector();
  const res = await l.post(`/api/data/CnDoc/${state.ids.doc}/transition`, { data: { transition: 'cerrar', _rowVersion: await version(state.ids.doc) } });
  expect(res.status()).toBe(400);
  const text = await res.text();
  // Regresión (auditoría 4 D-08): el aviso que lee el campo oculto no se lista (revelaba que tiene valor). El motor bloquea
  // con su autoridad (el secreto no está vacío y solo queda ese aviso); spec 031 (A-01): lo que se le dice es lo que /compute
  // le muestra (para él, el secreto está vacío), nunca el cruce con los avisos del motor.
  expect(text).not.toContain(AVISO_OCULTO);
  expect(text).toContain(AVISO);
  expect((await (await l.get(`/api/data/CnDoc/${state.ids.doc}`)).json())._workflow.state).toBe('Abierto');
  // Al administrador (que ve el campo) sí.
  expect(await (await (await admin()).post(`/api/data/CnDoc/${state.ids.doc}/transition`, { data: { transition: 'cerrar', _rowVersion: await version(state.ids.doc) } })).text()).toContain(AVISO_OCULTO);
});
