# Verify 025 — Explorador del modelo y de la base de datos

## 1. Pruebas unitarias
`dotnet test`: **572 correctas** (30 nuevas en `ExplorerTests.cs`): tipos SQL exactos y su coincidencia con
`DdlGenerator.ColumnType`; lista blanca de esquemas (`meta` solo superusuario; `dbo` o texto con inyección rechazados);
deriva (tabla sin catálogo, catálogo archivado, tabla faltante, campo archivado, columna sin campo, tipo distinto, columnas de
sistema faltantes; sin falsos positivos por partidas ni `CompanyId`); cardinalidad por nulabilidad y unicidad; filtros por
módulo y texto con tablas externas; DDL (identidad, `DEFAULT` con nombre, PK, índice filtrado, `INCLUDE`, llaves foráneas al
final, nombres escapados como `QUOTENAME`, sin `INSERT`/`SELECT`); ¿dónde se usa? (ruta de tres saltos de Inventario 1.7.0,
`SUM`, `UNITCOST`, automatizaciones con `target`/`lines`/`capture`, reglas, transiciones, layout, vistas, reportes, tableros,
webhooks, tareas, seguridad, `lookupFilter`, libro, `displayField`; expresión inválida; caché por instancia del modelo);
flujo con modos y ciclos (Tarjan); matriz con herencia, alcance, ocultos y solo lectura; árbol de navegación; paquetes que
crean o modifican un catálogo.

## 2. E2E
`explorador.spec.ts` (19 escenarios, uno o más por criterio): permisos (H7), tipos exactos y llaves contra SQL Server real
(H1 CA1/2/5), pata de gallo (CA3), deriva provocada con `sqlcmd` directo en la base desechable (CA4/CA2), exportar SVG, PNG y
DDL (CA7), 105 tablas sin traslapes, foco a 1–2 niveles, zoom y búsqueda en el servidor (CA8), teclado (CA9), relaciones
(H2), ficha (H3), usos (H4), flujo (H5), seguridad y navegación con teclado (H6), móvil 390 px y oscuro (H8), axe en cada
vista y capturas visuales del diagrama ER en claro y oscuro (revisadas antes de aprobarlas).

Pruebas existentes ajustadas por cambios intencionales: `navegacion.spec.ts` (el grupo *Modelo* ahora incluye «Explorador») y
las capturas `configuracion-*` (tarjeta nueva) y `modelador-*` (ya sin la franja vacía de F-01), revisadas una por una.

## 3. Revisión exploratoria
Navegador integrado contra una instancia aislada (base desechable `ErpAi_Revision_E2E`, puerto 5095, contraseña de prueba
generada en la sesión) con Compras, Inventario, Contabilidad y Costeo instalados, el módulo de prueba y 105 tablas más.

| ID | Sev. | Hallazgo | Corrección | Prueba |
|---|---|---|---|---|
| F-01 | Baja | El `input` oculto de los interruptores (`.switch`) era absoluto sin `position: relative` en su etiqueta: tomaba como contenedor a un ancestro lejano y alargaba el documento (la captura aprobada del modelador tenía una franja vacía de 74 px al pie: 874 px de alto en una ventana de 800). Es la lección de CLAUDE.md sobre absolutos que escapan. Un primer intento que además fijaba `top/left` al input lo dejó bajo la pista y rompió `multiempresa.spec` (se revirtió esa parte). | `.switch { position: relative }` (global); la captura del modelador se actualizó a 800 px tras revisarla | `H8: el input oculto de cada interruptor no escapa de su etiqueta ni alarga la página` |
| F-02 | Media | A menos de ~1280 px el panel de detalle dejaba el diagrama ilegible (23 %), y el ajuste inicial reducía también por altura. | El panel pasa abajo bajo 1280 px; el ajuste prioriza el ancho y no baja del 60 % por altura | Captura visual (1280 px) y móvil sin desborde |
| F-03 | Media | En Automatizaciones los colores por módulo no coincidían con los de Relaciones (otro orden) y el subtítulo mostraba el nombre interno (`ExpVentas`). | El servidor envía los módulos (`FlowGraph.apps`) y la vista usa los mismos colores y etiquetas, con leyenda | `H5 …` (leyenda con «Exploración», sin el nombre interno) |
| F-04 | Baja | Matriz de seguridad: los encabezados de fila heredaban el estilo en mayúsculas de las columnas, el módulo salía con su nombre interno y se contaban «ocultos» en roles que no leen el catálogo. | Estilo propio, `appLabel` desde el servidor y extras solo con lectura | `H6 …` |
| F-05 | Media | Al llegar a Base de datos con una tabla elegida (desde la ficha o Relaciones) se veían las 131 tablas al 20 % y la elegida quedaba perdida. | Con catálogo en la URL, el foco empieza activo | `H1 CA8 …` (llega enfocado: 4 tablas) |
| F-06 | Media | Petición del dueño durante la revisión: en los diagramas se encimaban etiquetas y las flechas se enredaban. | Distribución jerárquica (Sugiyama): nodos virtuales para que las flechas largas pasen por carriles entre las cajas, barrido de baricentros con transposición, puertos repartidos en la fila destino, acciones paralelas agrupadas («3 acciones»), etiquetas acomodadas cerca de su flecha y encima de todo, y resaltado de las relaciones de la caja elegida o señalada (lo demás se atenúa) | `H1 CA8` (105 cajas sin traslapes) y capturas visuales |
| F-07 | Baja | Con flechas rápidas el foco del teclado se perdía: se movía en un cuadro de animación posterior. | El foco se mueve en el mismo evento | `H1 CA9 …` |

### Lista de comprobación
- [x] Camino feliz de cada historia sin conocimiento técnico (cada vista explica qué muestra y cómo usarla).
- [x] Mensajes de error claros: 400 por esquema o texto inválido, 403 «Solo un superusuario ve las tablas del sistema».
- [x] Atrás/recargar: vista, catálogo y campo viajan en la URL.
- [x] Claro y oscuro, 1280 px y 375/390 px sin desborde horizontal; solo iconos Lucide.
- [x] Estados vacíos, de carga y de error con diseño.
- [x] Teclado: pestañas con flechas, una parada de tabulación por diagrama con flechas/Inicio/Fin/Enter, `+ − 0` para zoom,
      tablas alternativas con botones, árbol con `details`/`summary`.
- [x] Lector de pantalla: cada caja con nombre completo (tabla, catálogo, módulo, columnas, llaves, deriva), cardinalidad en
      texto, tablas con `caption`, conteos en regiones vivas; los diagramas de proceso son imágenes con su lista de transiciones.
- [x] Sin permiso: la opción no aparece, la ruta muestra «Sin acceso» y la API responde 403; `meta` solo superusuario.
- [x] Consola sin errores ni violaciones de CSP (exportar PNG usa una imagen `data:`).

### Regresiones comprobadas contra el código anterior
- F-01: con el CSS anterior, los 5 interruptores del modelador tienen un contenedor distinto de su etiqueta y el documento
  mide 874 px en una ventana de 800 (medido en el navegador); con la corrección, 0 y 800.
- F-03, F-04 y F-05: las aserciones nuevas fallan con la versión anterior (no había leyenda en Automatizaciones, la matriz
  mostraba `ExpVentas`, el foco empezaba apagado).
- F-07: `H1 CA9` falló con el foco en un cuadro posterior (corrida 3 de la sesión) y pasa con el foco síncrono.

## 4. Resultado
- `dotnet test`: 572 correctas, dos veces.
- `npm --prefix web run e2e`: 347 correctas, dos corridas completas seguidas sobre la base desechable.
- Revisión exploratoria completa (§3), sin hallazgos abiertos.

## 5. Pendientes (fuera de alcance)
Comparar la base de dos entornos; sugerir limpieza de columnas archivadas con respaldo y aprobación; las flechas que suben
de columna en un ciclo (hoy el compilador los impide) se dibujan por fuera sin carril.
