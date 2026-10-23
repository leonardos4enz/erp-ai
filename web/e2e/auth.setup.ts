import { test as setup } from '@playwright/test';
import { ADMIN, uiLogin } from './helpers';

// Inicia sesión una vez como admin por la UI y guarda la sesión para el resto de las pruebas.
setup('sesión de administrador', async ({ page }) => {
  await uiLogin(page, ADMIN.username, ADMIN.password);
  await page.context().storageState({ path: 'e2e/.auth/admin.json' });
});
