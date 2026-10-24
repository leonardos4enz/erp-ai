# Plan 014 — Diseño técnico

Todo es capacidad genérica del motor; lo único de Compras va en `upgrades` (1.8.0).

## 1. Compras 1.8.0 (`modules/compras.json`)
- `RequisicionPartida.CostoEstimado.defaultValue`:
  `IF(ISBLANK(Producto.CostoUltimo), NULL, CONVERT(Producto.CostoUltimo, COALESCE(Producto.MonedaCosto, BASECURRENCY()), BASECURRENCY(), Requisicion.Fecha))`
- `OrdenCompraPartida.PrecioUnitario.defaultValue`:
  `IF(ISBLANK(Producto.CostoUltimo), NULL, CONVERT(Producto.CostoUltimo / Orden.TipoCambio, COALESCE(Producto.MonedaCosto, BASECURRENCY()), BASECURRENCY(), Orden.Fecha))`
  — el lenguaje solo admite rutas de un nivel (`Orden.Moneda.Clave` no existe), y `TipoCambio` ya es la tasa
  moneda-de-la-orden → base. Dividir antes de convertir conserva el nulo: `CONVERT(nulo)` es nulo, y `x / 0` también.
  `IF(ISBLANK…)` evita que la aritmética (nulo = 0) sugiera 0 en una partida sin producto.
- `Producto.MonedaCosto`: `editable: true`, `defaultValue: BASECURRENCY()`; regla `MonedaCostoClave`
  (`ISBLANK(MonedaCosto) OR LEN(MonedaCosto) = 3`).

## 2. Acumulado en SQL (`RecordService.Automations.cs`, `RecordFilter`)
- `RecordFilter.TranslateRollup(filter, source, model, bag)`: el mismo traductor con modo «sistema»: rutas de un nivel
  por campo referencia o padre → `(SELECT r.[Campo] FROM app.X r WHERE r.[Id] = t.[Ref] AND r.[IsDeleted] = 0)` (igual
  que `RelatedLoader.Row`); `=`, `<>` e `IN` en dos valores con `CASE` (como `ExprEvaluator.Compare`); sin
  `CURRENTUSER`, `CURRENTUNIT`, `HASROLE`, `ISCREATOR` (el acumulado corre con `ExprEnv.System`). Lanza `ExprException`
  si no es traducible.
- `SumSourcesAsync`: si el campo `sum` está en la tabla y el filtro es nulo o traducible →
  `SELECT SUM(t.[sum]) FROM app.X t WHERE <claves> AND t.[IsDeleted] = 0 AND <filtro>` (nulo → 0). Si no, el camino en
  memoria con `MaxRollupSources`.

## 3. Folios (`RecordService.cs`, `RecordService.Import.cs`)
- `FolioSource` (privada): `Preview` (provisional `prefijo + "VP-" + n`, sin tocar `meta.Sequence`) o `Block` (rango
  reservado por campo). `AutoCtx.Folios`; `AssignFoliosAsync` toma de ahí si es el catálogo importado y queda rango; si
  no, la secuencia con bloqueo como siempre.
- Importación real: antes de la transacción, `ReserveFoliosAsync(e, n)` en conexión propia y transacción corta
  (`UPDATE … SET @start = NextValue, NextValue = NextValue + @n`). Una fila que se revierte al punto de guardado
  devuelve su folio al bloque (el contador se restaura). Si la importación falla: `UPDATE … SET NextValue = @start WHERE
  NextValue = @start + @n` (devuelve el bloque solo si nadie reservó después).

## 4. Campos locales (`ModelDiff`, `EnvironmentService`, `EnvironmentsPage`)
- `ModelDiffResult.LocalArchives` (texto «Catálogo › Campo»); la advertencia se quita de `Warnings`.
- `PackageDiffDto.LocalArchives`. `ApproveRequest` y `ApplyRequest` + `ConfirmArchive`. Con `LocalArchives` no vacío y
  sin confirmación → 409 «Este paquete archiva campos que solo existen en este entorno: … Confírmalo».
- Auditoría `environment.approve` y `environment.apply`: `localArchives`.
- UI: sección con la lista y casilla «Entiendo que estos campos se archivarán (sus datos se conservan)»; *Aprobar* y
  *Aplicar* deshabilitados sin ella.

## 5. Webhooks
- `WebhookService.DeliverAsync`: `Permanent(code) = code is >= 400 and < 500 and not 408 and not 429` → `failed` al
  primer intento con el mensaje del rechazo.
- `WebhookUrlPolicy.IsInternal`: `2001:0000::/32`.

## 6. Riesgos residuales
- `LockAutomationTargetsAsync(cn, tx, e, model, draft, fires, company)`: además de los destinos `target`, para cada
  acumulado por `match` con claves completas en el borrador toma `sp_getapplock` con el mismo recurso que
  `RollupAsync` (claves normalizadas por `RollupKey`), ordenados; luego los documentos `target`. Se llama en el alta
  (después de calcular, antes de validar; evento `create`), en la edición (`update`) y en la transición (como en la 012).
- `GET /api/admin/environments` → `directChangesOverride: boolean` (`RequireApproval && AllowDirectChanges`); aviso en
  *Entornos*. `Program.cs` registra una advertencia al arrancar.

## Contrato de API (cambios)
```ts
type PackageDiff = { ...; localArchives: string[] }
POST /api/admin/environments/imports/{id}/approve { diffHash, comment?, confirmArchive?: boolean } // 409 sin confirmación
POST /api/admin/environments/imports/{id}/apply   { diffHash, confirmArchive?: boolean }           // 409 sin confirmación
GET  /api/admin/environments → { ...; directChangesOverride: boolean }
// Webhooks: un 4xx (salvo 408/429) queda 'failed' con attempts = 1
```

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **T** Precio en otra moneda aceptado sin notarlo (sobrecosto) | Sugerencia convertida; sin tipo de cambio, campo vacío y obligatorio |
| **D** Un producto con muchos movimientos impide todo movimiento nuevo | Suma en SQL sin tope |
| **T** La suma en SQL difiere de la de memoria (nulos, borrados) | Misma semántica de nulos y de `IsDeleted`; unitarias de traducción; se cae a memoria si algo no es traducible |
| **I/T** Inyección por el filtro del acumulado | Identificadores solo de metadatos citados; literales como parámetros (mismo traductor de la 006) |
| **D** Una importación detiene las altas del catálogo | Vista previa sin secuencia; bloque reservado en transacción corta |
| **T** Folios repetidos | Bloque reservado atómicamente; la devolución es comparar-y-cambiar |
| **T/R** Un paquete archiva campos locales sin que nadie lo decida | Confirmación explícita al aprobar y aplicar, en la huella y en la auditoría |
| **D** Reintentos inútiles contra un destino que rechaza | 4xx definitivo sin reintento |
| **I/S** SSRF por Teredo | `2001::/32` interna |
| **D** Interbloqueos en acumulados por clave | Bloqueo por clave antes de insertar, en orden fijo |
| **E** Atajos abiertos sin que se note | Aviso al arrancar y en *Entornos* |
