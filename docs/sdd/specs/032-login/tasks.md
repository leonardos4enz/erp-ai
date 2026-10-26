# Tareas 032 — Rediseño del inicio de sesión

- [x] T1 Comprobar `git rev-list --count origin/main..main` = 0 y CI en verde en `main` (71246b2).
- [x] T2 Medir el bundle antes (`npm --prefix web run build`).
- [x] T3 Spec (referencias, dirección visual, CLARIFY) y plan (contrato sin cambios, STRIDE de 8 filas).
- [x] T4 `components/Brand.tsx`: isotipo e ilustración SVG.
- [x] T5 `styles/login.css` y tokens `--brand-*`; quitar `.login-*` de `app.css`.
- [x] T6 `LoginPage.tsx`: composición, contraseña visible, Bloq Mayús, errores de campo, foco, mensajes por estado.
- [x] T7 `AuthContext`: motivo de fin de sesión (`expired`/`signedOut`) en memoria.
- [x] T8 Integración: 429 real del login y bloqueo igual a inexistente.
- [x] T9 `e2e/login.spec.ts` (H1–H4, CSP) y capturas en `visual.spec.ts` (claro, oscuro, móvil, error).
- [x] T10 Revisar cada captura nueva; `e2e:regresion` contra 71246b2.
- [x] T11 Revisión adversarial independiente; hallazgos F-xx.
- [x] T12 Corrida completa + `--veces=3`; medir el bundle después; `verify.md`; riesgos; roadmap (Ventas 033, CFDI 034).
- [x] T13 Commit y push; CI en verde.
