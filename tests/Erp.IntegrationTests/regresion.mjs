// Spec 030 (H2 CA2): corre pruebas de integración contra el código anterior («cada hallazgo se vuelve prueba que falla con
// el código anterior»). Crea un worktree en <commit>, le copia tests/Erp.IntegrationTests tal como está aquí y corre el
// filtro pedido. Cada clase crea y borra su propia base (_IT), así que no choca con la suite de esta carpeta.
// Uso: npm --prefix web run it:regresion -- <commit> <filtro de dotnet test o nombre de clase/prueba> [--conservar] [--sin=<archivo.cs>]
//   p. ej. npm --prefix web run it:regresion -- cd81760 Hardening012Tests.H3
// Spec 034: --sin=<archivo.cs> (repetible) no copia ese archivo. Una prueba que usa una API nueva no compila contra el
// commit anterior y dejaría sin correr al resto: esa falla por definición y se dice en verify.md.
import { execFileSync, spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const args = process.argv.slice(2);
const conservar = args.includes('--conservar');
const sin = args.filter((a) => a.startsWith('--sin=')).map((a) => a.slice('--sin='.length));
if (sin.some((f) => !/^[A-Za-z0-9_.]+\.cs$/.test(f))) {
  console.error('--sin espera el nombre de un archivo .cs de tests/Erp.IntegrationTests');
  process.exit(2);
}
const [commit, filtro] = args.filter((a) => a !== '--conservar' && !a.startsWith('--sin='));
if (!commit || commit.startsWith('-') || !filtro) {
  console.error('Uso: npm --prefix web run it:regresion -- <commit> <filtro> [--conservar]');
  process.exit(2);
}
// El filtro llega como argumento de dotnet (sin shell); un nombre simple se toma como «contiene».
const filter = /[=~!&|]/.test(filtro) ? filtro : `FullyQualifiedName~${filtro}`;

const raiz = path.resolve(import.meta.dirname, '..', '..');
const git = (...a) => execFileSync('git', a, { cwd: raiz, encoding: 'utf8' }).trim();
const sha = git('rev-parse', '--verify', '--quiet', `${commit}^{commit}`);
const corto = sha.slice(0, 7);

const wt = mkdtempSync(path.join(tmpdir(), `erp-it-regresion-${corto}-`));
let creado = false;
let status = 1;
try {
  console.log(`[it:regresion] Worktree en ${corto} (${wt})`);
  rmdirSync(wt); // git worktree add crea la carpeta; mkdtemp solo reservó el nombre
  git('worktree', 'add', '--detach', wt, sha);
  creado = true;
  const origen = path.join(raiz, 'tests', 'Erp.IntegrationTests');
  cpSync(origen, path.join(wt, 'tests', 'Erp.IntegrationTests'), {
    recursive: true, filter: (f) => !/[\\/](bin|obj|TestResults)([\\/]|$)/.test(path.relative(origen, f)) && !sin.includes(path.basename(f)),
  });
  if (sin.length) console.log(`[it:regresion] Sin copiar: ${sin.join(', ')}`);
  console.log(`[it:regresion] tests/Erp.IntegrationTests copiado; filtro: ${filter}`);
  const resultados = path.join(wt, 'tests', 'Erp.IntegrationTests', 'TestResults');
  const r = spawnSync('dotnet', ['test', path.join(wt, 'tests', 'Erp.IntegrationTests'), '--filter', filter,
    '--logger', 'trx;LogFileName=regresion.trx', '--results-directory', resultados], { stdio: 'inherit', cwd: wt });
  status = r.status ?? 1;

  const trx = path.join(resultados, 'regresion.trx');
  if (existsSync(trx)) {
    const filas = resumir(readFileSync(trx, 'utf8'));
    const destino = path.join(raiz, 'tests', 'Erp.IntegrationTests', 'TestResults', `regresion-${corto}.json`);
    mkdirSync(path.dirname(destino), { recursive: true });
    writeFileSync(destino, JSON.stringify({ commit: sha, filtro: filter, filas }, null, 2));
    console.log(`\n[it:regresion] Contra ${corto}: ${filas.filter((f) => f.estado === 'FALLA').length} fallan, ` +
      `${filas.filter((f) => f.estado === 'correcta').length} pasan`);
    for (const f of filas) console.log(`  ${f.estado.padEnd(8)} ${f.prueba}${f.error ? `\n           ${f.error}` : ''}`);
    console.log(`[it:regresion] Resumen en ${path.relative(raiz, destino)}`);
  } else {
    console.log('[it:regresion] Sin resultados: ¿compiló el proyecto contra ese commit? (ver la salida de arriba)');
  }
} finally {
  if (conservar) console.log(`[it:regresion] Se conserva ${wt}.`);
  else if (creado) git('worktree', 'remove', '--force', wt);
  else if (existsSync(wt) && !readdirSync(wt).length) rmdirSync(wt);
}
process.exit(status);

/** Una fila por prueba del .trx: nombre, estado y la primera línea del mensaje de error. */
function resumir(xml) {
  const filas = [];
  for (const m of xml.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const attr = (n) => new RegExp(`${n}="([^"]*)"`).exec(m[1])?.[1] ?? '';
    const outcome = attr('outcome');
    const mensaje = /<Message>([\s\S]*?)<\/Message>/.exec(m[2] ?? '')?.[1] ?? '';
    filas.push({
      prueba: decode(attr('testName')),
      estado: outcome === 'Passed' ? 'correcta' : outcome === 'NotExecuted' ? 'omitida' : 'FALLA',
      error: (decode(mensaje).split('\n').map((l) => l.trim()).find(Boolean) ?? '').slice(0, 300),
    });
  }
  return filas;
}

function decode(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&#xD;/g, '').replace(/&#xA;/g, '\n').replace(/&amp;/g, '&');
}
