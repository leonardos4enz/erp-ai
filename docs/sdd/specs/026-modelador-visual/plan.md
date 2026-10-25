# Plan 026 — Diseño técnico

Sin cambios de esquema ni capacidades nuevas del motor. El servidor gana tres lecturas para modeladores (origen, validar
una expresión, funciones) y la vista previa de actualizar un módulo gana `conflicts`. Todo lo demás es la SPA.

## 1. Contrato de la API (todo exige sesión y `CanManageModel`; 403 si no)

### `GET /api/model/provenance`
```json
{ "entities": [
  { "entity": "Almacen",
    "installedBy": { "id": "compras", "name": "Compras", "version": "1.9.0" },
    "packages": [ { "id": "compras", "name": "Compras", "version": "1.9.0" }, { "id": "contabilidad", "name": "Contabilidad", "version": "1.2.0" } ],
    "elements": [
      { "kind": "field", "name": "CuentaInventario", "package": "contabilidad", "packageName": "Contabilidad", "step": "base", "modified": false },
      { "kind": "rule", "name": "SoloMias", "package": null, "packageName": null, "step": null, "modified": false } ] } ] }
```
- `kind` ∈ `entity` (propiedades generales; `name` = ""), `field`, `rule`, `automation`, `view`, `report`, `webhook`,
  `schedule`, `workflow`, `layout`, `security`, `ledger` (los cuatro últimos con `name` = "").
- `installedBy`: paquete cuya cadena creó el catálogo (`createEntity`), con la versión instalada; nulo = local.
- `step`: `"base"` o la versión de la actualización (`"1.7.0"`) que dejó el elemento como está; `package` nulo = local.
- `modified`: el elemento tiene origen de paquete y su definición actual difiere de la reproducida.
- Incluye catálogos de partidas y archivados (el editor los abre).

### `POST /api/model/expressions/check`
```json
// petición
{ "entity": "OrdenCompra", "expression": "Proveedor.Pais.Clave = 'MX'", "fields": [ /* FieldDef[] opcional: borrador */ ] }
// respuesta 200
{ "errors": [] }
```
- `entity`: identificador válido; si existe en el modelo es el ámbito; si no (catálogo nuevo) se arma uno vacío con ese nombre.
- `fields` (opcional, ≤ 200): reemplaza los campos del ámbito (los del borrador, para que los campos nuevos se conozcan).
  Se descartan los que no tengan identificador válido o tipo conocido. Nada se guarda.
- Límites: `expression` ≤ 2 000 caracteres (400 si excede); vacía → `{ errors: [] }`.
- Analiza con `ExprAnalyzer.Analyze` y los roles del entorno (los mismos que el compilador).

### `GET /api/model/expressions/functions`
`[{ "name": "ROUND", "min": 1, "max": 2 }, …]` — la lista del analizador (una sola fuente de verdad).

### `POST /api/admin/modules/{id}/upgrade/preview` (cambia: agrega `conflicts`)
```json
{ "valid": true, "errors": [], "steps": [], "roles": [], "seedRecords": 0, "versions": [],
  "conflicts": [ { "entity": "Almacen", "entityLabel": "Almacén", "kind": "rule", "name": "ClaveUnica",
                   "reason": "modified", "effect": "replaced" } ] }
```
- `reason`: `modified` (de este paquete u otro, cambiado localmente) o `local` (creado en este entorno).
- `effect`: `replaced` (la actualización lo vuelve a definir) o `removed` (lo quita, p. ej. una lista sin `merge`).

## 2. Origen (`Metadata/Provenance.cs`, puro)
1. **Reproducir**: paquetes instalados (los de `modules/` con fila en `meta.Module`) en orden de dependencias (`requires`,
   empate por nombre). Por cada uno, su base y las actualizaciones con versión ≤ la instalada. Cada paso se compila con
   `PlanCompiler.Compile` sobre el modelo acumulado, con roles = los del entorno ∪ los de todos los paquetes. Se toma
   `ResultingModel` aunque el paso tenga errores (lo inválido no se aplica, igual que al instalar).
2. **Elementos**: `Elements(EntityDef)` → mapa `(kind, name)` → `JsonNode` normalizado (serializado con
   `MetadataStore.Json`, sin nulos, listas vacías ni `false`; en campos sin `displayOrder`). Antes y después de cada paso,
   los elementos de los catálogos tocados que cambiaron o aparecieron quedan sellados con `(paquete, paso)`; los que
   desaparecen pierden el sello.
3. **Comparar** con el modelo actual: sello → origen y `modified = !JsonNode.DeepEquals(actual, reproducido)`; sin sello →
   local. `installedBy` = paquete del paso donde apareció el catálogo.
4. **Choques de una actualización**: se compilan las actualizaciones pendientes sobre el modelo actual; cada elemento actual
   que cambia o desaparece y es local o está modificado es un choque (`replaced` o `removed`).
5. Memo en proceso por (instancia del modelo en caché, versiones instaladas): `MetadataStore.Invalidate` cambia la
   instancia; la reproducción no se repite en cada petición.

## 3. SPA
- `lib/exprComplete.ts` (puro): palabra bajo el cursor, ruta por referencias (≤ 3 saltos), contexto de cadena después de
  `Campo = '` / `<>` / `IN (`, sugerencias (campos con etiqueta y tipo, funciones con firma, opciones). Inserción que
  reemplaza solo la palabra.
- `components/ExprInput.tsx`: `input` o `textarea` con `role="combobox"`, `aria-autocomplete="list"`, `aria-expanded`,
  `aria-controls`, `aria-activedescendant`; lista `role="listbox"`; región `aria-live` con «N sugerencias» y con el
  resultado de la validación. Valida con espera de 0.4 s y cancela la anterior (`AbortController`).
- `pages/modeler/sections/`: `ItemList` (lista de elementos con agregar, subir, bajar, quitar, plegar; estado vacío),
  `JsonAdvanced` («Ver como JSON», `<details>` cerrado, con el `Textarea` de siempre y su etiqueta «Reglas (JSON)», etc.),
  `OriginBadge`, `MultiPick` (casillas) y un editor por sección (`RulesEditor`, `AutomationsEditor` + `ActionsEditor`
  compartido con `SchedulesEditor`, `SecurityEditor`, `ViewsEditor`, `ReportsEditor`, `WebhooksEditor`, `LedgerEditor`).
  Cada editor recibe el valor ya interpretado y devuelve el nuevo; `EntityEditor` lo escribe con `pretty()` en el mismo
  texto del borrador (como `DesignOrJson`). Los editores parchan objetos (`{ ...x, ...patch }`): conservan propiedades que no
  muestran y quitan claves vacías.
- `EntityEditor`: pestañas nuevas (decisión 5); vista previa en vivo (`modelApi.preview` con espera de 0.8 s cuando hay
  operaciones y el borrador es válido en el cliente); los errores se reparten por pestaña según la operación (`setRules` →
  Reglas…) o, para los de la pasada final, por palabras del mensaje («automatización», «regla», «seguridad», «vista»,
  «reporte», «webhook», «tarea», «libro», «layout», «proceso»); los que no encajan van a *Catálogo*. Etiqueta de pestaña
  «(con errores)». Aviso por elemento de paquete cambiado en el borrador (H2 CA3).
- `ModelerPage`: agrupación por módulo con `?app=` y `?q=`, columna «Origen».
- `ModulesPage`: lista de choques en el diálogo de actualizar.

## 4. Pruebas
- Unitarias (`ProvenanceTests`): reproducir los cuatro paquetes reales (Compras, Inventario, Contabilidad, Costeo) → los
  campos de `Almacen` de Contabilidad sellados por Contabilidad y el resto por Compras; comparar el modelo reproducido
  contra sí mismo → nada modificado; una regla cambiada → modificada; una regla nueva → local; una actualización que
  reemplaza una lista sin `merge` → choque `removed` para la regla local y `replaced` para la modificada; normalización
  (nulos, listas vacías, orden de campos).
- E2E `visual-modelador.spec.ts` (corre después de instalar Contabilidad y Costeo): un escenario por CA (`H1 CA1`…), con
  accesibilidad automática y captura claro/oscuro del editor de automatizaciones y de la lista por módulos; los E2E que
  llenan JSON abren antes «Ver como JSON» de su sección.

## 5. STRIDE
| Amenaza | Riesgo | Mitigación |
|---|---|---|
| **S** suplantación | Un usuario sin modelado consulta origen o valida expresiones para mapear el modelo | Los tres endpoints exigen sesión y `CanManageModel` (403); E2E con un usuario normal |
| **T** manipulación | Un editor visual escribe algo que no pasó por el compilador | Los editores solo cambian el borrador; el plan sigue por `plan/preview` → `plan/apply` con `baseHash` (021) y `baseVersions`; nada nuevo escribe |
| **T** | Un plan escrito a mano «se declara» de un paquete | El origen no es metadato: se reproduce de los paquetes del servidor, que no llegan por la API |
| **R** repudio | Cambios sin rastro | Sin cambios: cada plan aplicado queda en `meta.ChangeSet` y la auditoría |
| **I** divulgación | `fields` del borrador o mensajes del analizador filtran datos | Solo metadatos, a quien ya ve el modelo completo; nunca lee filas de `app` |
| **D** negación de servicio | Expresiones enormes o validación en cada tecla | ≤ 2 000 caracteres, ≤ 200 campos, espera de 0.4 s y cancelación en el cliente; límite global por minuto; reproducción memorizada por versión del modelo |
| **E** elevación | Validar con roles de otro entorno o crear roles | El analizador usa los roles existentes; ningún endpoint nuevo escribe |
