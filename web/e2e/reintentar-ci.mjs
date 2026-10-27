// Spec 034 (H7, R-030-5): en CI, tras una falla de la suite E2E, repite solo los archivos que fallaron (con los que su
// encabezado dice que deben correr antes) sobre una base nueva, para saber si la falla es reproducible o intermitente.
// Es detección, no reintento: el trabajo sigue en rojo y la spec se reabre igual; la anotación dice qué fue.
// Uso: node web/e2e/reintentar-ci.mjs <salida de la suite>
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, renameSync, rmSync } from 'node:fs';
import path from 'node:path';

const dir = import.meta.dirname;
const suite = new Set(readdirSync(dir).filter((f) => f.endsWith('.spec.ts')));

/** Los archivos que el encabezado de uno dice que corren antes («Corre después de compras.spec.ts», «depende de …»). */
function dependencias(nombre, leer = (f) => readFileSync(path.join(dir, f), 'utf8')) {
  const vistos = new Set();
  const visitar = (f) => {
    if (vistos.has(f) || !suite.has(f)) return; // nombre exacto (en Windows existsSync no distingue mayúsculas)
    vistos.add(f);
    const encabezado = leer(f).split(/\r?\n/).filter((l) => l.startsWith('//')).slice(0, 12).join(' ');
    for (const frase of encabezado.matchAll(/(?:después de|depende de|dependen? de)([^.]*(?:\.spec\.ts[^.]*)*)/gi))
      // «Corre después de a.spec.ts … y antes de b.spec.ts»: b no es dependencia. «después de compras e inventario»
      // nombra archivos sin extensión: cuenta toda palabra que sea un archivo de la suite.
      for (const m of frase[1].split(/\bantes\b/i)[0].matchAll(/[\w-]+(?:\.spec\.ts)?/g))
        visitar(m[0].endsWith('.spec.ts') ? m[0] : `${m[0]}.spec.ts`);
  };
  visitar(nombre);
  return [...vistos];
}


// --dependencias <archivo>: solo imprime qué se repetiría (para revisar los encabezados).
if (process.argv[2] === '--dependencias') {
  console.log(dependencias(process.argv[3]).join(' '));
  process.exit(0);
}
const [archivo] = process.argv.slice(2);
const avisar = (nivel, titulo, texto) => console.log(`::${nivel} title=${titulo}::${texto.replace(/%/g, '%25').replace(/\r?\n/g, '%0A')}`);
if (!archivo || !existsSync(archivo)) {
  avisar('warning', 'E2E: sin reintento', 'No hay salida de la suite que revisar.');
  process.exit(0);
}

// Playwright: «  1) [chromium] › e2e\a.spec.ts:10:1 › título».
const salida = readFileSync(archivo, 'utf8').replace(/\u001b\[[0-9;]*m/g, '');
const fallidos = [...new Set([...salida.matchAll(/^\s+\d+\) \[\w+\] › e2e[\\/]([\w.-]+\.spec\.ts):\d+/gm)].map((m) => m[1]))];
if (fallidos.length === 0) {
  avisar('warning', 'E2E: sin reintento', 'La salida no nombra archivos fallidos (¿falló antes de correr las pruebas?).');
  process.exit(0);
}
if (fallidos.length > 5) {
  avisar('warning', 'E2E: sin reintento', `Fallaron ${fallidos.length} archivos: parece una falla general, no una intermitente. ${fallidos.join(', ')}`);
  process.exit(0);
}

const archivos = [...new Set(fallidos.flatMap((f) => dependencias(f)))].sort();
// La primera corrida conserva su video y su trace: el artefacto sube las dos.
for (const d of ['.results', '.report']) {
  const origen = path.join(dir, d);
  const destino = path.join(dir, `${d}-primera`);
  if (existsSync(origen)) { rmSync(destino, { recursive: true, force: true }); renameSync(origen, destino); }
}
console.log(`[reintento] Repitiendo sobre base nueva: ${archivos.join(' ')}`);
const r = spawnSync(process.execPath, [path.join(dir, 'run.mjs'), ...archivos.map((f) => `e2e/${f}`)], { stdio: 'inherit', cwd: path.dirname(dir) });
if (r.status === 0)
  avisar('warning', 'E2E: intermitente',
    `${fallidos.join(', ')} falló en la suite completa y pasó al repetirse sobre una base nueva (con ${archivos.join(', ')}). ` +
    'Es intermitente: anótala en R-030-5 con el motivo de la primera falla (anotaciones de arriba).');
else
  avisar('error', 'E2E: reproducible', `${fallidos.join(', ')} volvió a fallar sobre una base nueva (con ${archivos.join(', ')}): es reproducible.`);
process.exit(0);
