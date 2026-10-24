# Verificación 020 — Contabilidad y costeo

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 458/458 (+30 en `AccountingTests.cs`: `CostMath`, libro, acción `cost`, `create` con partidas, `UNITCOST`, módulos y diff de entornos) |
| E2E | 271/271 en dos corridas completas limpias seguidas (+18 en `valuacion-contable.spec.ts`, que corre después de Inventario y antes de las capturas; ajustado `inventario.spec.ts` H7: la versión 1.5.0 vuelve a declarar la entrada al almacén y el diálogo de instalación muestra dos pasos parecidos) |
| Contra el código anterior | Las tres regresiones se vieron fallar revirtiendo la corrección en el árbol de trabajo y pasar al restaurarla: F-01 (*element(s) not found*: el diálogo solo decía «Hay datos inválidos.»), F-02 (*Received: 0*), F-03 (31 recortes, de «Contabilidad @900px» a «Inventario @1660px») |
| Visual | Sin cambios en las referencias: una corrida completa con `--update-snapshots` no reescribió ninguna. El grupo *Contabilidad* queda debajo del área capturada del menú y el JSON del libro vive en la sección avanzada, cerrada en un catálogo nuevo |
| Accesibilidad | axe sin violaciones serias en la póliza con el diálogo de error abierto, el periodo cerrado, la póliza automática y el catálogo de cuentas |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 `setLedger` | Contabilidad lo declara (`H4 CA1 …`: el diálogo de instalación muestra el paso); unitarias de compilación y diff |
| H1 CA2 cuadre por cualquier vía | `H1 CA2: … la UI dice por cuánto` (UI), `H1 CA2: cada partida…` (API: ambos, una sola, negativos), `H3 CA2` (automatización) |
| H1 CA3 periodo abierto, antes y después | `H1 CA3` (sin periodo, cerrado al aplicar, al cancelar por UI y API; borrador editable; reabrir solo administrador con comentario) |
| H1 CA4 sin traslapes | `H1 CA4` |
| H1 CA5 libro inválido | `H1 CA5 · H2 CA6 · H3 CA3` (vista previa por API) y la teoría unitaria `Un_libro_mal_declarado…` (8 casos), archivar un campo del libro, libro compartido con periodos por empresa |
| H1 CA6 cierre concurrente | `H1 CA6` (tres pólizas y el cierre a la vez: cada póliza queda aplicada antes del cierre o rechazada por él; sin interbloqueos) |
| H2 CA1 promedio y FIFO | `H2 CA1 · CA2`, `H2 CA1` y unitarias de `CostMath` |
| H2 CA2 costo en el movimiento y valor en el saldo | `H2 CA1 · CA2` (costo total, costo unitario, valor y costo promedio de la existencia) |
| H2 CA3 sin costo / sin capas | `H2 CA3` (ajuste sin costo al promedio vigente; salida de existencias previas al costo último sin dejar valor negativo) |
| H2 CA4 `UNITCOST` | `H2 CA4 · H5 CA1` (traspaso FIFO: entrada y salida a 13.00) y unitarias del analizador y evaluador |
| H2 CA5 por empresa | `H2 CA5` (otra empresa con su almacén: su salida usa sus capas, la principal no cambia) y unitaria de la llave |
| H2 CA6 valuación inválida | `H1 CA5 · H2 CA6 · H3 CA3` y la teoría unitaria (7 casos) |
| H3 CA1 `createLines` y `transition` | `H3 · H5 CA1 · CA3` (póliza de dos partidas creada y aplicada por la recepción, historial «Automática: …») |
| H3 CA2 todo o nada | `H3 CA2` (periodo cerrado: la recepción sigue en borrador, sin entrada al almacén ni póliza) |
| H3 CA3 partidas inválidas | `H1 CA5 · H2 CA6 · H3 CA3` y `Una_poliza_automatica_mal_declarada…` |
| H4 CA1 cuentas y catálogo inicial | `H4 CA1 …` (9 cuentas en la empresa de quien instala, naturaleza por defecto) |
| H4 CA2 pólizas | `H1 CA2` (folio, origen Manual, totales), `H3 · H5 …` (origen Automática, referencia) |
| H4 CA3 saldos | `H1 CA2` (aplicar) y `H4 CA3` (cancelar con comentario) |
| H4 CA4 periodos | `H1 CA3` (cerrar desde la UI; *Reabrir* deshabilitado para el contador, 403 por API) |
| H4 CA5 balanza y tarea | `H4 CA5` (reporte por tipo; tarea en pausa con su rol) |
| H4 CA6 roles | Todas las pruebas del contador; `Cierre contable` en `H4 CA5` |
| H5 CA1 costo de entrada | `H3 · H5 CA1 · CA3` (orden en euros a 20; el euro sube a 21 antes de recibir: costo 42 = 2 × 21, no 40), `H2 CA4 · H5 CA1`; unitaria de la actualización 1.4 → 1.5 |
| H5 CA2 método y valuación | `preparar …` (Promedio por defecto), `H2 …` |
| H5 CA3 póliza de recepción | `H3 · H5 CA1 · CA3` (cargo a 1200 y abono a 2150 por 210) y `H5 CA3` (almacén sin cuentas: sin póliza) |
| H5 CA4 requisitos | `H4 CA1 …` («Requiere Contabilidad», botón deshabilitado) y `Costeo_sin_Contabilidad_no_compila` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | Al aplicar una póliza descuadrada (o cualquier transición rechazada por una regla del documento) el diálogo solo decía «Hay datos inválidos.»: los mensajes venían en campos que el diálogo no muestra. Lo encontró la primera corrida de `H1 CA2` | Corregido en `TransitionDialog` (genérico): los mensajes de campos que no están en el diálogo se muestran en la confirmación y en el aviso del diálogo con comentario. Regresión en `H1 CA2` y `H1 CA3` (cancelar por UI) |
| F-02 | Baja | Una existencia anterior al costeo mostraba «Costo promedio 0.00» con el valor vacío, como si no costara nada | Corregido en Costeo: sin valor, el costo promedio queda vacío. Regresión en `H2 CA3` |
| F-03 | Baja | En *Módulos*, «Contabilidad» no cabía junto a «Instalado» y pasaba por debajo de la insignia (también «Compras» e «Inventario» a ciertos anchos) | Corregido en CSS: la insignia baja de renglón. Regresión en `H4 CA1 …` recorriendo anchos de 900 a 1700 px |

Además, durante la implementación: el botón de cancelar una póliza se llamaba «Cancelar», igual que el botón que cierra el diálogo; ahora es «Cancelar póliza».

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5098` sobre `ErpAi_E2E`, preparada por una corrida completa con una contraseña de
  administrador generada para la sesión (guardada fuera del repositorio), con `RequireApproval` y `AllowDirectChanges`.
- *Contador*, tema oscuro, 1280 px: póliza nueva solo con teclado (buscador de cuenta con flechas y Enter, Tab entre
  celdas), concepto largo con acentos, ñ y raya; totales y diferencia en vivo; aplicarla descuadrada muestra
  «No cuadra: cargos 500.50 y abonos 400.00 (diferencia 100.50)…» en el diálogo (tras F-01). El diálogo enfoca
  «Cancelar» y se cierra con Esc.
- 390 px: la póliza no desborda la página (0 px); las partidas se desplazan dentro de su tabla, como en el resto de la app.
- *Supervisor*: existencias con valor y costo promedio (detectado F-02); movimientos con su costo; el movimiento nuevo
  ofrece *Costo unitario* también en salidas (ver riesgo residual).
- *Administrador*: el JSON del libro aparece en la sección avanzada de *Pólizas* sin cambios falsos («Sin cambios»); un libro
  con cargo de texto y un estado inexistente se rechaza en la vista previa con los dos mensajes. *Módulos* en tema claro:
  detectado F-03.
- Consola: solo los 401 previos al inicio de sesión y el 400 esperado de la póliza descuadrada; sin errores de JavaScript
  ni de CSP.

## Riesgo residual
- Dos partidas del mismo producto en un traspaso FIFO leen las mismas capas para valuar la entrada; la entrada se valúa a
  4 decimales y puede diferir centavos de la salida.
- En una salida el costo unitario lo calcula el costeo: lo que se capture ahí se reemplaza (el formulario lo ofrece igual
  que en una entrada).
- Las listas muestran los campos decimales sin ceros a la derecha («1,051» junto a «1,051.00» de una fórmula). Es el
  formato general de la app; queda como mejora aparte.
- El encabezado de un registro muestra el folio o `#id`, no el campo para mostrar («Periodo contable #1»).
- Póliza de salidas (costo de ventas), revaluación, pólizas en moneda extranjera y reversas con otra póliza: fuera de alcance.
- Las capas no se reconstruyen si se borra un movimiento por fuera del motor (los movimientos no se editan ni se eliminan).
