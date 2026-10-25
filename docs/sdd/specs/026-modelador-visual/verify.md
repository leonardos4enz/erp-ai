# Verify 026 — Modelador por módulos y sin JSON

## 1. Pruebas unitarias
`dotnet test`: **580 correctas** (572 de antes + 8 nuevas en `ProvenanceTests`):
- los paquetes instalados se ordenan por `requires`;
- con los cuatro paquetes reales (Compras, Inventario, Contabilidad, Costeo) cada campo lleva el paquete y el paso que lo
  dejó como está (`Almacen.CuentaInventario` → Costeo base; `Requisicion.Almacen` → una actualización de Compras) y nada
  sale modificado ni local;
- una regla cambiada en el entorno es «modificada»; una regla nueva, «local»; reordenar campos no es modificar;
- un catálogo creado en el entorno no tiene paquete; un paquete no instalado no sella nada; una versión instalada anterior
  no reproduce las siguientes;
- choques de actualizar Compras desde 1.6.0: la regla local que una lista sin `merge` quita (`removed`) y la regla
  modificada que se reescribe (`replaced`); sin cambios locales, ningún choque;
- forma canónica: nulos, listas vacías, `false` y orden de presentación no cuentan.

## 2. E2E
`npm --prefix web run e2e`: **366 pruebas, en verde dos veces seguidas** con el código final (corridas 6 y 7; ver §4).
- Nuevo `visual-modelador.spec.ts` (20 pruebas, un escenario por criterio de H1–H4, con accesibilidad automática en cada
  pantalla y capturas claro/oscuro del editor de automatizaciones y de la lista por módulos). Sus identificadores llevan un
  sufijo por corrida para poder repetirla sobre la misma base.
- Ajustados (el JSON ahora es la opción avanzada plegada de su pestaña; `openJson()` en `helpers.ts`):
  `automatizaciones`, `integraciones`, `interfaz`, `multiempresa` («Un registro por empresa» está en General),
  `programadas`, `seguridad`, `valuacion-rutas-avisos` (la ayuda de `severity` vive en el JSON de Reglas).
- Capturas de referencia regeneradas y **revisadas una por una**: `visual.spec` › `modelador-light/dark` (pestañas en dos
  renglones, General con «Empresas», sin franja vacía) y las cuatro nuevas (`automatizaciones-light/dark`, completa a 1900 px
  de alto y con la validación en vivo ya terminada; `catalogos-por-modulo-light/dark`, con lo que cambia por corrida
  enmascarado). La primera versión de `automatizaciones-*` salía cortada a la altura de la ventana: se rechazó y se rehízo.

## 3. Revisión exploratoria
Con el navegador integrado sobre una instancia de la API contra la base desechable `ErpAi_E2E` (puerto 5093, credencial de
prueba de esa corrida; la base `ErpAi` y la cuenta del dueño no se tocaron), después de la suite: Compras, Inventario,
Contabilidad y Costeo instalados y más de 200 catálogos.

### Hallazgos
| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-01 | Media | Con teclado, las flechas cambiaban de pestaña del editor pero el foco se quedaba en la anterior (la pestaña vive en la URL y el cambio llega después del cuadro siguiente; `Tabs` buscaba la seleccionada). Afectaba también a las pestañas de la 019. | `Tabs` enfoca por posición. | `visual-modelador` › «H4 CA13: con teclado…» (verificado: falla con el `ui.tsx` anterior) |
| F-02 | Media | La página de «Nuevo catálogo» medía 292 px más que su contenido: el `.sr-only` absoluto del mensaje del editor de expresiones escapaba del contenedor con scroll (lección de CLAUDE.md). | `.expr-field { position: relative }`. | `explorador` › «H8: el input oculto… ni alarga la página» la detectó en la corrida 1 y pasa con la corrección |
| F-03 | Media | Solo desplegar una automatización instalada («Póliza de entrada» de Costeo) mostraba «Lo instaló Costeo · tu cambio queda como modificación local» sin haber cambiado nada: lo guardado trae `null` que el borrador no. | La comparación canónica de las secciones ignora los nulos. | `visual-modelador` › «H2 CA3: abrir un elemento instalado sin cambiarlo no avisa…» (verificado: falla con el código anterior) |
| F-04 | Baja | Catálogos con el mismo nombre visible (p. ej. «Zonas») eran indistinguibles en los selectores de catálogo destino. | Si dos se llaman igual, se agrega el identificador. | — (cosmético; se comprobó en pantalla) |
| F-06 | Media | Tras elegir una referencia en el autocompletado, la lista que se reabre usaba el texto de ese instante: si la persona seguía tecleando («Cliente.Zona.»), mostraba sugerencias viejas («Zona» en vez de sus campos). Intermitente (depende de la velocidad). | La lista se recalcula con el texto y el cursor actuales del control. | `visual-modelador` › «H3 CA1…» la detectó al repetir solo esa prueba tras la corrida 5 |
| F-05 | Baja | En escritorio, las 9 pestañas del editor se recortaban («Vistas y reporte…») sin indicio de desplazamiento. | En escritorio bajan de renglón; en móvil se desplazan. | captura `modelador-light/dark` revisada |

### Lista de comprobación
**Flujo**
- [x] Pedido del dueño a mitad de la spec (H3 CA5): las condiciones se arman con el **constructor** sin escribir
  (campo por pasos con rutas, operador según el tipo, valor de una lista, Y/O, «es uno de», hoy ± días, quien usa el
  sistema); ida y vuelta con el texto, y lo complejo se queda en «Texto» con su aviso. Las condiciones reales de los
  paquetes (p. ej. el periodo abierto de Pólizas) se leen en renglones.
- [x] Camino feliz de cada historia sin JSON: agrupar y filtrar catálogos; ver origen; avisos de paquete; choques al
  actualizar; autocompletar y validar expresiones; reglas, automatizaciones, seguridad, vistas, reportes, webhooks, tareas y
  libro con formularios (cada uno aplicado y comprobado en la API; la automatización además se ejecutó).
- [x] Mensajes de error claros y junto al control: el analizador debajo de la expresión, el compilador en la sección, en el
  elemento y en la pestaña («(con errores)»).
- [x] Desplegar todo en seis catálogos reales instalados por paquetes (Recepción, Póliza, Orden de compra, Movimiento de
  inventario, Producto, Almacén) en todas las pestañas: **ninguna** expresión de los paquetes marcada como inválida (los
  ámbitos coinciden con el compilador: partida, registro creado, periodo), ningún aviso falso y «Sin cambios».
- [x] Recargar conserva pestaña, módulo y búsqueda; volver del editor recupera el filtro; un JSON inválido no se pierde.
- [x] Datos límite: textos largos se parten (`overflow-wrap`), identificadores solo admiten `[A-Za-z0-9_]`.

**Diseño**
- [x] Claro y oscuro; escritorio 1280 px y móvil 390 px sin desbordamiento horizontal en las 9 pestañas de tres catálogos
  con todo desplegado.
- [x] Solo iconos Lucide; estados vacíos con acción («Este catálogo no tiene reglas · Agregar regla»); ningún texto de
  ejemplo en las áreas JSON (hallazgo 3 del dueño, con prueba).

**Accesibilidad**
- [x] Todo con teclado: pestañas con flechas (F-01), desplegar con Enter, autocompletado con flechas/Enter/Tab/Esc,
  Ctrl+Espacio para pedir sugerencias.
- [x] Lector de pantalla: el editor de expresiones es `combobox` con `aria-autocomplete="list"`, `aria-expanded`,
  `aria-controls` → `listbox` y `aria-activedescendant`; el resultado de la validación y el número de sugerencias van en una
  región `aria-live` enlazada con `aria-describedby`; cada grupo de casillas es un `fieldset` con su `legend`; axe sin
  violaciones serias en cada pantalla nueva.

**Seguridad**
- [x] Sin «administrar modelo», `provenance` y `expressions/*` responden 403 (E2E); el modelador sigue tras su guardia.
- [x] Nada nuevo escribe: todo sale por `plan/preview` → `plan/apply` con `baseHash`; límites de tamaño en la validación.
- [x] Consola sin errores ni violaciones de CSP (la guardia de la suite y la revisión; solo los 401 esperados antes de
  iniciar sesión).

## 4. Resultado
| Corrida | Resultado |
|---|---|
| 1 | 335 bien, 18 mal: selectores ambiguos y estado del worker en la prueba nueva, 6 pruebas viejas que llenaban JSON y F-02 |
| 2 | 364 bien, 1 mal: carrera en la prueba de teclado (dependía de si la pestaña se volvía a montar) |
| 3 | 365 bien (código antes del constructor de condiciones) |
| 4 | detenida: llegó el pedido del constructor de condiciones y el código cambió |
| 5 | 363 bien, 3 mal: la prueba nueva esperaba texto donde ahora abre el constructor, captura cambiada y F-06 |
| 6 | **366 bien** (código final) |
| 7 | **366 bien** (código final) |

## 5. Pendientes (fuera de alcance, con razón en la spec)
- Origen de lo que llegó por paquetes de Entornos (009): hoy se ve como «Local», y la pantalla lo explica.
- Fusión de tres vías al actualizar un módulo: gana la actualización, y su vista previa lista los choques.
- Agrupar reportes por rutas de más de un salto y la etiqueta de un filtro de vista: en «Ver como JSON» (dicho en pantalla).
- `lookupFilter` de un campo: se muestra con el aviso de que se cambia por paquete o con el asistente.
- Deshacer y rehacer dentro de los editores; quitar un elemento con `merge`.
