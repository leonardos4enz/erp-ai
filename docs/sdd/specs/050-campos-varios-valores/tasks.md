# Tasks 050 — Campos de varios valores

- [x] T1 · Metadatos: `FieldTypes.MultiReference`/`MultiUser`, `IsMulti`, `IsVirtual`; `EntityDef.MultiFields`/`ValueFields`;
  `Identifiers.JoinTable`; `DdlGenerator` (tabla de unión en `CreateTable` y `AddColumn`).
- [x] T2 · Plan: `Normalize`/`ValidateField`/`ValidateFinal` (destino, partidas, `unique`, `defaultValue`, `lookupFilter`);
  `ExprAnalyzer` (campo y último salto); `InterfaceRules.Resolve`; `PrintRules` admite `{{ Campo }}` propio.
- [x] T3 · Registro: `ParseIntoAsync` (lista, tope, existencia, visibilidad, conservar), `IsBlank`, `SaveMultiAsync` tras la
  fila dueña, `LoadDraftAsync`, `SelectClause`/`Format` (ids y etiquetas con alcance), `FormatValues` (/compute).
- [x] T4 · Caminos: filtros (`FilterSql`, `ValueKind.Multi`), importación y exportación, OData (`$expand`, CSDL), impresión,
  explorador físico (la tabla de unión no es huérfana), avisos de vistas en paquetes.
- [x] T5 · Compras 1.11.0: `Proveedor.Categorias` y `Departamento.Responsables`.
- [x] T6 · SPA: tipos, `MultiReferenceSelect`, `FieldControl`, `draft.ts`, celdas de la lista, `FilterBuilder`, modelador.
- [x] T7 · Pruebas unitarias (`tests/Erp.Tests/MultiValueTests.cs`).
- [x] T8 · Pruebas de integración (`tests/Erp.IntegrationTests/MultiValueTests.cs`).
- [x] T9 · E2E (`web/e2e/varios-valores.spec.ts`) y canario (H6 CA1); capturas revisadas.
- [x] T10 · VERIFY: corrida completa, lo nuevo `--veces=3`, contra el código anterior, revisión adversarial, revisión
  exploratoria, `verify.md`, riesgos, roadmap (renumerar 051–056), índice de specs; commit y push.
