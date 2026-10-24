# Verificación 019 — Editores visuales

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 428/428 (sin cambios de backend: la spec no toca el servidor) |
| E2E | 253/253 en dos corridas completas limpias seguidas (+11 en `editores.spec.ts`; ajustadas `interfaz.spec.ts` H5 y F-03, `multiempresa.spec.ts` H3 y `automatizaciones.spec.ts` H1 CA4: el layout y el proceso pasaron a su pestaña y la sección avanzada ya no se abre sola por tener proceso) |
| Contra el código anterior | Las tres regresiones de la revisión exploratoria se vieron fallar revirtiendo la corrección en el árbol de trabajo (F-01: *Expected < 6, Received 39*; F-02: *Received «Nueva secciónSeguimiento»*; F-03: *Expected 0, Received 1*) y pasar al restaurarla. Sin la 019 la suite nueva falla en su primer paso: no existe la pestaña «Formulario» |
| Visual | Referencias `modelador-light/dark` regeneradas dentro de una corrida completa y revisadas una por una: solo cambia la barra de pestañas *Catálogo · Formulario · Proceso* sobre la tarjeta «General» |
| Accesibilidad | axe sin violaciones serias en las pestañas Formulario (con vista previa) y Proceso (con una transición seleccionada), en claro y oscuro (`H3 CA3`) |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 crear y quitar el layout | `H1 CA1 · CA2 · CA5 · CA6 · H3 CA1` (crear con un clic, una sección con todos los campos); «Quitar layout» deja `layoutJson` vacío → `setLayout null` (camino de la 007) |
| H1 CA2 arrastrar y soltar | `H1 CA1 …` (campo a otra sección, antes de otro campo, a «Sin colocar»; sección arrastrada por su asa) con `dragTo` nativo, fuente y destino verificados visibles |
| H1 CA3 solo teclado | `H1 CA3 · CA4 · CA5` (Espacio toma, flechas cruzan secciones, Espacio suelta, Esc regresa, Supr quita; región en vivo; el foco sigue al campo; diálogo de opciones con Esc; botones de sección) |
| H1 CA4 propiedades | `H1 CA3 · CA4 · CA5` (columnas, condición de sección y de campo, ancho completo, pestañas: organizar, agregar, renombrar con nombre repetido rechazado, mover sección a otra pestaña) |
| H1 CA5 vista previa en vivo | `H1 CA1 …` y `H1 CA3 …` (títulos, orden, «Otros datos», ancho completo, marca «Condicional», pestañas navegables); el formulario real usa el layout aplicado |
| H1 CA6 mismo `setLayout`, sin cambios falsos | `H1 CA1 …` y `H1 CA3 …` comparan el plan enviado a `/plan/preview` con el esperado; `H1 CA6` (mover y regresar, condición puesta y quitada → «Sin cambios») |
| H1 CA7 JSON alterno | `H1 CA7 · CA8` (JSON inválido → el diseñador lo explica y ofrece corregir) |
| H1 CA8 campos que ya no existen | `H1 CA7 · CA8` (campo escrito a mano y campo archivado en la pestaña Catálogo; se quitan con un clic; el plan es `archiveField` + `setLayout`) |
| H2 CA1 diagrama | `H2 CA1 · CA3` (nombres accesibles con inicial, editable y salidas; solo automatización; capas de izquierda a derecha; etiquetas sin tapar estados) |
| H2 CA2 editar y conectar | `H2 CA2 · CA5` (agregar estado con identificador derivado, color y editable; arrastrar de un estado a otro; roles, condición y asignaciones) |
| H2 CA3 teclado y eliminar | `H2 CA1 · CA3` (foco y Enter), `H2 CA3 · CA4` (confirmación con las transiciones que se van) |
| H2 CA4 crear proceso, identificadores fijos | `H2 CA4` (crear con un estado), `H2 CA1 · CA3` (identificador guardado deshabilitado), `H2 CA3 · CA4` (aviso de registros en el estado; el inicial no se elimina) |
| H2 CA5 mismo `setWorkflow` | `H2 CA2 · CA5` (plan comparado y aplicado; el modelo lo refleja) |
| H3 CA1 mismo camino | Todos los escenarios pasan por `Revisar cambios` → `/plan/preview` → `Aplicar cambios` (`/plan/apply`, con `RequireDirectChange` de la 012 cubierto por `endurecimiento.spec`) |
| H3 CA2 sin permiso | `H3 CA2 · CA4` (persona con solo el rol base: «Sin acceso» por URL directa, sin pestañas) |
| H3 CA3 accesibilidad y 390 px | `H3 CA3` (axe en claro y oscuro; sin desbordamiento; el recuadro del diagrama se enfoca y se desplaza con flechas) |
| H3 CA4 partidas | `H3 CA2 · CA4` (aviso en Formulario y Proceso) |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | En la cabecera de cada sección, el título (un `Input` sin etiqueta, que no lleva envoltorio y mide 100 %) bajaba de renglón: el asa quedaba sola arriba y las columnas y botones en un tercer renglón | Corregido en CSS (`.ld-section-head > .input`, también en pestañas y asignaciones). Regresión en `H1 CA1 …`, midiendo que asa, título y botones compartan renglón (falló: *Received 39*) |
| F-02 | Baja | «Agregar sección» llevaba el foco al título, pero con el cursor al final: lo escrito quedaba como «Nueva secciónPagos…» | Corregido: el título nuevo queda seleccionado. Regresión en `H1 CA1 …` escribiendo con el teclado (falló: *Received «Nueva secciónSeguimiento»*) |
| F-03 | Media | En un proceso grande (la orden de compra: 9 estados, 9 transiciones) las etiquetas largas, centradas en su flecha, tapaban estados vecinos y se enciman entre sí (14 encimamientos medidos) | Corregido: una pasada acomoda cada etiqueta en el primer lugar libre cerca de su flecha. Regresión en `H2 CA1 · CA3` con una etiqueta larga a propósito (falló: *Received 1*) |

Además, durante la implementación (antes de la revisión): a 1280 px el estado recién agregado quedaba fuera del recuadro visible y no se podía conectar arrastrando. El diagrama ahora se reduce hasta 70 % para caber y el panel de propiedades va debajo por debajo de 1440 px; «Sin colocar» pasó a ser una franja sobre el lienzo y la vista previa (tres columnas solo desde 1600 px).

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5098` sobre `ErpAi_E2E` (preparada por una corrida completa con una contraseña de
  administrador generada para la sesión y guardada fuera del repositorio), con `RequireApproval` y `AllowDirectChanges`.
- *Visitas* a 1280 px en tema oscuro: crear layout, agregar sección y escribir un título largo con acentos y ñ
  («Pagos y ñandú con título muy largo…»): detectados F-01 y F-02.
- *Órdenes de compra* (Compras instalado): el diseñador lee el layout real con pestañas «General» y «Seguimiento»; las
  fórmulas del documento aparecen en «Sin colocar» y la vista previa las pone en «Totales» (el texto lo aclara). El
  diagrama de 9 estados se reduce y se desplaza; detectado F-03 midiendo cajas; tras la corrección, 0 encimamientos.
- Teclado en el diagrama: Tab desde el recuadro llega a «Estado Borrador, inicial, editable, 2 transiciones salen», el
  anillo de foco se dibuja (trazo de 3 px) y Enter abre sus propiedades sin robar el foco.
- 375 px: la página no se desborda (0 px); «Sin colocar», lienzo y vista previa se apilan y los chips caben.
- Consola: solo el 401 previo al inicio de sesión; sin errores de JavaScript ni de CSP.

## Riesgo residual
- El diagrama no se reacomoda a mano: con muchos estados en una capa o muchas transiciones de regreso puede quedar
  denso; las etiquetas se separan de su flecha para no encimarse.
- Eliminar un estado guardado deja a los registros que estén en él con ese valor y sin transiciones (el motor no lo
  impide; el diálogo lo advierte). Sigue igual que editando el JSON.
- La vista previa muestra estructura, no evalúa condiciones (no hay registro con qué evaluarlas): lo condicional se
  marca.
- No hay deshacer dentro del diseñador; «Cancelar» descarta todo el borrador y la vista previa del plan es la última
  compuerta.
- La lógica de edición (`layoutEdit.ts`, `workflowEdit.ts`) se prueba por E2E: el proyecto web no tiene pruebas
  unitarias de JavaScript.
