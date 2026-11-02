# Tasks 052 — Filtros de búsqueda compuestos

- [x] T1 · Metadatos: `LookupFilter` con `conditions`, `LookupCondition`; `LookupFilterJson` a 4 000; `ModelDiff`, `PlanCompiler`
  (`LookupFilters.Validate`), índice de usos, asistente (prompt y contexto).
- [x] T2 · `FilterSql.Condition` (extraído) y `LookupFilters.Build` (orígenes con el borrador y `RelatedLoader`, `HiddenFor`).
- [x] T3 · `POST /api/data/{e}/lookup/{campo}` (`LookupForAsync`) y el núcleo del buscador compartido con el `GET`; `rutas.md`.
- [x] T4 · Guardar: `DocDraft.Original`, `CheckLookupFilters` en `Validate` y `SystemUpdateAsync`, con `UPDLOCK`.
- [x] T5 · SPA: buscador con el borrador (`ReferenceSelect`, `FieldInput`, `EntityFormPage`, `LinesEditor`), editor del modelador
  (`LookupFilterEditor`, `lib/lookupFilter.ts`), tipos.
- [x] T6 · Compras 1.13.0 (proveedor activo en la orden; producto activo en la partida).
- [x] T7 · Unitarias (`LookupFilterTests.cs`) y Vitest (`lookupFilter.test.ts`).
- [x] T8 · Integración (`tests/Erp.IntegrationTests/LookupFilterTests.cs`).
- [x] T9 · E2E (`web/e2e/filtros-busqueda.spec.ts`).
- [x] T10 · VERIFY: corrida completa, lo nuevo `--veces=3`, contra el código anterior, revisión adversarial, revisión
  exploratoria, `verify.md`, riesgos, roadmap e índice; commit y push.
