# Tasks 035 — ERP AI Studio: pantalla

## 0. Apertura
- [x] `git rev-list --count origin/main..main` = 0 (`9e70886`).
- [x] Última corrida de CI en `main` (`9e70886`): verde.
- [x] Contrato de la 033 (`plan.md` §2) y diferidos (R-033-4, R-033-10, R-033-11) releídos en el código.

## 1. API (H5)
- [x] `StudioTools.DataTools`: nombres de las herramientas del alcance `datos`, de `StudioTools.All`.
- [x] Purga de sus salidas en `StudioAgent.FinishAsync` y `StudioService.ExpireAsync`, en la transacción del cierre.
- [x] `GetAsync(after)`: con `after` no compila (sin plan, vista previa ni huella).
- [x] `StudioTests.Pantalla.cs`: H5 CA1 (cierre e interrupción), H5 CA2; contra el código anterior con `it:regresion`.

## 2. SPA
- [x] Tipos y `studioApi` (`types.ts`, `endpoints.ts`).
- [x] `lib/studio.ts` + `studio.test.ts` (Vitest).
- [x] `StudioPage.tsx`: historial, composer con alcances, sesión en vivo, consumo, pasos, propuesta y aplicar.
- [x] Ruta `admin/studio`, `adminNav.ts`, `pantallas.test.ts`, estilos `studio-*`.

## 3. E2E
- [x] `studio.spec.ts` con proveedor simulado (un escenario por CA, axe, capturas claro/oscuro/móvil revisadas).
- [x] `Studio__PollSeconds` y `Studio__RenewSeconds` en `playwright.config.ts`.
- [x] `navegacion.spec.ts › H3 CA1` con la opción nueva; `configuracion-*` no se regeneró (pasa por tolerancia; ver verify §2).

## 4. Hallazgos de VERIFY
- [x] F-01 purga al arrancar · F-02 aviso y R-035-1 · F-03 prueba de atomicidad · F-04 lista desde el catálogo · F-05 sin
      duplicados al sondear (revisión adversarial).
- [x] F-06 copia aislada por tramos (corrida completa) · F-07 fechas UTC · F-08 409 espera la lectura nueva · F-09 historial
      al cambiar de estado · F-10 resumen de paso en móvil.

## 5. VERIFY
- [x] Corrida completa, `--veces=3 --lento` de lo nuevo, revisión adversarial, revisión exploratoria, `verify.md`, riesgos,
      roadmap e índice; commit y push.
