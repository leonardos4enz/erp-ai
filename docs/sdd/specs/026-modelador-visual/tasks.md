# Tareas 026

## Servidor
- [x] T1 `Provenance.cs`: reproducir cadenas instaladas, elementos normalizados, comparar, choques de actualización, memo.
- [x] T2 `GET /api/model/provenance` (modeladores).
- [x] T3 `POST /api/model/expressions/check` y `GET /api/model/expressions/functions` (`ExprAnalyzer.FunctionList`).
- [x] T4 `UpgradePreviewAsync` con `conflicts`.
- [x] T5 Unitarias `ProvenanceTests` (paquetes reales, modificado, local, choques, normalización).

## SPA
- [x] T6 Tipos y `modelApi` (`provenance`, `checkExpression`, `functions`), `adminApi.previewUpgrade` con `conflicts`.
- [x] T7 `lib/exprComplete.ts` y `components/ExprInput.tsx` (combobox accesible, validación en vivo).
- [x] T7b `lib/conditionBuilder.ts` (texto ↔ renglones) y `components/ConditionBuilder.tsx`; `ExprInput condition` con modo
  «Constructor / Texto» en todas las condiciones de las secciones (H3 CA5).
- [x] T8 Bloques comunes: `ItemList`, `JsonAdvanced`, `OriginBadge`, `MultiPick`.
- [x] T9 Editores: Reglas, Automatizaciones (+ `ActionsEditor`), Seguridad, Vistas, Reportes, Webhooks, Tareas, Libro.
- [x] T10 `EntityEditor`: pestañas nuevas, quitar placeholders de ejemplo, «Un registro por empresa» en Catálogo, fórmula y
  valor por defecto con `ExprInput`, origen por campo y por bloque, aviso de cambio sobre elemento de paquete, vista previa
  en vivo con errores por pestaña.
- [x] T11 `ModelerPage`: agrupación por módulo, filtro y búsqueda en la URL, origen.
- [x] T12 `ModulesPage`: choques en la vista previa de actualizar.
- [x] T13 Estilos (claro/oscuro, móvil, pestañas con desplazamiento).

## Verificación
- [x] T14 E2E `visual-modelador.spec.ts` (H1–H4) y ajuste de los E2E que llenan JSON.
- [x] T15 Capturas de referencia afectadas regeneradas y revisadas una por una.
- [x] T16 Suites en verde dos veces; revisión exploratoria (teclado, lector, claro/oscuro, móvil); `verify.md`.
- [x] T17 Roadmap (026 esta, Ventas 027, CFDI 028), índice de specs, commit y push.
