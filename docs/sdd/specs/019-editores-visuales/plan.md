# Plan 019 — Diseño técnico

## Contrato
**Sin cambios en la API ni en el Plan de Cambios.** Los diseñadores producen exactamente las operaciones de las specs 002
y 007, armadas por el mismo `buildOperations` del editor del catálogo:
```ts
{ op: 'setLayout'; entity: string; layout: Layout | null }      // 007: null quita el layout
{ op: 'setWorkflow'; entity: string; workflow: Workflow }       // 002: crea la columna Estado la primera vez
```
Endpoints que usan (ya existentes, con `RequireModeler`): `GET /api/model/entities`, `POST /api/model/plan/preview`,
`POST /api/model/plan/apply` (con `RequireDirectChange` de la 012), `GET /api/admin/roles` (sugerencias de roles).
La validación sigue siendo la del servidor (`InterfaceRules.ValidateLayout`, `PlanCompiler.SetWorkflow`): el diseñador
evita errores por construcción, pero no reemplaza la vista previa.

## Arquitectura (web)
```
ModelerPage ── EntityEditor (dueño del borrador)
                 ├─ pestaña Catálogo      general, campos, avanzado (reglas, automatizaciones, seguridad, vistas…)
                 ├─ pestaña Formulario    DesignOrJson ── LayoutDesigner ── LayoutPreview
                 └─ pestaña Proceso       DesignOrJson ── WorkflowDesigner (WorkflowDiagram + inspector)
               barra común: Cancelar · Revisar cambios  →  PlanReviewDialog (sin cambios)
```
- `web/src/pages/modeler/DesignOrJson.tsx`: conmutador **Diseñador / JSON** sobre un texto del borrador. Si el texto no
  parsea, el diseñador no se ofrece y muestra el error. El diseñador recibe el valor parseado y devuelve el nuevo valor,
  que se serializa (`pretty`) al mismo campo del borrador (`layoutJson`, `workflowJson`).
- `web/src/lib/layoutEdit.ts` (puro, sin React): operaciones sobre un `Layout` —
  `moveField(layout, field, to: { section, index } | 'palette')`, `stepField(layout, field, dir)` (el paso de teclado
  que cruza secciones), `moveSection(layout, from, toIndex, tab?)`, `stepSection(layout, i, dir)` (cruza a la pestaña
  vecina), `setTabs(layout, on)`, `renameTab`, `addSection`, `removeSection` (sus campos vuelven a «Sin colocar»),
  `patchItem` (un campo sin `wide` ni `visibleWhen` se escribe como texto, como en los módulos), `uniqueName`.
  Cada operación conserva el resto de las propiedades del objeto tal cual (no pierde lo que el diseñador no conoce).
- `web/src/lib/workflowEdit.ts` (puro): `layoutDiagram(workflow)` → capas por distancia desde el inicial (BFS por
  `from → to`; lo inalcanzable en una capa final), posiciones de nodos y geometría de aristas (curvas cuadráticas,
  desplazadas cuando hay varias entre los mismos estados; las que regresan o van en la misma capa se arquean por
  debajo; los lazos sobre el nodo); `removeState` (quita transiciones que llegan y el estado de los `from`; devuelve las
  transiciones afectadas para el diálogo), `addState`, `addTransition`, `uniqueName`.
- `LayoutDesigner.tsx`: «Sin colocar» como franja arriba, y debajo el lienzo (pestañas y secciones con sus campos en
  rejilla de 1 o 2 columnas) junto a la vista previa; desde 1600 px, tres columnas; por debajo de 1000 px se apilan.
- `WorkflowDesigner.tsx`: barra (agregar estado, agregar transición), diagrama SVG en un recuadro con desplazamiento
  (`tabIndex=0`, nombre accesible; se reduce hasta 70 % para caber) y panel de propiedades del elemento seleccionado
  (al lado desde 1440 px, debajo en pantallas menores). Las etiquetas de transición se acomodan en el primer lugar libre
  cerca de su flecha para no tapar estados ni otras etiquetas (F-03).
- `canonical()` del editor ordena las claves de los objetos antes de comparar (H1 CA6).

## Interacción
### Formulario
| Acción | Ratón | Teclado |
|---|---|---|
| Mover campo | arrastrar el chip a otro chip (queda antes) o al espacio libre de una sección (al final) | Espacio toma; ↑/↓ mueve (cruza a la sección vecina; desde la primera posición, ↑ lo regresa a «Sin colocar»); Espacio suelta; Esc cancela y regresa a donde estaba |
| Colocar un campo sin colocar | arrastrarlo a una sección | Espacio toma; ↓ lo lleva al inicio de la primera sección |
| Quitar del formulario | arrastrar a «Sin colocar» | Supr, o «Quitar del formulario» en sus opciones |
| Opciones del campo | botón de opciones | ídem: diálogo con sección (incluye «Sin colocar»), ancho completo y condición |
| Mover sección | arrastrar su asa a otra sección (queda antes) o a la cabecera de una pestaña (al final de esa pestaña) | botones «Subir/Bajar sección» (al pasar la primera de su pestaña cambia de pestaña) |
| Pestañas | — | interruptor «Organizar en pestañas», nombre editable, «Subir/Bajar pestaña», «Agregar pestaña» |
Los anuncios van a una región `aria-live="assertive"`: «Clave tomado. Sección Datos generales, posición 2 de 4…»,
«Clave colocado en Pago, posición 1 de 3», «Movimiento cancelado».

### Proceso
- Nodo de estado: `<g role="button" tabIndex=0 aria-pressed aria-label="Estado X, inicial, editable, N transiciones salen">`;
  Enter o Espacio lo selecciona; el foco se dibuja con el mismo anillo de la app.
- Etiqueta de transición: `<g role="button">` con «Transición Enviar: de Borrador a Enviada (solo automatización)».
- Arrastrar de un nodo a otro (eventos de puntero, umbral de 6 px) crea `{ name, label, from: [A], to: B }` y la
  selecciona; un clic sin arrastrar selecciona.
- Panel: estado → etiqueta, identificador (fijo si ya estaba guardado), color, editable, «Marcar como inicial»,
  transiciones que salen (enlaces que las seleccionan), «Agregar transición desde aquí», «Eliminar estado».
  Transición → etiqueta, identificador, desde (casillas), hacia, roles (con sugerencias), comentario obligatorio,
  campos obligatorios (con sugerencias de campos capturables), condición y mensaje, asignaciones (campo = expresión),
  icono (selector de la app), variante, solo automatización, «Eliminar transición».

### Vista previa
`formSections(def con el layout del borrador, campos del borrador, sin ocultos)`: la misma función que el formulario.
Campos simulados (etiqueta y caja con el tipo), pestañas navegables, «Otros datos» y, si el catálogo es documento,
«Totales» con las fórmulas no colocadas. Las secciones y campos con `visibleWhen` llevan la marca «Condicional» con la
condición en su descripción accesible.

## Pruebas
- E2E `editores.spec.ts` (un escenario por criterio, ratón y teclado; `dragTo` para arrastrar y soltar nativo, puntero
  para conectar estados; los elementos se verifican visibles con `elementFromPoint` antes de interactuar).
- Ajuste de pruebas existentes que leían el JSON del layout en la sección avanzada (`interfaz.spec.ts`,
  `multiempresa.spec.ts`) y de la referencia visual del modelador (barra de pestañas nueva).
- `dotnet test` sin cambios de backend (debe seguir en verde).

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Cambiar el modelo por un camino nuevo sin vista previa ni aprobación | No hay endpoint ni operación nuevos: los diseñadores solo editan el borrador; aplicar sigue en `/plan/apply` con `RequireModeler` y `RequireDirectChange` (012) |
| **T** Un layout o proceso inválido producido por el diseñador | El servidor revalida todo en la vista previa y al aplicar (mismo `PlanCompiler`); el diseñador además respeta las reglas por construcción |
| **I** Ver la estructura del modelo sin permiso | Las pestañas viven dentro del modelador (`Guard` + `RequireModeler` en `GET /model/entities?includeArchived`); sin permiso, acceso denegado |
| **T** Inyección (XSS) por etiquetas o expresiones en el SVG o la vista previa | Todo se pinta como texto con React (sin `dangerouslySetInnerHTML`); la CSP sigue estricta |
| **R** Cambios visuales sin rastro | Mismo conjunto de cambios auditado (`model.change`) |
| **D** Diagramas o layouts enormes que congelan la página | Límites del motor (30 estados, 60 transiciones, 30 secciones); la geometría es O(estados + transiciones) |
| **T** Perder un cambio de seguridad hecho en JSON al pasar al diseñador | Las operaciones de edición conservan las propiedades que no conocen; el diseñador solo se ofrece con JSON válido |
