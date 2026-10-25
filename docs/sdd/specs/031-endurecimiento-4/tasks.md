# Tasks 031 — Endurecimiento 4

## 0. Apertura
- [x] `git log` muestra la 030 (`7fce1d6`) y `git rev-list --count origin/main..main` = 0.
- [x] Última corrida de CI en `main` (`7fce1d6`): verde. Es la segunda corrida completa de la 030.
- [x] Hallazgos de la auditoría 5 reconfirmados en el código (A-01 a A-05 y las observaciones del canario).

## 1. H1 — Avisos con el alcance de quien los recibe (A-01)
- [x] `ShownWarnings` y `CloneDraft` (`RecordService.Warnings.cs`): copia del borrador sin calcular, cargador con `viewer`,
      `VisibleWarnings`; nada si no ve el registro o si el cálculo para él falla.
- [x] `InsertDraftAsync`: la importación recibe los avisos de `ShownWarnings`; la auditoría, los del motor.
- [x] `ApplyTransitionAsync` + `Advance`; `CheckWarnings` con `confirm` (pregunta por lo que ve) y `block` (decide el motor,
      dice lo que ve).

## 2. H2 — Costos (A-03)
- [x] Costeo 1.2.0 con `setSecurity` de `Existencia` (conserva `systemManaged`), `MovimientoInventario` y `TraspasoPartida`.
- [x] `CostingPolicyTests` con la versión nueva.

## 3. H3 — Aprobación al mover de unidad (A-02)
- [x] `GainsMember` en `user.update` y `team.update`; `Hardening4Tests`.

## 4. H4 y H5 — Automatización y PDF (A-04, A-05)
- [x] El bloqueo de una automatización pasa por `CheckWarnings` con el usuario que disparó el evento.
- [x] `PrintChoicesAsync` y `PrintAsync` con `RelatedLoader(viewer)`.

## 5. H6 — Canario e inventario
- [x] `FallbackPolicy` con `UndeclaredRouteRequirement` (403 ProblemDetails); `MapFallbackToFile(...).AllowAnonymous()`.
- [x] Tableros, reportes y OData costosos con `RatePolicies.Aggregate`; la suite y la integración lo suben a 1000.
- [x] `rutas.md` regenerado; `RouteInventoryTests` (+2) y `Hardening031Tests › H6_CA3`.
- [x] `canario.spec.ts`: parámetros sin resolver, rutas no GET del inventario, XLSX, `lookup` filtrado, orden por ocultos,
      importación = `/compute` (y la fila de importación que no era válida).

## 6. VERIFY
- [x] Contra `7fce1d6`: 7 de 8 de `Hardening031Tests` y 2 del canario fallan.
- [x] Corrida completa; `--veces=3` de lo modificado.
- [x] Revisión adversarial (subagente sin contexto) y sus hallazgos.
- [x] Riesgos (R-029-3, R-029-6 y §031), roadmap (renumeración, auditoría 5), índice.
- [x] Commit y push; CI.
