// Spec 028: instancia para la revisión exploratoria de VERIFY, sobre una base desechable propia y un puerto libre, con las
// opciones de la suite (aprobación de otro administrador, paquetes firmados y cambios directos permitidos).
// Uso: npm run revision [-- --conservar] [-- --sin-compilar]
//   --conservar     no recrea la base (y reutiliza la contraseña guardada); sin él, la base se recrea.
//   --sin-compilar  no recompila la SPA (web/dist).
// La contraseña del administrador se genera y se guarda FUERA del repositorio (%LOCALAPPDATA%/ErpAi/revision/<base>.json).
// Ctrl+C detiene la instancia (si este proceso se mata desde fuera, detener también Erp.Api.exe). Si el servidor de
// desarrollo (erp-api) corre en esta carpeta, detenlo antes: dotnet run compila y Erp.Api.exe estaría bloqueado.
import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { SQL_SERVER, baseDeCarpeta, bloqueDePuertos, borrarBase, carpeta, tomarCandado, validarBase } from './instancia.mjs';

const args = process.argv.slice(2);
const conservar = args.includes('--conservar');
const c = carpeta(import.meta.dirname);
const db = validarBase(process.env.E2E_DB ?? baseDeCarpeta(c, 'Revision'));
try { tomarCandado(db); } catch (e) { console.error(`[revision] ${e.message}`); process.exit(1); }
// 5950: fuera de los bloques de los worktrees (5100–5899), para no tomar un puerto auxiliar de otra suite (revisión 028, F-09).
const puerto = await bloqueDePuertos({ fijo: process.env.REVISION_PORT, preferido: 5950, cuantos: 1 });

const dirSecretos = path.join(process.env.LOCALAPPDATA ?? path.join(homedir(), '.local', 'share'), 'ErpAi', 'revision');
const archivo = path.join(dirSecretos, `${db}.json`);
if (conservar && !existsSync(archivo)) {
  // Revisión 028 (F-08): sin la contraseña guardada no se puede entrar a la base conservada; nunca se borra en su lugar.
  console.error(`[revision] --conservar: no está ${archivo}. Corre sin --conservar para recrear la base.`);
  process.exit(2);
}
let secretos = conservar ? JSON.parse(readFileSync(archivo, 'utf8')) : null;
if (!secretos) {
  console.log(`[revision] Recreando base ${db} en ${SQL_SERVER}`);
  borrarBase(db);
  secretos = { password: `Rev-${randomBytes(12).toString('base64url')}`, signingKey: randomBytes(32).toString('base64url') };
  mkdirSync(dirSecretos, { recursive: true });
  writeFileSync(archivo, JSON.stringify(secretos), { mode: 0o600 });
}

if (!args.includes('--sin-compilar')) {
  console.log('[revision] Compilando SPA');
  execFileSync('npm', ['run', 'build'], { stdio: 'inherit', shell: true, cwd: path.join(c.raiz, 'web') });
}

const url = `http://localhost:${puerto}`;
console.log(`\n[revision] ${url}  ·  usuario: admin  ·  contraseña en ${archivo}  ·  base ${db}`);
console.log('[revision] Ctrl+C la detiene. Arrancando (compila la API la primera vez)…\n');
const api = spawn('dotnet', ['run', '--project', path.join(c.raiz, 'src', 'Erp.Api'), '--no-launch-profile', '--urls', url], {
  stdio: 'inherit',
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Development',
    ConnectionStrings__Erp: `Server=${SQL_SERVER};Database=${db};Integrated Security=true;Encrypt=true;TrustServerCertificate=true`,
    Database__AutoCreate: 'true',
    Bootstrap__AdminPassword: secretos.password,
    Environments__Name: 'Revisión exploratoria',
    Environments__RequireApproval: 'true',
    Environments__SigningKey: secretos.signingKey,
    Environments__AllowDirectChanges: 'true',
    Spa__Path: path.join(c.raiz, 'web', 'dist'),
  },
});
// dotnet run deja a Erp.Api.exe como proceso hijo: matar solo a dotnet lo dejaría huérfano ocupando el puerto y bloqueando
// la compilación. En Windows se detiene el árbol completo.
const detener = () => {
  if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(api.pid), '/T', '/F'], { stdio: 'ignore' });
  else api.kill();
};
for (const s of ['SIGINT', 'SIGTERM', 'SIGBREAK']) process.on(s, detener);
api.on('exit', (code) => process.exit(code ?? 0));
