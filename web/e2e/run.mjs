// Ejecuta la suite E2E contra una base de datos desechable.
// 1) recrea la base de la carpeta  2) compila la SPA  3) corre Playwright (que levanta la API en el puerto de la carpeta).
// Uso: npm run e2e [-- argumentos de playwright, p. ej. --ui, --update-snapshots o e2e/archivo.spec.ts --repeat-each=3]
// Spec 028: la carpeta principal usa ErpAi_E2E y el puerto 5090; un worktree, su propia base y su propio bloque de
// puertos (ver instancia.mjs). E2E_DB y E2E_PORT_BASE los fijan a mano.
import { execFileSync, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { SQL_SERVER, baseDeCarpeta, bloqueDePuertos, borrarBase, carpeta, puertoPreferido, tomarCandado, validarBase } from './instancia.mjs';

const dir = import.meta.dirname;
const web = path.dirname(dir);

// Spec 028: todo archivo serial admite E2E_INDEPENDENT=1 (verificar contra el código anterior sin que una falla salte el
// resto). Un `mode: 'serial'` fijo lo impediría.
const fijos = readdirSync(dir).filter((f) => f.endsWith('.ts') && /mode:\s*['"`]serial['"`]/.test(readFileSync(path.join(dir, f), 'utf8')));
if (fijos.length) {
  console.error(`[e2e] Estos archivos fijan mode: 'serial'. Usa mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial':\n  ${fijos.join('\n  ')}`);
  process.exit(1);
}

const c = carpeta(dir);
const db = validarBase(process.env.E2E_DB ?? baseDeCarpeta(c));
try { tomarCandado(db); } catch (e) { console.error(`[e2e] ${e.message}`); process.exit(1); }
const puerto = await bloqueDePuertos({ fijo: process.env.E2E_PORT_BASE, preferido: puertoPreferido(c) });

// Spec 028: --veces=N corre lo pedido N veces, cada una sobre una base nueva (se compila una sola vez). Sirve para «lo
// nuevo, repetido» al cerrar: --repeat-each repite dentro de la misma base y choca con las pruebas «preparar» que crean
// datos con nombre fijo (casi todos los archivos seriales).
const veces = Number(process.argv.find((a) => a.startsWith('--veces='))?.slice('--veces='.length) ?? 1);
if (!Number.isInteger(veces) || veces < 1 || veces > 10) { console.error('[e2e] --veces debe ser un entero entre 1 y 10'); process.exit(2); }
const argsPlaywright = process.argv.slice(2).filter((a) => !a.startsWith('--veces='));

console.log(`[e2e] Recreando base ${db} en ${SQL_SERVER}; API en el puerto ${puerto} (auxiliares ${puerto + 1}–${puerto + 4})`);
borrarBase(db);

// Revisión adversarial 028 (F-04): tsc y Playwright se ejecutan con node y una lista de argumentos, sin shell. Con shell,
// un argumento con «&», «|» o «%VAR%» (p. ej. un nombre de archivo que llega de git diff en e2e:regresion) se interpretaba.
const node = (modulo, args, opciones = {}) => spawnSync(process.execPath, [path.join(web, 'node_modules', ...modulo), ...args], { stdio: 'inherit', cwd: web, ...opciones });

console.log('[e2e] Revisando tipos de las pruebas');
if (node(['typescript', 'bin', 'tsc'], ['-p', 'e2e']).status !== 0) process.exit(1);

console.log('[e2e] Compilando SPA');
execFileSync('npm', ['run', 'build'], { stdio: 'inherit', shell: true, cwd: web }); // argumentos fijos

// Contraseña nueva por corrida: nunca queda guardada en el repositorio.
const env = {
  ...process.env,
  E2E_DB: db,
  E2E_SQL_SERVER: SQL_SERVER,
  E2E_PORT_BASE: String(puerto),
  E2E_ADMIN_PASSWORD: process.env.E2E_ADMIN_PASSWORD ?? `E2e-${randomBytes(12).toString('base64url')}`,
  // Spec 009: clave de firma de paquetes, también nueva por corrida.
  E2E_SIGNING_KEY: process.env.E2E_SIGNING_KEY ?? randomBytes(32).toString('base64url'),
};
for (let i = 1; i <= veces; i++) {
  if (i > 1) {
    console.log(`[e2e] Vuelta ${i} de ${veces}: recreando base ${db}`);
    borrarBase(db);
  }
  const result = node(['@playwright', 'test', 'cli.js'], ['test', ...argsPlaywright], { env });
  if (result.status !== 0) {
    if (veces > 1) console.error(`[e2e] Falló en la vuelta ${i} de ${veces}.`);
    process.exit(result.status ?? 1);
  }
}
if (veces > 1) console.log(`[e2e] ${veces} vueltas en verde.`);
