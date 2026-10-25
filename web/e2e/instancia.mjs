// Spec 028: base de datos y puertos por carpeta, para que varias sesiones corran su suite a la vez sin chocar.
// La carpeta principal conserva ErpAi_E2E y el puerto 5090; un worktree usa ErpAi_<worktree>_E2E y un bloque de puertos
// propio. Lo usan run.mjs, regresion.mjs y revision.mjs.
import { execFileSync } from 'node:child_process';
import { closeSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';

export const SQL_SERVER = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';
/** Puertos por corrida: +0 API, +1 proveedor simulado del asistente, +2 producción (012), +3 seguridad (015), +4 seguridad (021). */
export const PUERTOS_POR_BLOQUE = 10;

const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();

/** Raíz de la carpeta de trabajo y si es un worktree enlazado (no la carpeta principal del repositorio). */
export function carpeta(cwd = process.cwd()) {
  const raiz = path.resolve(git(cwd, 'rev-parse', '--show-toplevel'));
  const gitDir = path.resolve(cwd, git(cwd, 'rev-parse', '--git-dir'));
  const comun = path.resolve(cwd, git(cwd, 'rev-parse', '--git-common-dir'));
  const worktree = gitDir !== comun;
  // Revisión adversarial 028 (F-05): «feat-a», «feat.a» y «feat_a», o dos worktrees con el mismo nombre en rutas distintas,
  // daban la misma base. La huella de la ruta completa (en minúsculas: SQL Server y Windows no distinguen) los separa.
  const legible = path.basename(raiz).replace(/[^A-Za-z0-9]/g, '_').replace(/_+/g, '_').replace(/^_|_$/g, '').slice(0, 30) || 'wt';
  const nombre = `${legible}_${hash(raiz.toLowerCase()).toString(36)}`;
  return { raiz, worktree, nombre };
}

/**
 * Nombre de base desechable. Solo se aceptan nombres que terminan en _E2E y con [A-Za-z0-9_]: el nombre se interpola en
 * SQL (DROP DATABASE) y así nunca puede ser la base del dueño (ErpAi) ni llevar comillas o corchetes.
 */
export function validarBase(db) {
  if (!/^[A-Za-z0-9_]{1,100}$/.test(db) || !db.endsWith('_E2E')) {
    throw new Error(`Por seguridad solo se usan bases que terminan en _E2E y con letras, números o «_» (recibido: ${JSON.stringify(db)}).`);
  }
  return db;
}

/** Base por defecto: ErpAi[_<worktree>][_<sufijo>]_E2E; E2E_DB la fija a mano. */
export function baseDeCarpeta(c, sufijo = '') {
  const partes = ['ErpAi', ...(c.worktree ? [c.nombre] : []), ...(sufijo ? [sufijo] : []), 'E2E'];
  return validarBase(partes.join('_'));
}

function libreEn(puerto, host) {
  return new Promise((resolve) => {
    const s = createServer();
    s.once('error', () => resolve(false));
    s.listen(puerto, host, () => s.close(() => resolve(true)));
  });
}

// Sin host se detecta lo que escucha en «localhost» por IPv6 (Kestrel con --urls http://localhost:N); con 127.0.0.1, lo
// que escucha solo ahí (el proveedor simulado del asistente). Revisión adversarial 028 (F-12): sin lo segundo, un puerto
// ocupado solo en 127.0.0.1 se daba por libre.
const libre = async (puerto) => (await libreEn(puerto, '127.0.0.1')) && (await libreEn(puerto));

async function bloqueLibre(base, cuantos) {
  for (let p = base; p < base + cuantos; p++) if (!(await libre(p))) return false;
  return true;
}

function hash(texto) {
  let h = 0;
  for (const ch of texto) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return h;
}

/**
 * Primer puerto de un bloque libre de `cuantos`. Si `fijo` viene (E2E_PORT_BASE), se usa ese o se falla; si no, se parte de
 * `preferido` y se avanza de bloque en bloque.
 */
export async function bloqueDePuertos({ fijo, preferido, cuantos = PUERTOS_POR_BLOQUE, intentos = 80 }) {
  if (fijo) {
    const base = Number(fijo);
    if (!Number.isInteger(base) || base < 1024 || base + cuantos > 65535) throw new Error(`E2E_PORT_BASE inválido: ${fijo}`);
    if (!(await bloqueLibre(base, cuantos))) throw new Error(`Algún puerto entre ${base} y ${base + cuantos - 1} está ocupado (E2E_PORT_BASE=${fijo}).`);
    return base;
  }
  for (let i = 0, base = preferido; i < intentos; i++, base += cuantos) {
    if (base + cuantos > 65535) base = 5100;
    if (await bloqueLibre(base, cuantos)) return base;
  }
  throw new Error(`No hay un bloque de ${cuantos} puertos libre a partir de ${preferido}. Fija uno con E2E_PORT_BASE.`);
}

/** Puerto preferido de la carpeta: 5090 la principal; un worktree, un bloque entre 5100 y 5890 según su nombre. */
export const puertoPreferido = (c) => (c.worktree ? 5100 + (hash(c.nombre) % 80) * PUERTOS_POR_BLOQUE : 5090);

/**
 * Candado exclusivo por base (revisión adversarial 028, F-05/F-06): dos corridas que resuelven la misma base (dos sesiones
 * en la misma carpeta, un segundo clon) no se la borran una a la otra a mitad de la corrida. Un candado de un proceso que
 * ya no existe se toma. Devuelve la función que lo suelta (también se suelta al salir).
 */
export function tomarCandado(db) {
  const archivo = path.join(tmpdir(), `erp-e2e-${db.toLowerCase()}.lock`);
  for (let intento = 0; intento < 2; intento++) {
    try {
      const fd = openSync(archivo, 'wx');
      writeFileSync(fd, String(process.pid));
      closeSync(fd);
      const soltar = () => { try { if (readFileSync(archivo, 'utf8') === String(process.pid)) rmSync(archivo); } catch { /* ya no está */ } };
      process.on('exit', soltar);
      return soltar;
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      const pid = Number(readFileSync(archivo, 'utf8'));
      let vivo = false;
      try { process.kill(pid, 0); vivo = true; } catch { /* el proceso ya no existe */ }
      if (vivo) throw new Error(`La base ${db} la está usando otra corrida (PID ${pid}, ${archivo}). Espera a que termine o usa otra carpeta.`);
      rmSync(archivo, { force: true });
    }
  }
  throw new Error(`No se pudo tomar el candado de ${db}.`);
}

function sql(query) {
  execFileSync('sqlcmd', ['-S', SQL_SERVER, '-E', '-b', '-Q', query], { stdio: 'inherit' });
}

export function borrarBase(db) {
  validarBase(db);
  sql(`IF DB_ID('${db}') IS NOT NULL BEGIN ALTER DATABASE [${db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${db}]; END`);
}
