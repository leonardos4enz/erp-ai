# Plan 005 — Diseño técnico

## Automatizaciones (`setAutomations`)
Se guardan en `meta.Entity.AutomationsJson` (migración idempotente) y viajan en `EntityDef.automations`.

```ts
type AutomationEvent = 'create' | 'update' | 'enterState' | 'transition';

type Automation = {
  name: string;            // identificador, único en el catálogo
  label: string;           // aparece en errores, auditoría e historial
  on: AutomationEvent;
  state?: string;          // on = enterState: estado al que se entra
  transition?: string;     // on = transition: transición ejecutada
  when?: string;           // condición sobre el registro ya guardado
  actions: AutomationAction[];
};

type AutomationAction = {
  type: 'update' | 'transition' | 'create' | 'rollup';
  lines?: string;          // repetir por cada partida de este campo (el ámbito es la partida)
  when?: string;           // condición en el ámbito de la acción, con datos frescos
  target?: string;         // update | transition | rollup: campo referencia del ámbito → registro destino
  set?: { field: string; value: string }[];  // update: campos del destino · create: campos del registro nuevo
  transition?: string;     // transition: nombre de la transición del destino
  entity?: string;         // create: catálogo nuevo · rollup por coincidencia: catálogo destino
  match?: { field: string; source: string }[]; // rollup por coincidencia: destino.field = origen.source
  createMissing?: boolean; // rollup por coincidencia: crear el destino si no existe
  field?: string;          // rollup: campo numérico del destino que recibe la suma
  sum?: string;            // rollup: campo numérico del origen que se suma
  filter?: string;         // rollup: condición sobre cada registro de origen
};
```
`{ op: 'setAutomations'; entity: string; automations: Automation[]; merge?: boolean }` — sin `merge`
reemplaza la lista (editor del modelador); con `merge: true` agrega o reemplaza por nombre y conserva
las demás (un módulo que extiende el catálogo de otro).

### Semántica
- **Ámbito.** El registro que dispara el evento (o cada partida, con `lines`). Las expresiones usan el
  lenguaje de la spec 002; en una partida, `Recepcion.Folio` lee el documento por su campo padre.
- **Orden.** Las automatizaciones del catálogo corren en orden de declaración y sus acciones también.
  Antes de cada acción se relee el registro de origen y se usa un cargador nuevo de relacionados: una
  condición como `OrdenCompra.PendientePorRecibir <= 0` ve lo que dejó la acción anterior.
- **update:** asigna `set` en el registro al que apunta `target`. Si el destino es una partida, se carga
  su documento y se recalculan sus fórmulas completas (encabezado y partidas).
- **transition:** ejecuta `transition` en el destino si está en uno de sus estados `from`; si no, se omite
  (no es error). Valida la condición propia de la transición y las reglas como una transición normal,
  aplica sus `set` y escribe el historial con el comentario `Automática: {label} ({origen})`.
- **create:** crea un registro de `entity` con `set` (puede asignar campos no editables por personas).
- **rollup:** destino = registro de `target`, o el de `entity` cuyos campos `match` coinciden con los del
  origen (se crea con esos valores si `createMissing`). Origen = todos los registros vigentes del catálogo
  del ámbito que apuntan al mismo destino (mismo `target` o mismos valores `match`) y cumplen `filter`.
  `destino.field = SUM(origen.sum)`. Es un recálculo, no un incremento.
- **Autoridad del motor.** Las acciones no revisan permisos ni estado editable del destino; sí recalculan
  fórmulas y validan **las reglas del registro modificado** (de la partida, si el destino es una partida).
  `update`/`rollup` no rellenan valores por defecto en campos vacíos del destino (no inventan datos);
  `create` sí, y valida obligatorios como un alta normal.
- **Encadenamiento.** Cada escritura dispara los eventos del destino (`update`, `create`, `transition` y
  `enterState`) con profundidad + 1. Más de **5** niveles: error y se revierte todo.
- **Errores.** Una falla dentro de una acción se responde como 400 con título
  `No se pudo completar «{label}»: {mensaje}` y la transacción completa se revierte.
- **Auditoría.** `automation.update`, `automation.create` y `workflow.{t}` con antes/después, a nombre del
  usuario que disparó el evento.

### Validación estática (`PlanCompiler`, pasada final)
Nombres válidos y únicos; máximo 20 automatizaciones por catálogo y 20 acciones por automatización;
solo en catálogos raíz (no partidas); `state`/`transition` existen en el proceso; `lines` es un campo de
partidas; `target` es una referencia del ámbito; `set.field` existe en el destino y no es calculado;
la transición existe en el proceso del destino; `create` incluye todos los campos obligatorios sin valor
por defecto; `rollup.field` y `rollup.sum` son numéricos; los `match` existen en ambos lados y son
columnas; todas las expresiones (`when`, `set.value`, `filter`) se analizan en su ámbito.
**Ciclos:** grafo catálogo → catálogo (solo aristas que disparan eventos con automatizaciones en el
destino); un ciclo se rechaza: «Ciclo de automatizaciones: A → B → A».

## Otras capacidades genéricas
| Capacidad | Contrato |
|---|---|
| Transición del sistema | `WorkflowTransition.system?: boolean`. No se ejecuta por API (400 «La ejecuta el sistema») y no viaja en `_workflow.transitions` |
| Buscador filtrado | `FieldDef.lookupFilter?: { field: string; source: string }` (`meta.Field.LookupFilterJson`). `field`: campo del catálogo referenciado; `source`: campo del mismo registro o, en partidas, `CampoPadre.Campo`. `GET /data/{e}/lookup?search&filterField&filterValue`: `filterField` se resuelve contra los metadatos del catálogo (lista blanca) y el valor va como parámetro |
| Campo para mostrar calculado | `displayField` acepta una fórmula de texto (p. ej. la partida de OC se muestra como «CAJ-30 · Caja… · pendiente 100.00») |
| Recalcular fórmulas existentes | Al aplicar un plan que agrega o cambia una fórmula, se recalculan las fórmulas guardadas de los registros vigentes del catálogo (o de los documentos, si es de partida), en la misma transacción. Solo fórmulas: no aplica valores por defecto ni toca `UpdatedAt` |
| Dependencias de módulos | `ModulePackage.requires?: string[]`. `ModuleInfo += requires, missingRequirements`. Instalar sin ellos: 409 «Requiere el módulo …» |

## Compras 1.2.0 (entrada en `upgrades`)
- `OrdenCompraPartida` + `CantidadRecibida` (decimal, no editable, por defecto 0), `Pendiente`
  (`Cantidad - CantidadRecibida`), `Descripcion` (texto: clave · descripción · pendiente) como campo para mostrar.
- `OrdenCompra` + `PendientePorRecibir` (`SUM(Partidas.Pendiente)`); proceso: `recibirParcial` y `recibir`
  pasan a `system: true`; `cerrar` también desde *Recibida parcial* (cierre con faltante).
- `RecepcionPartida` + `PartidaOrden` (referencia obligatoria, filtrada por `Orden = Recepcion.OrdenCompra`);
  `Producto` sugiere `PartidaOrden.Producto` y `Cantidad` sugiere `PartidaOrden.Pendiente`. Reglas:
  la partida es de la orden, el producto coincide, y no se recibe más de lo pendiente.
- Automatización `AcumularRecibido` en `Recepcion` al entrar a *Confirmada*: rollup por partida
  (`CantidadRecibida = SUM(Cantidad)` con `filter: Recepcion.Estado = 'Confirmada'`), costo último del
  producto, y transición `recibir` / `recibirParcial` según `OrdenCompra.PendientePorRecibir`.

## Inventario 1.0.0 (`modules/inventario.json`, requiere Compras)
- `Existencia`: almacén, producto, cantidad (no editables; los asigna el motor), clave calculada. Regla
  `Cantidad >= 0` («no hay existencias suficientes»).
- `MovimientoInventario`: folio `MOV-`, fecha, tipo (Entrada, Salida, Ajuste, Traspaso salida, Traspaso
  entrada), almacén, producto, cantidad, efecto (`±` según tipo), referencia (no editable), notas. Proceso
  de un estado *Aplicado* no editable (inmutable). Automatización al crear: rollup por coincidencia
  `Existencia(Almacen, Producto).Cantidad = SUM(Efecto)` con `createMissing`.
- `Traspaso` con partidas: *Borrador → Aplicado*; al entrar a *Aplicado* crea por partida una salida en
  el origen y una entrada en el destino.
- En `Recepcion` (con `merge`): al entrar a *Confirmada*, una entrada por partida.
- Roles: *Supervisor de inventario* (todo, ajustes) y *Almacenista* (consulta existencias; entradas,
  salidas y traspasos).

## Contrato de API (cambios)
```ts
EntityDef += { automations?: Automation[] }
FieldDef  += { lookupFilter?: { field: string; source: string } }
WorkflowTransitionDef += { system?: boolean }
Operation += { op: 'setAutomations'; entity: string; automations: Automation[]; merge?: boolean }
ModuleInfo += { requires: string[]; missingRequirements: string[] }
GET /data/{e}/lookup?search=&filterField=&filterValue=
```
Sin cambios en registros, transiciones ni `/compute`: las automatizaciones son efectos del servidor.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Escalar privilegios a través de una automatización (un rol sin permiso sobre la OC la mueve) | Solo un modelador declara automatizaciones (plan de cambios auditado). El evento exige el permiso normal del usuario; lo que sigue es lógica de negocio aprobada |
| **E** Ejecutar a mano una transición reservada al sistema | `system: true` se rechaza en el servidor (400) aunque se llame por API |
| **D** Ciclos o cascadas sin fin | Ciclos rechazados en el compilador; profundidad máx. 5 en ejecución; máx. 20 automatizaciones × 20 acciones; rollup máx. 10 000 registros de origen; partidas máx. 500 |
| **T** Totales descuadrados por concurrencia (dos recepciones a la vez) | Rollup = recálculo desde la fuente; bloqueo `UPDLOCK, HOLDLOCK` del destino antes de sumar; `sp_getapplock` por clave en la búsqueda/creación por coincidencia (evita existencias duplicadas). Residual: dos recepciones simultáneas pueden pasar la regla «no más de lo pendiente»; el total acumulado sigue siendo exacto |
| **T** Saltar la liga recepción → partida con el buscador | El filtro del buscador es comodidad; las reglas de la partida validan orden y producto en el servidor |
| **I** Filtrar el buscador por campos arbitrarios | `filterField` debe ser una columna del catálogo según los metadatos; el valor es parámetro. Filtrar solo reduce resultados; los permisos de lookup no cambian |
| **I** Inyección vía expresiones o nombres en automatizaciones | Expresiones con el intérprete propio; identificadores solo de metadatos validados; valores parametrizados |
| **R** No saber quién movió la OC | Auditoría `automation.*` / `workflow.*` a nombre del usuario que disparó el evento + historial «Automática: …» |
| **T** Inventario negativo o movimientos editados | Regla `Cantidad >= 0` en la existencia (revierte la operación); movimientos en estado no editable; existencias sin campos capturables |
