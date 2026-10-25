import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';

// Spec 028: el puerto y la base salen de la carpeta (run.mjs, instancia.mjs); 5090 y ErpAi_E2E en la carpeta principal.
const PORT = Number(process.env.E2E_PORT_BASE ?? 5090);
const root = path.resolve(import.meta.dirname, '..');
const db = process.env.E2E_DB ?? 'ErpAi_E2E';
const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';

// Revisión adversarial 028 (F-07): también al correr Playwright directo, nunca contra una base que no sea desechable (la
// del dueño es ErpAi). Mismo criterio que instancia.mjs.
if (!/^[A-Za-z0-9_]{1,100}$/.test(db) || !db.endsWith('_E2E')) throw new Error(`E2E_DB debe terminar en _E2E (recibido: ${db}).`);
if (!process.env.E2E_ADMIN_PASSWORD || !process.env.E2E_SIGNING_KEY) {
  throw new Error('Ejecuta la suite con "npm run e2e" (prepara la base y la contraseña de prueba).');
}

export default defineConfig({
  testDir: './e2e',
  outputDir: './e2e/.results',
  snapshotPathTemplate: '{testDir}/__screenshots__/{testFilePath}/{arg}{ext}',
  fullyParallel: false, // comparten una sola base y unos archivos dependen de otros (spec 028): orden determinista
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { outputFolder: 'e2e/.report', open: 'never' }]],
  expect: { toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled' } },
  use: {
    baseURL: `http://localhost:${PORT}`,
    locale: 'es-MX',
    timezoneId: 'America/Mexico_City',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      dependencies: ['setup'],
      use: { ...devices['Desktop Chrome'], storageState: 'e2e/.auth/admin.json', viewport: { width: 1280, height: 800 } },
    },
  ],
  webServer: {
    command: `dotnet run --project "${path.join(root, 'src', 'Erp.Api')}" --no-launch-profile --urls http://localhost:${PORT}`,
    url: `http://localhost:${PORT}/api/health`,
    reuseExistingServer: false,
    timeout: 180_000,
    stdout: 'ignore',
    stderr: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ConnectionStrings__Erp: `Server=${server};Database=${db};Integrated Security=true;Encrypt=true;TrustServerCertificate=true`,
      Database__AutoCreate: 'true',
      Bootstrap__AdminPassword: process.env.E2E_ADMIN_PASSWORD,
      Security__LoginAttemptsPerMinute: '1000',
      // La suite crece con cada spec y como administrador rebasa el límite global de 600 por minuto (no así una persona).
      Security__RequestsPerMinute: '5000',
      // Spec 027: la suite descarga más PDF por minuto que una persona.
      Security__PrintsPerMinute: '1000',
      // Spec 029: exportar e importar como lo hace la suite; la búsqueda se deja baja para probar su 429 (capacidades.spec.ts).
      Security__BulkPerMinute: '1000',
      Security__SearchesPerMinute: '120',
      // Spec 031: tableros, reportes y OData como los recorre la suite (en producción, 120 por minuto).
      Security__AggregatesPerMinute: '1000',
      // Spec 008: el receptor de webhooks de la suite escucha en 127.0.0.1 por http; ninguna otra IP interna se permite.
      Webhooks__AllowHttp: 'true',
      Webhooks__AllowedInternalAddresses: '127.0.0.1',
      Webhooks__PollSeconds: '1',
      Webhooks__RetryDelaysSeconds: '1,1',
      // Spec 016: la bandeja de automatizaciones revisa cada segundo y reintenta lo transitorio sin esperar minutos.
      Automations__PollSeconds: '1',
      Automations__RetryDelaysSeconds: '1,1',
      // Spec 018: páginas de OData de 3 filas para probar @odata.nextLink sin crear cientos de registros.
      OData__MaxPageSize: '3',
      // Spec 021: tope de partidas expandidas por página, bajo para probarlo con un documento de 11 partidas.
      OData__MaxExpandedLines: '10',
      // Spec 009: la suite se comporta como producción (aprobación de otro administrador y paquetes firmados).
      Environments__Name: 'Producción E2E',
      Environments__RequireApproval: 'true',
      Environments__SigningKey: process.env.E2E_SIGNING_KEY ?? '',
      // Spec 012: con aprobación, el modelo solo cambia por paquetes; la suite prepara sus datos con planes, así que lo
      // permite de forma explícita. endurecimiento.spec.ts levanta otra instancia sin esta opción (puerto +2) y prueba el bloqueo.
      Environments__AllowDirectChanges: 'true',
      // Spec 011: el asistente habla con un proveedor simulado (asistente.spec.ts) en loopback. La clave se guarda desde la
      // app en la prueba y es ficticia: la suite nunca usa una clave real de Anthropic.
      Assistant__BaseUrl: `http://127.0.0.1:${PORT + 1}`,
      Assistant__RequestsPerHour: '30',
      Spa__Path: path.join(root, 'web', 'dist'),
    },
  },
});
