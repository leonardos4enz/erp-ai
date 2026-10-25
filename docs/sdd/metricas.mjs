// Spec 028: tabla de métricas por spec (§1.2 de la retrospectiva 001) a partir de los verify.md.
// Uso: node docs/sdd/metricas.mjs
// Los verify.md con la plantilla (desde la 028) traen un bloque «## Métricas» con `- clave: valor`; de los anteriores se
// cuentan los hallazgos F-xx por severidad y la fecha del commit que cerró la spec. Un dato ausente es «n/d», nunca 0.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

const specs = path.join(import.meta.dirname, 'specs');
const ND = 'n/d';

function metricas(texto) {
  const bloque = /^## Métricas\s*\n([\s\S]*?)(?=^## )/m.exec(texto);
  if (!bloque) return null;
  const m = {};
  for (const linea of bloque[1].split('\n')) {
    const r = /^-\s*([^:]+):\s*(.*)$/.exec(linea.trim());
    if (r) m[r[1].trim().toLowerCase()] = r[2].trim();
  }
  return m;
}

/** Hallazgos F-xx por severidad en las tablas del verify (un ID cuenta una vez, aunque se repita en otra tabla). */
function hallazgos(texto) {
  const vistos = new Map();
  for (const linea of texto.split('\n')) {
    const id = /^\|\s*\**(F-\d+)\**\s*\|/.exec(linea)?.[1];
    if (!id || vistos.has(id)) continue;
    const sev = linea.split('|').map((c) => c.trim().replace(/\*/g, '')).find((c) => /^(Alta|Media|Baja)\b/i.test(c));
    if (sev) vistos.set(id, sev[0].toUpperCase());
  }
  if (!vistos.size) return ND;
  const n = (s) => [...vistos.values()].filter((v) => v === s).length;
  return `${vistos.size} (${n('A')}/${n('M')}/${n('B')})`;
}

function duracion(inicio, cierre) {
  const a = Date.parse(inicio?.replace(' ', 'T')), b = Date.parse(cierre?.replace(' ', 'T'));
  if (Number.isNaN(a) || Number.isNaN(b) || b < a) return ND;
  const min = Math.round((b - a) / 60000);
  return min >= 60 ? `${Math.floor(min / 60)} h ${String(min % 60).padStart(2, '0')} min` : `${min} min`;
}

function cierreGit(archivo) {
  try {
    const f = execFileSync('git', ['log', '--diff-filter=A', '--format=%ci', '--', archivo], { cwd: specs, encoding: 'utf8' }).trim().split('\n').at(-1);
    return f ? f.slice(0, 16) : ND;
  } catch { return ND; }
}

const filas = [];
for (const dir of readdirSync(specs).sort()) {
  const archivo = path.join(specs, dir, 'verify.md');
  if (!existsSync(archivo)) continue;
  const texto = readFileSync(archivo, 'utf8');
  const titulo = /^# (?:Verify|Verificación) ([^\n]+)/m.exec(texto)?.[1]?.replace(/^\d+(\s*(y|\+)\s*\d+)?\s*[—-]\s*/, '') ?? dir;
  const m = metricas(texto);
  const v = (k) => (m?.[k] && m[k] !== '' ? m[k] : ND);
  filas.push([
    `${dir.slice(0, 3)} ${titulo}`,
    m ? v('cierre') : cierreGit(archivo),
    m ? duracion(m.inicio, m.cierre) : ND,
    v('unitarias'),
    v('e2e'),
    v('corridas completas'),
    m?.hallazgos ? `${m.hallazgos}` : hallazgos(texto),
    v('revisión adversarial'),
    v('riesgos'),
    m ? 'plantilla' : 'anterior',
  ]);
}

const cab = ['Spec', 'Cierre', 'Duración', 'Unit.', 'E2E', 'Corridas completas', 'Hallazgos (A/M/B)', 'Adversarial', 'Riesgos', 'Formato'];
console.log(`| ${cab.join(' | ')} |\n|${cab.map(() => '---').join('|')}|`);
for (const f of filas) console.log(`| ${f.map((c) => c.replace(/\|/g, '\\|')).join(' | ')} |`);
