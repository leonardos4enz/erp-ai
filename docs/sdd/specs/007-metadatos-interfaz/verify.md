# Verificación 007 — Metadatos de interfaz: layouts, vistas, reportes y tableros

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 153/153 (+31 en `InterfaceTests`: validación de layouts, vistas, reportes y tableros; filtros a SQL parametrizado; campo oculto → 400; reporte no disponible por campo oculto o rol; condición que lee un campo oculto; prefijo de parámetros; CSV sin inyección de fórmulas; XLSX válido sin fórmulas; Compras e Inventario compilan con todas sus actualizaciones) |
| E2E | 108/108 en dos corridas limpias seguidas (+15 en `interfaz.spec.ts`; se ajustaron dos pasos de `compras.spec.ts` por Compras 1.4.0: el tipo de cambio ya no se muestra en pesos y la autorización está en la pestaña «Seguimiento») |
| Visual | Capturas de referencia sin cambios (la sección «Análisis» va al final del menú, fuera del área capturada); no se aprobó ninguna nueva |
| Actualización real | Base `ErpAi`: Compras **1.3.0 → 1.4.0** e Inventario **1.1.0 → 1.2.0** desde la UI, con vista previa válida (8 y 4 pasos) |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 · CA2 · CA3 — secciones, pestañas, condición en vivo, obligatorio oculto no se exige, la pestaña con error se abre sola | `interfaz › H1 CA1 · CA2 · CA3`, `H1 CA2 · CA4` |
| H1 CA4 — condición que lee un campo oculto se ignora; campo oculto por 006 no aparece aunque el layout lo mencione | `interfaz › H1 CA1…`, `H1 CA2 · CA4`; unitaria `Condicion_que_lee_un_campo_oculto_se_detecta` |
| H1 CA5 — vista previa rechaza layouts inválidos | `interfaz › H1 CA5`; unitarias `Layout_*` |
| H2 CA1 · CA2 · CA3 — vista predeterminada, filtros, columnas, vista personal (guardar, reabrir, eliminar) | `interfaz › H2 CA1 · CA2 · CA3` |
| H2 CA4 — filtro por campo oculto 400, vistas compartidas por rol y sin campos ocultos, personales privadas (404), auditoría | `interfaz › H2 CA4`; unitarias `Filtr*` |
| H3 CA1 · CA2 · CA3 — agrupaciones (propias, relacionadas, periodo, partidas), totales, alcance por registro, «sin acceso», campo oculto → 404 | `interfaz › H3 CA1 · CA2 · CA3`; unitarias `Reporte*` |
| H3 CA4 · CA6 — gráfica con alternativa textual, tabla con total, filtros, descarga | `interfaz › H3 CA4 · CA6`, `H3 CA4: en móvil…` |
| H3 CA5 — CSV (BOM, sin inyección) y XLSX | `interfaz › H3 CA5`; unitarias `Csv_*`, `Xlsx_*` |
| H4 CA1 · CA2 · CA3 — tableros por usuario, tarjetas filtradas, menú «Análisis», móvil | `interfaz › H4` |
| H5 · CA1 — JSON de layout, vistas, reportes y tableros validado; F-03 | `interfaz › H5`, `H5 CA1 (F-03)` |
| H6 CA1–CA4 — Compras 1.4.0 e Inventario 1.2.0 | `interfaz › H6 CA1 · CA2`, `H6 CA3 · CA4`; unitaria `Compras_e_Inventario_compilan…`; manual en `ErpAi` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | El traductor de reglas `where` (spec 006) no aceptaba una fórmula sí/no como condición («Bajo mínimo»); un filtro fijo de reporte o una regla de acceso sobre ella se rechazaba | Corregido en `RecordFilter`; regresión `Formula_si_no_es_condicion_en_filtros_fijos` y `Compras_e_Inventario_compilan…`, **verificadas fallando con el código anterior** |
| F-02 | Alta | Las pestañas inactivas del formulario seguían a la vista: `display: flex` anulaba el atributo `hidden` | Corregido (`.form-sections[hidden]`); regresión en `interfaz › H1 CA1…`, **verificada fallando con el código anterior** |
| F-03 | Media | En móvil los botones del reporte (Filtros, CSV, Excel) tapaban un título con palabras largas: el título se encogía a ~0 px | Corregido (los botones pasan a su propia fila); regresión `interfaz › H3 CA4: en móvil…`, **verificada fallando con el código anterior** |
| F-04 | Baja | Un indicador de suma sin registros mostraba «—» («Monto comprado» en una base sin órdenes autorizadas) | Corregido: suma y conteo sin registros dan 0; regresión en `interfaz › H3 CA1…`, **verificada fallando con el código anterior** |
| F-05 | Media | La sección «Análisis» empujaba «Configuración» fuera de la pantalla (lo detectó la regresión de la revisión 003 en `compras.spec`) | Corregido: «Análisis» va al final del menú; la prueba existente falla con el orden anterior (primera corrida) |
| F-06 | Baja | Con la moneda aún vacía (orden nueva sin proveedor), «Tipo de cambio» aparecía porque `NULL <> 'MXN'` es verdadero en el lenguaje | Corregido en Compras 1.4.0 antes de publicarlo: `NOT ISBLANK(Moneda) AND Moneda.Clave <> 'MXN'`; cubierto por `compras › CA2…` |
| F-07 | Baja | Cada sección del formulario era una región con nombre: ruido para el lector de pantalla y su nombre («Fechas») competía con la etiqueta de un campo («Fecha») | Corregido: la sección se anuncia por su encabezado `h2`; cubierto por `compras › Spec 004 · H1` |
| F-03 (006) | Baja | El modelador ofrecía «Revisar cambios» sin cambios con `displayOrder` no consecutivo | **Resuelto**: solo se renumera si cambió el orden; `interfaz › H5` y `H5 CA1 (F-03)`, **verificadas fallando con el código anterior**; comprobado en *Orden de compra* de `ErpAi` («Sin cambios») |

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- Módulos: «Actualizar Compras» describe los 8 pasos (layouts, vistas, reportes y el tablero con 6 tarjetas); «Actualizar Inventario», 4 pasos. Ambos aplicados; tarjetas en v1.4.0 y v1.2.0.
- Orden de compra nueva: pestañas «General» y «Seguimiento», secciones «Datos de la orden» y «Condiciones de pago»; sin tipo de cambio en pesos.
- Tablero «Compras» en claro y oscuro: indicadores, pastel de requisiciones por estado y estados vacíos con texto («Sin datos todavía»).
- Reporte «Requisiciones por estado» en 390 px: sin desbordamiento; ahí se encontró F-03.
- Consola sin errores ni violaciones de CSP. No se modificó al usuario `admin`; los roles distintos se revisaron con usuarios propios en `ErpAi_E2E`.

## Riesgo residual
- Las agrupaciones por fecha-hora usan la hora UTC guardada (un registro de las 23:30 de México cuenta en el día siguiente).
- Un reporte sobre partidas no puede filtrar por campos del documento (el filtro fijo solo usa columnas propias); «Productos más comprados» incluye órdenes canceladas y lo dice en su descripción.
- La visibilidad condicional es presentación: el valor de un campo oculto por condición se conserva y se guarda; para ocultar datos se usa la seguridad de la 006.
- La exportación se limita a lo que muestra el reporte (máx. 1000 grupos); los listados no se exportan todavía.
