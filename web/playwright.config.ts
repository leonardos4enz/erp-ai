import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';

const PORT = 5090;
const root = path.resolve(import.meta.dirname, '..');
const db = process.env.E2E_DB ?? 'ErpAi_E2E';
const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';

if (!process.env.E2E_ADMIN_PASSWORD || !process.env.E2E_SIGNING_KEY) {
  throw new Error('Ejecuta la suite con "npm run e2e" (prepara la base y la contraseña de prueba).');
}

export default defineConfig({
  testDir: './e2e',
  outputDir: './e2e/.results',
  snapshotPathTemplate: '{testDir}/__screenshots__/{testFilePath}/{arg}{ext}',
  fullyParallel: false, // comparten una sola base: orden determinista
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
      // Spec 009: la suite se comporta como producción (aprobación de otro administrador y paquetes firmados).
      Environments__Name: 'Producción E2E',
      Environments__RequireApproval: 'true',
      Environments__SigningKey: process.env.E2E_SIGNING_KEY ?? '',
      // Spec 012: con aprobación, el modelo solo cambia por paquetes; la suite prepara sus datos con planes, así que lo
      // permite de forma explícita. endurecimiento.spec.ts levanta otra instancia sin esta opción (:5092) y prueba el bloqueo.
      Environments__AllowDirectChanges: 'true',
      // Spec 011: el asistente habla con un proveedor simulado (asistente.spec.ts) en loopback. La clave se guarda desde la
      // app en la prueba y es ficticia: la suite nunca usa una clave real de Anthropic.
      Assistant__BaseUrl: 'http://127.0.0.1:5091',
      Assistant__RequestsPerHour: '30',
      Spa__Path: path.join(root, 'web', 'dist'),
    },
  },
});
