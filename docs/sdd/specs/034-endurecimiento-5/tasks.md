# Tasks 034 — Endurecimiento 5

## 0. Apertura
- [x] `git rev-list --count origin/main..main` = 0 (`ad0534b`).
- [x] Última corrida de CI en `main` (`ad0534b`): verde.
- [x] Hallazgos de la auditoría 6 reconfirmados en el código (A-01 a A-07, R-009-1, R-033-5).
- [x] Roadmap renumerado (035 pantalla de Studio, 036 MCP, 037 Ventas, 038 CFDI 4.0) y referencias en ADR-0008, riesgos y spec 033.

## 1. H1 — Nadie actúa sobre quien tiene más (A-01)
- [x] `RequireActOnAsync` en `UpdateUserCore`: contraseña, activación, unidad o empresas sobre un destino con capacidades
      que el actor no tiene → 403; antes de la solicitud y al aprobar. Revisión adversarial F-01: también cambiar sus roles.
- [x] `Hardening034Tests`: API y aprobación.

## 2. H2 — Clave de firma obligatoria con aprobación (R-009-1)
- [x] `EnvironmentOptions.Create` lo exige; unitarias que creaban «producción» sin clave, con clave.

## 3. H3 — Catálogo de Studio (A-02, A-03, A-04, R-033-5)
- [x] `StudioTool.Capability`: la publicada; el runner la exige para `c.User`.
- [x] `BearerScope.Check` (función pura) usada por `UseBearerScope` y por el despachador; `ToolContext.Credential`.
- [x] `Effect.AuditTrail` en `/pdf` y `/export`; `Writes` por el efecto; inventario regenerado; GET recorridos.
- [x] `DispatchLimits` con los topes de `RatePolicies`.

## 4. H4 — Bitácora (A-07, R-032-6)
- [x] Listas clasificadas y filtro que deniega por omisión; constantes en lugar de textos sueltos; guardias.
- [x] `jerarquia.spec.ts › H5 CA1` filtra por acciones de datos.

## 5. H5 — Studio (A-05, A-06)
- [x] `FinishAsync` y `ExpireAsync` suman lo reservado; la falla de la copia se recuerda en la corrida.

## 6. H6 — Riesgos
- [x] R-015-3, R-021-4, R-011-2 cerrados con su prueba; R-011-1 reescrito; R-016-3 y R-008-4 con prueba si cabe; R-009-1,
      R-032-6, R-033-5 cerrados; R-034-*.

## 7. H7 — Estabilidad de CI
- [x] `run.mjs --lento` (obligatorio con `--veces` > 1) y latencia en `helpers.ts`.
- [x] `expectToast`, `tras`, `receptor()` con `marca()`/`siguiente()`.
- [x] Guardia en Vitest; `asistente.spec.ts` y las `.last()` existentes con receptor o razón.
- [x] `reintentar-ci.mjs` en `ci.yml`: repite el archivo que falló con sus dependencias sobre base nueva y anota.

## 8. VERIFY
- [x] Contra `ad0534b`: las pruebas nuevas de integración fallan (`it:regresion`, con `--sin` para la de la credencial); mutaciones de las correcciones de la revisión.
- [x] Corrida completa; `--veces=3` de lo modificado.
- [x] Revisión adversarial (subagente sin contexto) y sus hallazgos.
- [x] `verify.md`, riesgos, roadmap, índice.
- [x] Commit y push; CI.
