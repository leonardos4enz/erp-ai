// Spec 028: corre las pruebas nuevas contra el código anterior («cada hallazgo se vuelve prueba que falla con el código
// anterior»). Crea un worktree en <commit>, le copia las pruebas cambiadas y la infraestructura de la suite, y las corre con
// E2E_INDEPENDENT=1 en su propia base y sus propios puertos (no choca con la suite de la carpeta principal).
// Uso: npm run e2e:regresion -- <commit> [e2e/archivo.spec.ts … | argumentos de Playwright] [--conservar]
//   Sin archivos corre los *.spec.ts que cambiaron desde <commit>. Si una prueba depende de otro archivo (p. ej. de que
//   compras.spec.ts instale Compras), agrégalo: Playwright los corre en orden alfabético.
import { execFileSync, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmdirSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { baseDeCarpeta, borrarBase, carpeta } from './instancia.mjs';

const args = process.argv.slice(2);
const conservar = args.includes('--conservar');
const [commit, ...resto] = args.filter((a) => a !== '--conservar');
if (!commit || commit.startsWith('-')) {
  console.error('Uso: npm run e2e:regresion -- <commit> [e2e/archivo.spec.ts …] [--conservar]');
  process.exit(2);
}

const actual = carpeta(import.meta.dirname);
// core.quotePath=false: sin él, git entrega los nombres no ASCII entre comillas y con escapes, y no se copiarían.
const git = (...a) => execFileSync('git', ['-c', 'core.quotePath=false', ...a], { cwd: actual.raiz, encoding: 'utf8' }).trim();
const sha = git('rev-parse', '--verify', '--quiet', `${commit}^{commit}`);
const corto = sha.slice(0, 7);

// Corridas que quedaron a medias (el proceso murió) con la unión adentro: no se borran a ciegas, se dice cómo.
for (const viejo of readdirSync(tmpdir()).filter((d) => d.startsWith('erp-regresion-'))) {
  const union = path.join(tmpdir(), viejo, 'web', 'node_modules');
  if (existsSync(union)) {
    console.warn(`[regresion] Aviso: ${path.join(tmpdir(), viejo)} quedó de otra corrida (o sigue corriendo). Para quitarlo, ` +
      `primero la unión SIN recursión (PowerShell: [IO.Directory]::Delete('${union}', $false); cmd: rmdir "${union}") y luego ` +
      `git worktree remove --force "${path.join(tmpdir(), viejo)}". Nunca Remove-Item -Recurse sobre la unión (013 F-03).`);
  }
}

// Lo que se copia: lo de web/e2e que cambió desde el commit (o es nuevo) y la configuración de Playwright.
const cambiados = [...new Set([
  ...git('diff', '--name-only', sha, '--', 'web/e2e', 'web/playwright.config.ts').split('\n'),
  ...git('ls-files', '--others', '--exclude-standard', '--', 'web/e2e').split('\n'),
])].filter((f) => f && existsSync(path.join(actual.raiz, f)));
const pruebas = resto.length ? resto : cambiados.filter((f) => f.endsWith('.spec.ts')).map((f) => path.posix.relative('web', f));
if (!pruebas.length) {
  console.error(`[regresion] No hay pruebas cambiadas desde ${corto}; indica los archivos a correr.`);
  process.exit(2);
}

// Revisión adversarial 028 (F-01): ruta y base únicas por corrida (dos sesiones pueden verificar el mismo commit a la
// vez) y la limpieza solo quita lo que esta corrida creó.
const wt = mkdtempSync(path.join(tmpdir(), `erp-regresion-${corto}-`));
const sufijo = path.basename(wt).slice(-6).replace(/[^A-Za-z0-9]/g, '_');
const modulos = path.join(wt, 'web', 'node_modules');
const db = baseDeCarpeta({ worktree: true, nombre: `regresion_${corto}_${sufijo}` });
const creado = { worktree: false, union: false, base: false };
// F-02: Ctrl+C llega también a este proceso; sin manejador, Node terminaría sin pasar por el finally y la unión quedaría
// viva. Con él, spawnSync regresa cuando Playwright termina y la limpieza corre.
process.on('SIGINT', () => console.log('\n[regresion] Interrumpido: limpiando…'));
let status = 1;
try {
  console.log(`[regresion] Worktree en ${corto} (${wt})`);
  rmdirSync(wt); // git worktree add crea la carpeta; mkdtemp solo reservó el nombre
  git('worktree', 'add', '--detach', wt, sha);
  creado.worktree = true;
  for (const f of cambiados) {
    mkdirSync(path.dirname(path.join(wt, f)), { recursive: true });
    copyFileSync(path.join(actual.raiz, f), path.join(wt, f));
  }
  console.log(`[regresion] ${cambiados.length} archivos de prueba copiados; node_modules por unión`);
  symlinkSync(path.join(actual.raiz, 'web', 'node_modules'), modulos, 'junction');
  creado.union = true;

  const json = path.join(wt, 'web', 'e2e', '.results', 'regresion.json');
  const env = { ...process.env, E2E_INDEPENDENT: '1', E2E_DB: db, PLAYWRIGHT_JSON_OUTPUT_NAME: json };
  delete env.E2E_PORT_BASE; // el worktree elige su propio bloque libre
  creado.base = true;
  const r = spawnSync(process.execPath, ['e2e/run.mjs', ...pruebas, '--reporter=list,json'], { cwd: path.join(wt, 'web'), stdio: 'inherit', env });
  status = r.status ?? 1;

  if (existsSync(json)) {
    const filas = resumir(JSON.parse(readFileSync(json, 'utf8')));
    const destino = path.join(actual.raiz, 'web', 'e2e', '.results', `regresion-${corto}.json`);
    mkdirSync(path.dirname(destino), { recursive: true });
    writeFileSync(destino, JSON.stringify({ commit: sha, pruebas, filas }, null, 2));
    console.log(`\n[regresion] Contra ${corto}: ${filas.filter((f) => f.estado === 'FALLA').length} fallan, ` +
      `${filas.filter((f) => f.estado === 'correcta').length} pasan, ${filas.filter((f) => f.estado === 'omitida').length} omitidas`);
    for (const f of filas) console.log(`  ${f.estado.padEnd(8)} ${f.prueba}${f.error ? `\n           ${f.error}` : ''}`);
    console.log(`[regresion] Resumen en ${path.relative(actual.raiz, destino)}`);
  }
} finally {
  if (conservar) {
    console.log(`[regresion] Se conserva ${wt} y la base ${db}.`);
  } else {
    // Lección 013 F-03: borrar el worktree con la unión adentro vació node_modules de la carpeta principal. Primero se
    // quita la unión (solo el enlace, no recursivo) y se comprueba que el original sigue ahí.
    if (creado.union && existsSync(modulos)) {
      try { unlinkSync(modulos); } catch { rmdirSync(modulos); }
    }
    if (!existsSync(path.join(actual.raiz, 'web', 'node_modules', '.bin'))) {
      console.error('[regresion] ¡node_modules de la carpeta actual ya no está! No se borra el worktree; revisa antes de seguir.');
      process.exit(3);
    }
    if (creado.worktree) git('worktree', 'remove', '--force', wt);
    else if (existsSync(wt)) rmdirSync(wt); // solo la carpeta vacía que reservó mkdtemp
    if (creado.base) {
      try { borrarBase(db); } catch (e) { console.error(`[regresion] No se pudo borrar la base ${db}: ${e.message}`); }
    }
  }
}
process.exit(status);

/** Una fila por prueba: archivo › título, estado y la primera línea del error (sin colores de la terminal). */
function resumir(reporte) {
  const filas = [];
  const recorrer = (suite, ruta) => {
    for (const s of suite.suites ?? []) recorrer(s, s.title && !s.title.endsWith('.ts') ? [...ruta, s.title] : ruta);
    for (const spec of suite.specs ?? []) {
      for (const t of spec.tests ?? []) {
        if (t.projectName === 'setup') continue;
        const ultimo = t.results?.at(-1);
        const estado = t.status === 'expected' ? 'correcta' : t.status === 'skipped' ? 'omitida' : 'FALLA';
        const error = estado === 'FALLA'
          ? (ultimo?.error?.message ?? '').replace(/\u001b\[[0-9;]*m/g, '').split('\n').map((l) => l.trim()).find(Boolean) ?? ''
          : '';
        filas.push({ prueba: [spec.file, ...ruta, spec.title].join(' › '), estado, error });
      }
    }
  };
  for (const s of reporte.suites ?? []) recorrer(s, []);
  return filas;
}
