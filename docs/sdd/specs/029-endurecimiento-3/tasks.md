# Tasks 029 — Endurecimiento 3

## 0. Apertura
- [x] `git rev-list --count origin/main..main` = 0.
- [x] Suite completa como segunda corrida de la 028: 607 unitarias y 385 E2E en verde (8 min 28 s).
- [x] Inventario de las ~140 rutas con su comprobación actual (subagente de solo lectura, verificado al anotar cada ruta).

## 1. H1 — Capacidad declarada por ruta (P1)
- [x] `Infrastructure/Capabilities.cs`: `Capability`, `Effect`, `Gates`, metadatos y `.Requires/.Writes/.ReadsOnly/.Costly/.AppliesReviewed`.
- [x] `CapabilityRequirement` + `CapabilityHandler` (requisito de autorización: corre antes de leer el cuerpo) y
      `CapabilityResultHandler` (403 con el mismo ProblemDetails de siempre).
- [x] `Infrastructure/ErpApp.cs`: servicios y rutas fuera de `Program` (la prueba construye las rutas sin base).
- [x] Las 139 rutas anotadas; quitados `RequireModeler`, `RequireSuperuser`, `RequireUserAdmin` y los `if (!u.…) throw` de los
      handlers (quedan los de servicios y operaciones que reaplica una aprobación, decisión 7).
- [x] `Gates.DirectChange` como filtro (plan, módulos, asistente).
- [x] `/api/files/{id}` y la vista previa del PDF a modelador; `RatePolicies.Search` y `RatePolicies.Bulk`.
- [x] `RouteInventoryTests` y `docs/sdd/rutas.md` generado; mutación: quitar la capacidad de una ruta la hace fallar.
- [x] `capacidades.spec.ts` (403 y 401 recorriendo `rutas.md`; imágenes).

## 2. H2 — Canario (P2)
- [x] `canario.spec.ts`: catálogos «Pruebas 029», empresa propia, usuario «CN Lector» con auditoría.
- [x] Recorrido de todas las GET del inventario con los parámetros del canario y consultas que devuelven más.
- [x] POST que devuelven datos: `/compute`, importación en vista previa, crear, editar, transición.
- [x] Texto del PDF (WinAnsi, flujos Flate) para buscar el canario.
- [x] `e2e:regresion` copia también `docs/sdd/rutas.md`.

## 3. H3 — Alcance en cada salto (D-01)
- [x] `RelatedLoader(viewer)`: cada `Row` pasa por `Visible` (con caché) cuando calcula para una persona.
- [x] `/compute`, `_layout` y `_warnings` con el cargador del usuario.
- [x] Fórmulas guardadas: `DerivedAccess` (catálogos recorridos con permisos por registro restringidos; de compartido a por
      empresa).

## 4. H4 — Lo derivado de un oculto (D-05, D-08)
- [x] `DerivedAccess.Annotate` en `MetadataStore.LoadAsync`; `FieldAccess.ByRole` = regla propia + derivada.
- [x] `FieldAccess.Strip` recorre partidas aunque la partida no tenga reglas propias.
- [x] `onWarning: block` lista solo los avisos visibles (o mensaje genérico).
- [x] `seguridad.spec.ts`: el almacenista no ve `CostoUnitario` ni `Costo` de la recepción; revisión campo por campo de
      Inventario y Costeo en `verify.md`.

## 5. H5 — Límites (D-02, D-09)
- [x] `PrintLimits.RenderAsync`: el lugar se libera en el `finally` del render; pruebas por tiempo y por cancelación.
- [x] `FileStore.PixelsAsync` y 12 MP por documento.
- [x] `/api/search` costosa con límite; la suite lo fija en 120.

## 6. H6 — Aprobación y huella (D-03, D-04)
- [x] `WhyNotApprover` sin `default`: `NoRule`; reglas de `unit.move` (`GainsUnit`) e integraciones.
- [x] `baseHash` en las vistas previas de módulos; instalar/actualizar la exigen (400/409) dentro de la transacción.
- [x] `ModulesPage` la manda.

## 7. VERIFY
- [x] Contra `5de6fa0`: las 10 pruebas nuevas fallan (E2E); el inventario unitario no compila y se demuestra por mutación.
- [x] Corrida completa; `--veces=3` de lo nuevo y modificado.
- [x] Revisión adversarial (subagente sin contexto) y sus hallazgos.
- [x] Riesgos (`riesgos.md` §029 y los que revisó la auditoría 4), roadmap (auditoría 4, la siguiente adelantada), índice.
- [x] Commit y push.
