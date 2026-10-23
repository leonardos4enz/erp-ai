// Ejecuta la suite E2E contra una base de datos desechable.
// 1) recrea ErpAi_E2E  2) compila la SPA  3) corre Playwright (que levanta la API en :5090).
// Uso: npm run e2e [-- argumentos de playwright, p. ej. --ui o --update-snapshots]
import { execFileSync, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';

const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';
const db = 'ErpAi_E2E';
if (!db.endsWith('_E2E')) throw new Error('Por seguridad solo se recrean bases que terminan en _E2E.');

console.log(`[e2e] Recreando base ${db} en ${server}`);
execFileSync('sqlcmd', ['-S', server, '-E', '-b', '-Q',
  `IF DB_ID('${db}') IS NOT NULL BEGIN ALTER DATABASE [${db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${db}]; END`,
], { stdio: 'inherit' });

console.log('[e2e] Revisando tipos de las pruebas');
execFileSync('npx', ['tsc', '-p', 'e2e'], { stdio: 'inherit', shell: true });

console.log('[e2e] Compilando SPA');
execFileSync('npm', ['run', 'build'], { stdio: 'inherit', shell: true });

// Contraseña nueva por corrida: nunca queda guardada en el repositorio.
const env = {
  ...process.env,
  E2E_DB: db,
  E2E_SQL_SERVER: server,
  E2E_ADMIN_PASSWORD: process.env.E2E_ADMIN_PASSWORD ?? `E2e-${randomBytes(12).toString('base64url')}`,
};
const result = spawnSync('npx', ['playwright', 'test', ...process.argv.slice(2)], { stdio: 'inherit', shell: true, env });
process.exit(result.status ?? 1);
