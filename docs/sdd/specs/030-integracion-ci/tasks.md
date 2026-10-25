# Tasks 030 — Integración y CI

## 0. Apertura
- [x] `git rev-list --count origin/main..main` = 0.
- [x] Suite completa como segunda corrida de la 029: 628 unitarias y 400 E2E en verde (7.6 min).
- [x] CLARIFY: no se divide; runner Windows con LocalDB (comprobado en el README de la imagen `windows-2025`).

## 1. H1 — Arnés de integración (P5)
- [x] `tests/Erp.IntegrationTests` en `ErpAi.slnx`; `Harness/ErpHost.cs` (base por clase validada, `UseSetting`,
      `SiblingAsync`), `Harness/Client.cs`, `Harness/Concurrency.cs` (`Concurrently`, `Invariant`, `Faults`).
- [x] `HarnessTests`: bases desechables, configuración que llega a `AddErp`, barrera de `Concurrently`, fallas que se deshacen.

## 2. H2 — Pruebas de API movidas y regresiones
- [x] `ApiSecurityTests` (19, de `api-security.spec.ts`), `Hardening012Tests` (9, de `endurecimiento.spec.ts`),
      `Hardening021Tests` (14, de `endurecimiento-2.spec.ts`, más dos instancias sobre una bandeja), `PendingTests` (8, de
      `pendientes.spec.ts`); los archivos E2E conservan solo lo de UI.
- [x] `tests/Erp.IntegrationTests/regresion.mjs` (`npm --prefix web run it:regresion`).
- [x] 012-3 falla con `cd81760` (nueve 500 y recepción sobre orden cancelada); 021-3 falla con `f6c5445`.
- [x] R-029-12: `ModuleRaceTests` (invariante; la mutación sin candado no se detecta: el riesgo sigue abierto).

## 3. H3 — CI (P8)
- [x] `.github/workflows/ci.yml`: rápido en `ubuntu-latest`; integración y E2E en `windows-2025` en `main` o a mano.
- [ ] Trabajo rápido en Linux: no se pudo probar localmente (Docker Desktop detenido y sin distribución de WSL); sus pasos sí corrieron en Windows. Lo prueba el push.
- [ ] Primera corrida en GitHub tras el push (registrada en `verify.md`).
- [x] `verificacion.md` y `CLAUDE.md`: CI es la segunda corrida completa; si falla, la spec se reabre.

## 4. H4 — Vitest (P14)
- [x] `npm --prefix web test`; `lib/canonical.ts` y `completeAfterChoice` extraídos para probarlos sin React.
- [x] `layoutEdit`, `workflowEdit`, `conditionBuilder`, `exprComplete`, `canonical`; 026 F-03 falla al quitar el filtro de nulos.

## 5. H5 — Guardias (P9)
- [x] `helpers.ts`: `desborde`, `alerta`, `foco` (opción `guardias` por archivo).
- [x] `src/__tests__/pantallas.test.ts`: rutas de `App.tsx` contra capturas o razón escrita.
- [x] Lo que encontraron en la suite actual, corregido o declarado (ver `verify.md`).
- [x] Cada guardia demostrada contra el código anterior a su hallazgo de origen.

## 6. H6 — Riesgos de la suite
- [x] R-028-2 y R-028-3 con estado y razón actualizados.

## 7. VERIFY
- [x] Corrida completa + `--veces=3` de lo modificado; revisión adversarial (F-A…F-L corregidos o documentados); `verify.md`,
      riesgos, roadmap; commit y push.
